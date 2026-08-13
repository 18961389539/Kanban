using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Client;
using Kanban.Contracts.Dtos;
using Kanban.Core.Data;
using Kanban.Core.Models;
using Kanban.Core.Services;
using MainAPP.Resources;
using MainAPP.Services;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

namespace MainAPP.ViewModels;

/// <summary>
/// 配方管理页 VM（独立导航页，全局配方库）。
/// 配方按机型归属（空机型=通用）；下发目标通过页面内设备下拉选择任意设备。
/// 下发执行：Local 模式用 RecipeApplier 直接写 PLC（写后读回校验+失败回滚）；Remote 模式经 SignalR 由 Collector 执行。
/// </summary>
public partial class RecipeManagerViewModel : ObservableObject, IDisposable
{
    private readonly IRecipeStore _recipeStore;
    private readonly RecipeApplier _recipeApplier;
    private readonly KanbanDataClient _client;
    private readonly IRuntimeMode _runtimeMode;
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDialogService _dialog;
    private readonly RecipeJsonIOService _recipeJsonIo;
    private readonly ILogger<RecipeManagerViewModel> _logger;
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;

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

    /// <summary>全部配方列表（含通用 + 各机型）。</summary>
    public ObservableCollection<Recipe> AvailableRecipes { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteRecipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecipeCommand))]
    private Recipe? _selectedRecipe;

    /// <summary>可下发目标设备列表（独立页下拉选择）。</summary>
    public ObservableCollection<Device> TargetDevices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecipeCommand))]
    private Device? _selectedTargetDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelApplyCommand))]
    private bool _isApplying;

    /// <summary>下发中的取消令牌（Local 模式可用；Remote 为 SignalR 请求-响应无法中途取消，取消按钮不显示）。</summary>
    private CancellationTokenSource? _applyCts;

    /// <summary>下发逐项结果（写/校验每项成败明细，成功与失败均展示）。</summary>
    public ObservableCollection<RecipeItemResultDto> ApplyItemResults { get; } = new();

    /// <summary>是否 Remote 模式（XAML 控制取消下发按钮可见性）。</summary>
    public bool IsRemote => _runtimeMode.IsRemote;

    // ──────────── 编辑区（内联表单） ────────────

    /// <summary>正在编辑的配方（null = 无编辑/新建初始）。</summary>
    private Recipe? _editingRecipe;

    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editMachineType = "";
    [ObservableProperty] private string _editRemark = "";

    /// <summary>编辑中的参数项（DataGrid 可编辑绑定）。</summary>
    public ObservableCollection<RecipeItem> EditItems { get; } = new();

    /// <summary>下发结果状态文本（"成功"用绿色，失败用红色，见 StatusKind）。</summary>
    [ObservableProperty] private string _applyStatus = "";

    /// <summary>"Success"/"Error"/"" 控制状态文本颜色。</summary>
    [ObservableProperty] private string _applyStatusKind = "";

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
        SelectedRecipe = AvailableRecipes.FirstOrDefault(r => r.Id == current) ?? AvailableRecipes.FirstOrDefault();
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
        // 用深拷贝填充编辑区：DataGrid 行编辑（UpdateSourceTrigger=PropertyChanged）只改副本，
        // 未保存时不会污染配方库内存对象
        foreach (var item in recipe.Items) EditItems.Add(item.Clone());
        ApplyStatus = "";
        ApplyStatusKind = "";
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
        EditItems.Add(new RecipeItem { DataType = Kanban.Contracts.Enums.PlcDataType.Int32 });
        ApplyStatus = "";
        ApplyStatusKind = "";
    }

    /// <summary>保存当前编辑配方（校验 → Upsert → 落盘 → 刷新列表）。</summary>
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
        foreach (var item in EditItems) recipe.Items.Add(item);

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
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>删除选中配方（二次确认）。</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedRecipe))]
    private async Task DeleteRecipeAsync()
    {
        if (SelectedRecipe is null) return;
        var confirm = _dialog.Show(string.Format(Strings.K677, SelectedRecipe.Name),
            Strings.K664, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var id = SelectedRecipe.Id;
        _recipeStore.Delete(id);
        await _recipeStore.SaveAllAsync();
        _logger.LogInformation("配方已删除：{Id}", id);
        RefreshRecipes();
    }

    private bool HasSelectedRecipe() => SelectedRecipe != null && !IsApplying;

    /// <summary>下发选中配方到目标设备（Local 直接写 PLC，Remote 经 Collector 执行）。
    /// 下发期间逐项进度更新 ApplyStatus，最终逐项结果填充 ApplyItemResults。</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRecipe))]
    private async Task ApplyRecipeAsync()
    {
        if (SelectedTargetDevice is null)
        {
            _dialog.NotifyWarning(Strings.K687);
            return;
        }
        if (SelectedRecipe is null) return;

        var confirm = _dialog.Show(string.Format(Strings.K685, SelectedRecipe.Name, SelectedTargetDevice.Name),
            Strings.K661, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        IsApplying = true;
        ApplyStatus = Strings.K686;
        ApplyStatusKind = "";
        ApplyItemResults.Clear();
        _applyCts = new CancellationTokenSource();
        IDisposable? progressSubscription = null;
        try
        {
            RecipeApplyResultDto result;
            if (_runtimeMode.IsRemote)
            {
                // 进度经 Hub 推送（OnRecipeApplyProgress），最终结果由 Invoke 返回承载；
                // 订阅句柄在 finally 中释放，防止 handler 逐次累积（重复回调 + 泄漏）
                progressSubscription = _client.OnRecipeApplyProgress(OnApplyProgress);
                result = await _client.ApplyRecipeAsync(SelectedTargetDevice.Id, SelectedRecipe.Id);
            }
            else
            {
                var device = SelectedTargetDevice;
                var recipe = SelectedRecipe;
                var ct = _applyCts.Token;
                result = await Task.Run(() => _recipeApplier.Apply(device, recipe, OnApplyProgress, ct));
            }

            foreach (var item in result.Items) ApplyItemResults.Add(item);

            if (result.Success)
            {
                ApplyStatus = Strings.K675;
                ApplyStatusKind = "Success";
                _dialog.NotifySuccess(string.Format(Strings.K675));
                _logger.LogInformation("配方下发成功：{Recipe} -> {Device}", SelectedRecipe.Name, SelectedTargetDevice.Name);
            }
            else if (_applyCts.IsCancellationRequested)
            {
                // 用户主动取消：显示取消消息，不弹错误对话框
                ApplyStatus = result.Message;
                ApplyStatusKind = "";
                _logger.LogInformation("配方下发已取消：{Recipe} -> {Device}", SelectedRecipe.Name, SelectedTargetDevice.Name);
            }
            else
            {
                ApplyStatus = string.Format(Strings.K676, result.Message);
                ApplyStatusKind = "Error";
                _dialog.NotifyError(string.Format(Strings.K676, result.Message));
                _logger.LogWarning("配方下发失败：{Message}（{Recipe} -> {Device}）",
                    result.Message, SelectedRecipe.Name, SelectedTargetDevice.Name);
            }
        }
        catch (Exception ex)
        {
            ApplyStatus = string.Format(Strings.K676, ex.Message);
            ApplyStatusKind = "Error";
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

    /// <summary>下发进度回调（Local 在 Task.Run 线程、Remote 在 SignalR 消息线程回调，统一封送 UI 线程）。</summary>
    private void OnApplyProgress(RecipeApplyProgressDto p)
    {
        var text = $"{p.Index}/{p.Total} {p.ParamName}";
        if (_uiDispatcher.CheckAccess()) ApplyStatus = text;
        else _uiDispatcher.BeginInvoke(() => ApplyStatus = text);
    }

    private bool CanApplyRecipe() => SelectedRecipe != null && SelectedTargetDevice != null && !IsApplying;

    /// <summary>添加参数项。</summary>
    [RelayCommand]
    private void AddItem()
    {
        EditItems.Add(new RecipeItem { DataType = Kanban.Contracts.Enums.PlcDataType.Int32 });
    }

    /// <summary>移除指定参数项。</summary>
    [RelayCommand]
    private void RemoveItem(RecipeItem? item)
    {
        if (item is not null) EditItems.Remove(item);
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
        foreach (var item in clone.Items) EditItems.Add(item.Clone());
        ApplyStatus = "";
        ApplyStatusKind = "";
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
