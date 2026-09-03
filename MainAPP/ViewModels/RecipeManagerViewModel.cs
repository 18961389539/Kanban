using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Client;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Localization;
using MainAPP.Models;
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
public partial class RecipeManagerViewModel : ObservableObject, IDisposable, INavigationPageLifecycle
{
    /// <summary>"全部"筛选胶囊的哨兵值（与"通用"机型的空字符串区分）。</summary>
    public const string AllMachineTypesFilter = "__ALL__";

    /// <summary>Remote 下发等待上限（SignalR 请求-响应无法中途取消，超时自动终止等待）。</summary>
    private static readonly TimeSpan RemoteApplyTimeout = TimeSpan.FromSeconds(90);

    private readonly IRecipeStore _recipeStore;
    private readonly RecipeApplier _recipeApplier;
    private readonly IKanbanAdminClient _client;
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
        IKanbanAdminClient client,
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

        // 配方库集合变更（Remote 同步/其他端写入）→ 刷新列表；保存中的内部刷新由 _isSaving 屏蔽
        _recipeStore.Recipes.CollectionChanged += OnStoreRecipesChanged;

        // 构造时同步填充一次，保证"构造即可用"契约（单测直接断言 AvailableRecipes/TargetDevices、
        // 以及任何依赖 VM 创建后立即可展示首屏的场景）；OnPageEnter 仍会再刷一次拉取最新数据
        // （此前填充只在 OnPageEnter 的 BeginInvoke 里做，构造后列表恒为空——审查修复 2026-09-03）。
        RefreshRecipes();
        RefreshDevices();
    }

    private bool _pageActive;

    /// <inheritdoc />
    public void OnPageEnter()
    {
        _pageActive = true;
        _uiDispatcher.BeginInvoke(() =>
        {
            if (!_pageActive) return;
            RefreshRecipes();
            RefreshDevices();
        }, DispatcherPriority.Background);
    }

    /// <inheritdoc />
    public void OnPageExit() => _pageActive = false;

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
    [NotifyCanExecuteChangedFor(nameof(CloneRecipeCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(DeleteRecipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloneRecipeCommand))]
    private bool _isApplying;

    /// <summary>下发中的取消令牌（Local 模式可手动取消；Remote 模式仅作超时链接用）。</summary>
    private CancellationTokenSource? _applyCts;

    /// <summary>本轮下发已处理的进度序号（按 Index 去重，避免同名参数第二行被误吞）。</summary>
    private readonly HashSet<int> _seenApplyIndexes = new();

    /// <summary>下发逐项结果（写/校验每项成败明细，进度回调实时填充，单一通道）。</summary>
    public ObservableCollection<RecipeItemResultDto> ApplyItemResults { get; } = new();

    /// <summary>是否 Remote 模式（XAML 控制取消下发按钮可见性）。</summary>
    public bool IsRemote => _runtimeMode.IsRemote;

    // ──────────── 编辑区（内联表单） ────────────

    /// <summary>正在编辑的配方（null = 无编辑/新建初始）。</summary>
    private Recipe? _editingRecipe;

    /// <summary>跳过下一次选中变更的未保存确认（New/Clone 主动清空编辑区时使用）。</summary>
    private bool _skipSelectionGuard;
    private Recipe? _previousSelectedRecipe;

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

    /// <summary>Bool 参数值 UI 可选范围（仅 True/False；后台仍兼容历史数据中的 0/1 输入，不用于展示）。</summary>
    public string[] BoolOptions => new[] { "True", "False" };

    /// <summary>从配方库加载全部配方。</summary>
    private void RefreshRecipes()
    {
        var current = SelectedRecipe?.Id;
        // 保存/恢复守卫：外层若是程序化刷新（如保存流程）会保持 true，此处不清掉，
        // 避免 RefreshRecipes 内部把外层保护复位后触发未保存确认。
        var previousGuard = _skipSelectionGuard;
        _skipSelectionGuard = true;
        AvailableRecipes.Clear();
        foreach (var r in _recipeStore.Recipes) AvailableRecipes.Add(r);
        ApplyFilters();
        SelectedRecipe = AvailableRecipes.FirstOrDefault(r => r.Id == current) ?? AvailableRecipes.FirstOrDefault();
        _skipSelectionGuard = previousGuard;
        if (SelectedRecipe is null)
        {
            // 程序化刷新后确实无选中项（如删除/远程清空）：清空编辑区，避免残留旧编辑
            _editingRecipe = null;
            EditName = "";
            EditMachineType = "";
            EditRemark = "";
            EditItems.Clear();
        }
        else if (!IsEditorDirty())
        {
            // 被动刷新（Remote 同步等）不覆盖用户未保存的内联编辑；无改动时才重载，保持列表与编辑区一致
            LoadIntoEditor(SelectedRecipe);
        }
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
        if (_skipSelectionGuard)
        {
            _previousSelectedRecipe = value;
            return;
        }

        var old = _previousSelectedRecipe;
        _previousSelectedRecipe = value;

        // 切换前未保存确认：避免静默丢弃当前编辑区内容
        if (value != old && IsEditorDirty())
        {
            var keepEditing = _dialog.Show(
                "切换配方会放弃当前未保存的编辑，是否继续？",
                "提示", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (keepEditing != MessageBoxResult.Yes)
            {
                _skipSelectionGuard = true;
                SelectedRecipe = old;
                _skipSelectionGuard = false;
                _previousSelectedRecipe = old;
                return;
            }
        }

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

    /// <summary>当前编辑区相对目标配方是否有未保存改动（切换选中配方法用）。</summary>
    private static string NormalizeValueForDirty(RecipeItem item)
    {
        if (item.DataType != Kanban.Contracts.Enums.PlcDataType.Bool) return item.Value;
        var raw = item.Value?.Trim();
        if (string.Equals(raw, "0", StringComparison.Ordinal)
            || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return "False";
        if (string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return "True";
        return raw ?? string.Empty;
    }

    private bool IsEditorDirty()
    {
        if (_editingRecipe is null)
            return !string.IsNullOrWhiteSpace(EditName) || EditItems.Count > 0;

        if (!string.Equals(EditName.Trim(), _editingRecipe.Name, StringComparison.Ordinal)
            || !string.Equals(EditMachineType.Trim(), _editingRecipe.MachineType, StringComparison.Ordinal)
            || !string.Equals(EditRemark, _editingRecipe.Remark, StringComparison.Ordinal)
            || EditItems.Count != _editingRecipe.Items.Count)
            return true;

        for (var i = 0; i < EditItems.Count; i++)
        {
            var a = EditItems[i].Item;
            var b = _editingRecipe.Items[i];
            if (!string.Equals(a.ParamName, b.ParamName, StringComparison.Ordinal)
                || !string.Equals(a.PlcAddress, b.PlcAddress, StringComparison.Ordinal)
                || a.DataType != b.DataType
                || !string.Equals(NormalizeValueForDirty(a), NormalizeValueForDirty(b), StringComparison.Ordinal)
                || a.Min != b.Min
                || a.Max != b.Max
                || !string.Equals(a.Unit, b.Unit, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>新建配方：清空编辑区并预置一个参数项。</summary>
    [RelayCommand]
    private void NewRecipe()
    {
        // 主动清空编辑区：跳过切换确认
        _skipSelectionGuard = true;
        SelectedRecipe = null;
        _skipSelectionGuard = false;
        _previousSelectedRecipe = null;
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

        // 编辑副本防御：先在候选对象上组装并校验，校验通过后再 Upsert，
        // 避免把 _editingRecipe 指向的库中活对象直接改坏（校验失败时原配方仍完整）。
        var isNew = _editingRecipe is null;
        var recipe = isNew
            ? new Recipe()
            : new Recipe { Id = _editingRecipe!.Id, CreatedAt = _editingRecipe.CreatedAt };
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
            _skipSelectionGuard = true;
            RefreshRecipes();
            SelectedRecipe = AvailableRecipes.FirstOrDefault(r => r.Id == recipe.Id);
            // 新保存的配方必须可见：重置筛选（搜索词 + 机型），避免被过滤隐藏导致"保存了却看不到"
            RecipeSearchText = "";
            SelectedMachineFilter = AllMachineTypesFilter;
            // 重建编辑区副本，彻底隔离与库的引用
            if (SelectedRecipe is not null) LoadIntoEditor(SelectedRecipe);
            _skipSelectionGuard = false;
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

        // 稳定引用：下发期间捕获选中对象，避免用户切换选择导致结果/审计写错对象
        var device = SelectedTargetDevice!;
        var recipe = SelectedRecipe!;

        IsApplying = true;
        ApplyStatus = Strings.K686;
        ApplyStatusKind = ApplyStatusKind.None;
        ApplyItemResults.Clear();
        _seenApplyIndexes.Clear();
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
                result = await _client.ApplyRecipeAsync(device.Id, recipe.Id, timeoutCts.Token);
            }
            else
            {
                var ct = _applyCts.Token;
                result = await Task.Run(() => _recipeApplier.Apply(device, recipe, OnApplyProgress, ct));
            }

            AuditLog.Record("Recipe.Apply", "Recipe", recipe.Id, succeeded: result.Success,
                detail: $"{recipe.Name} -> {device.Name}：{result.Message}");

            if (result.Success)
            {
                ApplyStatus = Strings.K675;
                ApplyStatusKind = ApplyStatusKind.Success;
                _dialog.NotifySuccess(string.Format(Strings.K675));
                _logger.LogInformation("配方下发成功：{Recipe} -> {Device}", recipe.Name, device.Name);
            }
            else if (_applyCts.IsCancellationRequested)
            {
                // 用户主动取消（仅 Local 模式）：显示取消消息，不弹错误对话框
                ApplyStatus = result.Message;
                ApplyStatusKind = ApplyStatusKind.None;
                _logger.LogInformation("配方下发已取消：{Recipe} -> {Device}", recipe.Name, device.Name);
            }
            else
            {
                ApplyStatus = string.Format(Strings.K676, result.Message);
                ApplyStatusKind = ApplyStatusKind.Error;
                if (_runtimeMode.IsRemote && ApplyItemResults.Count == 0)
                    ApplyItemResults.Add(new RecipeItemResultDto("-", false, result.Message));
                _dialog.NotifyError(string.Format(Strings.K676, result.Message));
                _logger.LogWarning("配方下发失败：{Message}（{Recipe} -> {Device}）",
                    result.Message, recipe.Name, device.Name);
            }
        }
        catch (OperationCanceledException)
        {
            // Remote 超时（或 Local 用户取消）：统一显示终止原因，不弹错误框
            var timeoutMsg = _runtimeMode.IsRemote ? Strings.K703 : Strings.K686;
            ApplyStatus = timeoutMsg;
            ApplyStatusKind = ApplyStatusKind.Error;
            _logger.LogWarning("配方下发超时/取消：{Recipe} -> {Device}", recipe.Name, device.Name);
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
            // 去重保护：Remote 模式 Hub 可能重复投递，按 Index 去重；同名参数不同行也不会被误吞。
            if (!_seenApplyIndexes.Add(p.Index)) return;
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
        // 主动清空编辑区：跳过切换确认
        _skipSelectionGuard = true;
        SelectedRecipe = null;
        _skipSelectionGuard = false;
        _previousSelectedRecipe = null;
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
        // 历史数据兼容：Bool 值归一化为 True/False（大小写不敏感、容忍首尾空格与 0/1），避免下拉框显示空白
        if (item.DataType == Kanban.Contracts.Enums.PlcDataType.Bool)
        {
            var raw = item.Value?.Trim();
            if (string.Equals(raw, "0", StringComparison.Ordinal)
                || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
                item.Value = "False";
            else if (string.Equals(raw, "1", StringComparison.Ordinal)
                     || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
                item.Value = "True";
            else
                item.Value = raw ?? string.Empty;
        }
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
        set { Item.DataType = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBool)); Revalidate(); }
    }

    /// <summary>是否为布尔类型（XAML 用此布尔值切换 值列 的文本框/下拉框，避免枚举 DataTrigger 失效）。</summary>
    public bool IsBool => DataType == Kanban.Contracts.Enums.PlcDataType.Bool;

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

        if (item.DataType == Kanban.Contracts.Enums.PlcDataType.String && (item.Value?.Length ?? 0) > RecipeValidator.MaxStringLength)
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
