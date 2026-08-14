using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Client;
using Kanban.Contracts.Dtos;
using Kanban.Core.Data;
using Kanban.Core.Models;
using Kanban.Core.Services;
using Kanban.Collector.Core.Localization;
using MainAPP.Resources;
using MainAPP.Services;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace MainAPP.ViewModels;

/// <summary>
/// 配方管理页 VM（独立导航页，全局配方库）。
/// 配方按机型归属（空机型=通用）；下发目标通过页面内设备下拉选择任意设备。
/// 下发执行：Local 模式用 RecipeApplier 直接写 PLC（写后读回校验+失败回滚）；Remote 模式经 SignalR 由 Collector 执行。
/// </summary>
public partial class RecipeManagerViewModel : ObservableObject, IDisposable
{
    /// <summary>"全部"筛选胶囊的哨兵值（与"通用"机型的空字符串区分）。</summary>
    public const string AllMachineTypesFilter = "__ALL__";

    /// <summary>Remote 下发等待上限（SignalR 请求-响应无法中途取消，超时自动终止等待）。</summary>
    private static readonly TimeSpan RemoteApplyTimeout = TimeSpan.FromSeconds(90);

    private readonly IRecipeStore _recipeStore;
    private readonly RecipeApplier _recipeApplier;
    private readonly KanbanDataClient _client;
    private readonly IRuntimeMode _runtimeMode;
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDialogService _dialog;
    private readonly RecipeJsonIOService _recipeJsonIo;
    private readonly ILogger<RecipeManagerViewModel> _logger;
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;
    private readonly ListCollectionView _filteredView;

    public RecipeManagerViewModel(
        IRecipeStore recipeStore,
        RecipeApplier recipeApplier,
        KanbanDataClient client,
        IRuntimeMode runtimeMode,
        IDeviceRepository deviceRepository,
        IDialogService dialog,
        RecipeJsonIOService recipeJsonIo,
        ILogger<RecipeManagerViewModel> logger)
    {
        _recipeStore = recipeStore;
        _recipeApplier = recipeApplier;
        _client = client;
        _runtimeMode = runtimeMode;
        _deviceRepository = deviceRepository;
        _dialog = dialog;
        _recipeJsonIo = recipeJsonIo;
        _logger = logger;

        // 单一数据源：卡片列表 = AvailableRecipes 的过滤视图（ListCollectionView 在源集合变更时自动重估 Filter），
        // 不再维护第二份手工同步的拷贝集合（消除索引漂移风险）。
        _filteredView = new ListCollectionView(AvailableRecipes) { Filter = FilterPredicate };

        RefreshRecipes();
        RefreshDevices();
        // 配方库集合变更（Remote 同步/其他端写入）→ 刷新列表；保存中的内部刷新由 _isSaving 屏蔽
        _recipeStore.Recipes.CollectionChanged += OnStoreRecipesChanged;
    }

    /// <summary>正在保存（SaveRecipeAsync 内部会 Upsert 触发 CollectionChanged，需屏蔽其导致的中间态刷新）。</summary>
    private bool _isSaving;

    private void OnStoreRecipesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isSaving) return;
        RefreshRecipes();
    }

    /// <summary>全部配方列表（含通用 + 各机型）——展示列表的单一数据源。</summary>
    public ObservableCollection<Recipe> AvailableRecipes { get; } = new();

    /// <summary>按搜索词 + 机型筛选后的展示视图（卡片列表绑定源）。</summary>
    public ICollectionView FilteredRecipes => _filteredView;

    /// <summary>机型筛选胶囊（全部 + 各机型，含计数与选中态）。</summary>
    public ObservableCollection<MachineTypeFilterOption> MachineTypeFilters { get; } = new();

    /// <summary>当前筛选结果数（空态判断用；ICollectionView 无 Count 变更通知，由 VM 维护）。</summary>
    [ObservableProperty] private int _filteredCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteRecipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecipeCommand))]
    private Recipe? _selectedRecipe;

    /// <summary>配方卡片搜索关键词（匹配配方名或参数名）。</summary>
    [ObservableProperty] private string _recipeSearchText = "";

    /// <summary>当前机型筛选（<see cref="AllMachineTypesFilter"/> = 全部，空字符串 = 通用机型）。</summary>
    [ObservableProperty] private string _selectedMachineFilter = AllMachineTypesFilter;

    partial void OnRecipeSearchTextChanged(string value) => ApplyFilters();

    partial void OnSelectedMachineFilterChanged(string value)
    {
        foreach (var f in MachineTypeFilters) f.IsSelected = f.MachineType == value;
        ApplyFilters();
    }

    /// <summary>点击机型筛选胶囊（参数为胶囊项本身）。</summary>
    [RelayCommand]
    private void SelectMachineFilter(MachineTypeFilterOption? option)
    {
        if (option is null) return;
        SelectedMachineFilter = option.MachineType;
    }

    /// <summary>可下发目标设备列表（独立页下拉选择）。</summary>
    public ObservableCollection<Device> TargetDevices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecipeCommand))]
    private Device? _selectedTargetDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelApplyCommand))]
    private bool _isApplying;

    /// <summary>下发中的取消令牌（Local 模式可手动取消；Remote 模式仅作超时链接用）。</summary>
    private CancellationTokenSource? _applyCts;

    /// <summary>下发逐项结果（写/校验每项成败明细，进度回调实时填充，单一通道）。</summary>
    public ObservableCollection<RecipeItemResultDto> ApplyItemResults { get; } = new();

    /// <summary>是否 Remote 模式（XAML 控制取消下发按钮可见性）。</summary>
    public bool IsRemote => _runtimeMode.IsRemote;

    // ──────────── 编辑区（内联表单） ────────────

    /// <summary>正在编辑的配方（null = 无编辑/新建初始）。</summary>
    private Recipe? _editingRecipe;

    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editMachineType = "";
    [ObservableProperty] private string _editRemark = "";

    /// <summary>编辑中的参数项（行包装：转发表单属性 + 行内实时校验）。</summary>
    public ObservableCollection<RecipeEditItemRow> EditItems { get; } = new();

    /// <summary>下发结果状态文本（颜色由 <see cref="ApplyStatusKind"/> 控制）。</summary>
    [ObservableProperty] private string _applyStatus = "";

    /// <summary>下发状态类别（Success/Error/None 控制状态文本颜色）。</summary>
    [ObservableProperty] private ApplyStatusKind _applyStatusKind;

    /// <summary>PLC 数据类型选项（参数项"类型"列下拉）。</summary>
    public Array PlcDataTypes => Enum.GetValues<Kanban.Contracts.Enums.PlcDataType>();

    /// <summary>Bool 参数值可选形式（Value 列 Bool 类型下拉引导，兼容 TryConvert 全部可解析形式）。</summary>
    public string[] BoolOptions => new[] { "True", "False", "0", "1" };

    /// <summary>从配方库加载全部配方。</summary>
    private void RefreshRecipes()
    {
        var current = SelectedRecipe?.Id;
        AvailableRecipes.Clear();
        foreach (var r in _recipeStore.Recipes) AvailableRecipes.Add(r);
        ApplyFilters();
        SelectedRecipe = AvailableRecipes.FirstOrDefault(r => r.Id == current) ?? AvailableRecipes.FirstOrDefault();
    }

    /// <summary>
    /// 重建机型筛选胶囊 + 应用搜索/机型过滤（过滤视图 Refresh + 计数同步）。
    /// 机型胶囊计数按配方库本身统计（通用排前，其余按名称）；选中态回填。
    /// 机型筛选语义对齐 <see cref="RecipeStore.GetByMachineType"/>：选具体机型时同时显示通用配方
    /// （空机型配方对任意机型可用），保证"筛选结果 = 可下发集合"一致。
    /// </summary>
    private void ApplyFilters()
    {
        // ── 机型胶囊（计数随配方库变化重建，选中态回填） ──
        var counts = AvailableRecipes.GroupBy(r => r.MachineType)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var types = counts.Keys.OrderBy(t => string.IsNullOrEmpty(t) ? 0 : 1)
            .ThenBy(t => t, StringComparer.CurrentCulture).ToList();
        MachineTypeFilters.Clear();
        MachineTypeFilters.Add(new MachineTypeFilterOption(AllMachineTypesFilter, Strings.K004, AvailableRecipes.Count) { IsSelected = SelectedMachineFilter == AllMachineTypesFilter });
        if (counts.TryGetValue("", out var generalCount))
        {
            MachineTypeFilters.Add(new MachineTypeFilterOption("", Strings.K698, generalCount) { IsSelected = SelectedMachineFilter == "" });
        }
        foreach (var t in types)
        {
            if (t == "") continue;
            MachineTypeFilters.Add(new MachineTypeFilterOption(t, t, counts[t]) { IsSelected = t == SelectedMachineFilter });
        }

        // ── 搜索 + 机型过滤（视图 Refresh 重估 Filter 委托） ──
        _filteredView.Refresh();
        FilteredCount = FilteredRecipes.Cast<Recipe>().Count();
    }

    /// <summary>过滤委托：搜索词（名/参数名）+ 机型（具体机型含通用配方）。</summary>
    private bool FilterPredicate(object o)
    {
        if (o is not Recipe r) return false;
        if (SelectedMachineFilter != AllMachineTypesFilter)
        {
            // 语义对齐 GetByMachineType：空机型（通用）对任意机型可用
            if (!string.IsNullOrEmpty(r.MachineType)
                && !string.Equals(r.MachineType, SelectedMachineFilter, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        var kw = RecipeSearchText?.Trim();
        if (!string.IsNullOrEmpty(kw))
        {
            var hit = r.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
                || r.Items.Any(i => i.ParamName.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (!hit) return false;
        }
        return true;
    }

    /// <summary>刷新可下发目标设备列表（保持当前选中；无设备时清空）。</summary>
    private void RefreshDevices()
    {
        var currentId = SelectedTargetDevice?.Id;
        TargetDevices.Clear();
        foreach (var d in _deviceRepository.GetDevicesSnapshot()) TargetDevices.Add(d);
        SelectedTargetDevice = TargetDevices.FirstOrDefault(d => d.Id == currentId) ?? TargetDevices.FirstOrDefault();
    }

    partial void OnSelectedRecipeChanged(Recipe? value)
    {
        if (value is null)
        {
            _editingRecipe = null;
            EditName = "";
            EditMachineType = "";
            EditRemark = "";
            EditItems.Clear();
            return;
        }
        LoadIntoEditor(value);
    }

    private void LoadIntoEditor(Recipe recipe)
    {
        _editingRecipe = recipe;
        EditName = recipe.Name;
        EditMachineType = recipe.MachineType;
        EditRemark = recipe.Remark;
        EditItems.Clear();
        // 深拷贝填充编辑区：行编辑（UpdateSourceTrigger=PropertyChanged）只改副本，
        // 未保存时不会污染配方库内存对象
        foreach (var item in recipe.Items) EditItems.Add(new RecipeEditItemRow(item.Clone()));
        ApplyStatus = "";
        ApplyStatusKind = ApplyStatusKind.None;
    }

    /// <summary>新建配方：清空编辑区并预置一个参数项。</summary>
    [RelayCommand]
    private void NewRecipe()
    {
        // 先清选中：SelectedRecipe = null 会触发 OnSelectedRecipeChanged 清空编辑区，
        // 因此预置参数项必须在清选中之后再添加，否则会被清掉
        SelectedRecipe = null;
        _editingRecipe = null;
        EditName = "";
        EditMachineType = SelectedTargetDevice?.MachineType ?? "";
        EditRemark = "";
        EditItems.Clear();
        EditItems.Add(new RecipeEditItemRow(new RecipeItem { DataType = Kanban.Contracts.Enums.PlcDataType.Int32 }));
        ApplyStatus = "";
        ApplyStatusKind = ApplyStatusKind.None;
    }

    /// <summary>保存当前编辑配方（校验 → Upsert → 落盘 → 刷新列表）。
    /// 库中保存的是参数项深拷贝，保存后重新克隆编辑区——编辑区与库对象零引用共享（防脏写）。</summary>
    [RelayCommand]
    private async Task SaveRecipeAsync()
    {
        if (EditItems.Count == 0)
        {
            _dialog.NotifyWarning(Strings.K681);
            return;
        }

        var recipe = _editingRecipe ?? new Recipe();
        recipe.Name = EditName.Trim();
        recipe.MachineType = EditMachineType.Trim();
        recipe.Remark = EditRemark;
        recipe.Items.Clear();
        foreach (var row in EditItems) recipe.Items.Add(row.Item.Clone());

        // 校验（含同机型配方名唯一性：existing = 当前配方库，排除自身）
        var errors = RecipeValidator.Validate(recipe, _recipeStore.Recipes);
        if (errors.Count > 0)
        {
            _dialog.NotifyWarning(string.Format(Strings.K682, string.Join("；", errors)));
            return;
        }

        _isSaving = true;
        try
        {
            _recipeStore.Upsert(recipe);
            await _recipeStore.SaveAllAsync();
            _logger.LogInformation("配方已保存：{Name}（{MachineType}）", recipe.Name, recipe.MachineType);
            _dialog.NotifySuccess(string.Format(Strings.K688, recipe.Name));
            RefreshRecipes();
            SelectedRecipe = AvailableRecipes.FirstOrDefault(r => r.Id == recipe.Id);
            // 新保存的配方必须可见：重置筛选（搜索词 + 机型），避免被过滤隐藏导致"保存了却看不到"
            RecipeSearchText = "";
            SelectedMachineFilter = AllMachineTypesFilter;
            // 重建编辑区副本，彻底隔离与库的引用
            if (SelectedRecipe is not null) LoadIntoEditor(SelectedRecipe);
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>删除选中配方（二次确认 + 审计）。</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedRecipe))]
    private async Task DeleteRecipeAsync()
    {
        if (SelectedRecipe is null) return;
        var confirm = _dialog.Show(string.Format(Strings.K677, SelectedRecipe.Name),
            Strings.K664, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var id = SelectedRecipe.Id;
        var name = SelectedRecipe.Name;
        _recipeStore.Delete(id);
        await _recipeStore.SaveAllAsync();
        _logger.LogInformation("配方已删除：{Id}", id);
        AuditLog.Record("Recipe.Delete", "Recipe", id, detail: name);
        RefreshRecipes();
    }

    private bool HasSelectedRecipe() => SelectedRecipe != null && !IsApplying;

    /// <summary>下发选中配方到目标设备（Local 直接写 PLC，Remote 经 Collector 执行）。
    /// 机型不匹配（非通用）时先警告确认；逐项结果由进度回调实时填充（单一通道）。</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRecipe))]
    private async Task ApplyRecipeAsync()
    {
        if (SelectedTargetDevice is null)
        {
            _dialog.NotifyWarning(Strings.K687);
            return;
        }
        if (SelectedRecipe is null) return;

        // 机型兼容性拦截（对齐 GetByMachineType 语义：空机型=通用，任意设备可用）
        if (!string.IsNullOrEmpty(SelectedRecipe.MachineType)
            && !string.Equals(SelectedRecipe.MachineType, SelectedTargetDevice.MachineType, StringComparison.OrdinalIgnoreCase))
        {
            var mismatchConfirm = _dialog.Show(
                string.Format(Strings.K702, SelectedRecipe.MachineType, SelectedTargetDevice.MachineType),
                Strings.K661, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (mismatchConfirm != MessageBoxResult.Yes) return;
        }

        var confirm = _dialog.Show(string.Format(Strings.K685, SelectedRecipe.Name, SelectedTargetDevice.Name),
            Strings.K661, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        IsApplying = true;
        ApplyStatus = Strings.K686;
        ApplyStatusKind = ApplyStatusKind.None;
        ApplyItemResults.Clear();
        _applyCts = new CancellationTokenSource();
        IDisposable? progressSubscription = null;
        try
        {
            RecipeApplyResultDto result;
            if (_runtimeMode.IsRemote)
            {
                // 进度经 Hub 推送（OnRecipeApplyProgress），最终结果由 Invoke 返回承载；
                // 订阅句柄在 finally 中释放，防止 handler 逐次累积（重复回调 + 泄漏）。
                // 90s 超时：SignalR 请求-响应无法中途取消，超时自动终止等待，避免 IsApplying 永久卡死。
                progressSubscription = _client.OnRecipeApplyProgress(OnApplyProgress);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_applyCts.Token);
                timeoutCts.CancelAfter(RemoteApplyTimeout);
                result = await _client.ApplyRecipeAsync(SelectedTargetDevice.Id, SelectedRecipe.Id, timeoutCts.Token);
            }
            else
            {
                var device = SelectedTargetDevice;
                var recipe = SelectedRecipe;
                var ct = _applyCts.Token;
                result = await Task.Run(() => _recipeApplier.Apply(device, recipe, OnApplyProgress, ct));
                AuditLog.Record("Recipe.Apply", "Recipe", recipe.Id, succeeded: result.Success,
                    detail: $"{recipe.Name} -> {device.Name}：{result.Message}");
            }

            if (result.Success)
            {
                ApplyStatus = Strings.K675;
                ApplyStatusKind = ApplyStatusKind.Success;
                _dialog.NotifySuccess(string.Format(Strings.K675));
                _logger.LogInformation("配方下发成功：{Recipe} -> {Device}", SelectedRecipe.Name, SelectedTargetDevice.Name);
            }
            else if (_applyCts.IsCancellationRequested)
            {
                // 用户主动取消（仅 Local 模式）：显示取消消息，不弹错误对话框
                ApplyStatus = result.Message;
                ApplyStatusKind = ApplyStatusKind.None;
                _logger.LogInformation("配方下发已取消：{Recipe} -> {Device}", SelectedRecipe.Name, SelectedTargetDevice.Name);
            }
            else
            {
                ApplyStatus = string.Format(Strings.K676, result.Message);
                ApplyStatusKind = ApplyStatusKind.Error;
                _dialog.NotifyError(string.Format(Strings.K676, result.Message));
                _logger.LogWarning("配方下发失败：{Message}（{Recipe} -> {Device}）",
                    result.Message, SelectedRecipe.Name, SelectedTargetDevice.Name);
            }
        }
        catch (OperationCanceledException)
        {
            // Remote 超时（或 Local 用户取消）：统一显示终止原因，不弹错误框
            var timeoutMsg = _runtimeMode.IsRemote ? Strings.K703 : Strings.K686;
            ApplyStatus = timeoutMsg;
            ApplyStatusKind = ApplyStatusKind.Error;
            _logger.LogWarning("配方下发超时/取消：{Recipe} -> {Device}", SelectedRecipe.Name, SelectedTargetDevice.Name);
        }
        catch (Exception ex)
        {
            ApplyStatus = string.Format(Strings.K676, ex.Message);
            ApplyStatusKind = ApplyStatusKind.Error;
            _dialog.NotifyError(string.Format(Strings.K676, ex.Message));
            _logger.LogError(ex, "配方下发异常");
        }
        finally
        {
            progressSubscription?.Dispose();
            IsApplying = false;
            _applyCts?.Dispose();
            _applyCts = null;
        }
    }

    /// <summary>取消正在执行的下发（仅 Local 模式可用；Remote 为 SignalR 请求-响应无法中途取消服务端执行）。</summary>
    [RelayCommand(CanExecute = nameof(IsApplying))]
    private void CancelApply() => _applyCts?.Cancel();

    /// <summary>下发进度回调（Local 在 Task.Run 线程、Remote 在 SignalR 消息线程回调，统一封送 UI 线程）。
    /// 单一通道：每个回调即一条最终结果行（成功=已验证；失败=错误原因），与最终 result.Items 同源。</summary>
    private void OnApplyProgress(RecipeApplyProgressDto p)
    {
        void OnUi()
        {
            ApplyStatus = $"{p.Index}/{p.Total} {p.ParamName}";
            // 去重保护：Remote 模式下 Hub 可能重复投递，避免结果行翻倍
            if (ApplyItemResults.All(r => r.ParamName != p.ParamName))
                ApplyItemResults.Add(new RecipeItemResultDto(p.ParamName, p.Success, p.Message));
        }
        if (_uiDispatcher.CheckAccess()) OnUi();
        else _uiDispatcher.BeginInvoke(OnUi);
    }

    private bool CanApplyRecipe() => SelectedRecipe != null && SelectedTargetDevice != null && !IsApplying;

    /// <summary>添加参数项。</summary>
    [RelayCommand]
    private void AddItem()
    {
        EditItems.Add(new RecipeEditItemRow(new RecipeItem { DataType = Kanban.Contracts.Enums.PlcDataType.Int32 }));
    }

    /// <summary>移除指定参数项。</summary>
    [RelayCommand]
    private void RemoveItem(RecipeEditItemRow? row)
    {
        if (row is not null) EditItems.Remove(row);
    }

    /// <summary>复制选中配方到编辑区：新 Id + 副本后缀，保存后独立落库，不覆盖原配方。</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedRecipe))]
    private void CloneRecipe()
    {
        if (SelectedRecipe is null) return;
        var clone = SelectedRecipe.Clone();
        // 先清选中（触发 OnSelectedRecipeChanged 清空编辑区），再填充副本——与 NewRecipe 顺序一致
        SelectedRecipe = null;
        _editingRecipe = null;
        EditName = clone.Name + Strings.K692;
        EditMachineType = clone.MachineType;
        EditRemark = clone.Remark;
        EditItems.Clear();
        foreach (var item in clone.Items) EditItems.Add(new RecipeEditItemRow(item.Clone()));
        ApplyStatus = "";
        ApplyStatusKind = ApplyStatusKind.None;
    }

    /// <summary>导出全部配方到 JSON 文件（与 recipes.json 同构，备份/分享用）。</summary>
    [RelayCommand]
    private void ExportRecipes() => _recipeJsonIo.ExportRecipes();

    /// <summary>从 JSON 文件导入配方（合并 by Id：新增/更新，不删除本地配方）。</summary>
    [RelayCommand]
    private async Task ImportRecipesAsync() => await _recipeJsonIo.ImportRecipesAsync();

    public void Dispose()
    {
        _recipeStore.Recipes.CollectionChanged -= OnStoreRecipesChanged;
    }
}

/// <summary>下发状态类别（替代魔法字符串，控制状态文本颜色）。</summary>
public enum ApplyStatusKind
{
    None,
    Success,
    Error,
}

/// <summary>机型筛选胶囊项（配方卡片列表上方筛选条）。空机型 = 通用配方。</summary>
public sealed partial class MachineTypeFilterOption : ObservableObject
{
    public MachineTypeFilterOption(string machineType, string display, int count)
    {
        MachineType = machineType;
        Display = display;
        Count = count;
    }

    /// <summary>原始机型值（空字符串 = 通用/全部胶囊，由调用方按语义区分）。</summary>
    public string MachineType { get; }

    /// <summary>胶囊显示文本（通用配方显示"通用"，其余显示机型名）。</summary>
    public string Display { get; }

    /// <summary>该机型配方数。</summary>
    public int Count { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 编辑行包装：转发 <see cref="RecipeItem"/> 表单属性并做行内实时校验（首个错误）。
/// 地址重复/配方名唯一等需要整配方上下文的规则仍由保存时 <see cref="RecipeValidator"/> 兜底。
/// </summary>
public sealed partial class RecipeEditItemRow : ObservableObject
{
    /// <summary>被包装的参数项（保存时取 <see cref="Item"/> 深拷贝入库，不与库共享引用）。</summary>
    public RecipeItem Item { get; }

    public RecipeEditItemRow(RecipeItem item)
    {
        Item = item;
        Revalidate();
    }

    public string ParamName
    {
        get => Item.ParamName;
        set { Item.ParamName = value; OnPropertyChanged(); Revalidate(); }
    }

    public string PlcAddress
    {
        get => Item.PlcAddress;
        set { Item.PlcAddress = value; OnPropertyChanged(); Revalidate(); }
    }

    public Kanban.Contracts.Enums.PlcDataType DataType
    {
        get => Item.DataType;
        set { Item.DataType = value; OnPropertyChanged(); Revalidate(); }
    }

    public string Value
    {
        get => Item.Value;
        set { Item.Value = value; OnPropertyChanged(); Revalidate(); }
    }

    public double? Min
    {
        get => Item.Min;
        set { Item.Min = value; OnPropertyChanged(); Revalidate(); }
    }

    public double? Max
    {
        get => Item.Max;
        set { Item.Max = value; OnPropertyChanged(); Revalidate(); }
    }

    public string Unit
    {
        get => Item.Unit;
        set { Item.Unit = value; OnPropertyChanged(); }
    }

    /// <summary>行内校验错误文本（空 = 通过）。</summary>
    [ObservableProperty] private string _errorText = "";

    /// <summary>是否通过行内校验（XAML 控制错误列可见性）。</summary>
    public bool IsValid => string.IsNullOrEmpty(ErrorText);

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(IsValid));

    /// <summary>重算行内校验（首个错误优先展示）。</summary>
    private void Revalidate()
    {
        ErrorText = ValidateItem(Item);
    }

    /// <summary>单行校验（与 <see cref="RecipeValidator"/> 同口径的逐项规则；地址重复等整配方规则不在行内）。</summary>
    public static string ValidateItem(RecipeItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ParamName))
            return RecipeValidationMessages.RecipeParamNameEmpty;
        if (string.IsNullOrWhiteSpace(item.PlcAddress))
            return RecipeValidationMessages.RecipeAddressEmpty;

        var parse = PlcAddressParser.Parse(item.PlcAddress);
        if (!parse.IsValid)
            return string.Format(RecipeValidationMessages.RecipeAddressUnresolvable, item.PlcAddress);
        var expectedType = item.DataType == Kanban.Contracts.Enums.PlcDataType.Bool ? PlcAddressType.MBit : PlcAddressType.DWord;
        if (parse.Type != expectedType)
            return string.Format(RecipeValidationMessages.RecipeAddressTypeMismatch, item.DataType, expectedType);

        if (item.DataType == Kanban.Contracts.Enums.PlcDataType.String && item.Value.Length > RecipeValidator.MaxStringLength)
            return string.Format(RecipeValidationMessages.RecipeStringTooLong, RecipeValidator.MaxStringLength);

        if (!RecipeValidator.TryParseValue(item, out var numeric))
            return string.Format(RecipeValidationMessages.RecipeValueInvalid, item.Value, DataTypeText(item.DataType));
        if (item.Min is { } minValue && item.Max is { } maxValue && minValue > maxValue)
            return string.Format(RecipeValidationMessages.RecipeMinMaxInverted, minValue, maxValue);
        if (numeric is { } n)
        {
            if (item.Min is { } min && n < min)
                return string.Format(RecipeValidationMessages.RecipeBelowMin, n, min);
            if (item.Max is { } max && n > max)
                return string.Format(RecipeValidationMessages.RecipeAboveMax, n, max);
        }
        return "";
    }

    private static string DataTypeText(Kanban.Contracts.Enums.PlcDataType type) => type switch
    {
        Kanban.Contracts.Enums.PlcDataType.Int32 => RecipeValidationMessages.RecipeTypeInt32,
        Kanban.Contracts.Enums.PlcDataType.Float => RecipeValidationMessages.RecipeTypeFloat,
        Kanban.Contracts.Enums.PlcDataType.Bool => RecipeValidationMessages.RecipeTypeBool,
        Kanban.Contracts.Enums.PlcDataType.String => RecipeValidationMessages.RecipeTypeString,
        Kanban.Contracts.Enums.PlcDataType.UInt16 => RecipeValidationMessages.RecipeTypeUInt16,
        _ => type.ToString(),
    };
}
