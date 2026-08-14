using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Kanban.Core.Models;
using Kanban.Core.Services;
using Serilog;

namespace Kanban.Core.Data;

/// <summary>配方存储接口（配方库 CRUD + 持久化）。</summary>
public interface IRecipeStore
{
    /// <summary>全部配方（UI 绑定集合）。</summary>
    ObservableCollection<Recipe> Recipes { get; }

    Recipe? GetById(string id);

    /// <summary>按机型取配方（空 MachineType = 通用配方，始终返回；优先精确匹配机型）。</summary>
    IReadOnlyList<Recipe> GetByMachineType(string machineType);

    /// <summary>从 recipes.json 加载配方到内存。启动时调用一次；文件损坏时备份 .corrupt 并保持空集合。</summary>
    void LoadAll();

    /// <summary>异步保存全部配方。Remote 模式经 RemotePersistenceHook 委托 Collector 落盘。</summary>
    Task SaveAllAsync();

    /// <summary>同步保存全部配方（退出/迁移等同步上下文；Remote 模式委托 Collector 落盘）。</summary>
    void SaveAll();

    /// <summary>新增或更新配方（按 Id），并返回落库后的实体（UpdatedAt 已刷新）。</summary>
    Recipe Upsert(Recipe recipe);

    /// <summary>整体替换配方列表（Remote 同步语义，对齐 DeviceRepository.ReplaceAll）。</summary>
    void ReplaceAll(IEnumerable<Recipe> recipes);

    /// <summary>按 Id 删除配方。</summary>
    bool Delete(string id);

    /// <summary>配方远程持久化委托（Remote 模式由 MainAPP 注入：经 SignalR 推给 Collector 落盘 recipes.json）。</summary>
    Func<IReadOnlyList<Recipe>, Task>? RemotePersistenceHook { get; set; }
}

/// <summary>
/// 配方库存储：内存集合 + recipes.json 全量覆写持久化。
/// 与 DeviceRepository 同模式（原子写 + .corrupt 备份 + RemotePersistenceHook），
/// 遵循 ADR-2 单写者：Remote 模式下写操作委托 Collector 落盘。
/// </summary>
public sealed class RecipeStore : IRecipeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AppSettings _appSettings;
    private readonly object _collectionLock = new();

    /// <summary>集合同步锁（只读暴露）：供 WPF 绑定引擎注册跨线程同步（MainAPP 启动时调用
    /// BindingOperations.EnableCollectionSynchronization(Recipes, SyncRoot)）。</summary>
    public object SyncRoot => _collectionLock;

    public RecipeStore(AppSettings appSettings)
    {
        _appSettings = appSettings;
        // 注：WPF 绑定同步锁不在此注册（UI 进程关注点，Core 不依赖 WPF），
        // 由 MainAPP.WpfCollectionBindingRegistrar 经 SyncRoot 注册。
    }

    public ObservableCollection<Recipe> Recipes { get; } = new();

    private readonly ConcurrentDictionary<string, Recipe> _recipeMap = new();

    public string FilePath => _appSettings.GetFilePath("recipes.json");

    public Func<IReadOnlyList<Recipe>, Task>? RemotePersistenceHook { get; set; }

    public Recipe? GetById(string id)
        => _recipeMap.TryGetValue(id, out var recipe) ? recipe : null;

    public IReadOnlyList<Recipe> GetByMachineType(string machineType)
    {
        lock (_collectionLock)
        {
            var normalized = machineType?.Trim() ?? string.Empty;
            return Recipes
                .Where(r => string.IsNullOrEmpty(r.MachineType) || string.Equals(r.MachineType, normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public void LoadAll()
    {
        lock (_collectionLock)
        {
            Recipes.Clear();
            _recipeMap.Clear();
        }

        if (!File.Exists(FilePath)) return;

        try
        {
            var json = File.ReadAllText(FilePath);
            var recipes = JsonSerializer.Deserialize<List<Recipe>>(json, JsonOptions);
            if (recipes != null)
            {
                lock (_collectionLock)
                {
                    // 加载校验过滤：非法条目（空名/地址不可解析/值越界/与已加载配方重名等）跳过并告警。
                    // 文件本身保留（用户可手工修复），不做 .corrupt 备份——避免坏配方进入库后可被下发。
                    // existing 传已加载集合，顺带拦截历史文件中的同机型重名。
                    foreach (var r in recipes)
                    {
                        var errors = RecipeValidator.Validate(r, Recipes);
                        if (errors.Count > 0)
                        {
                            Log.Warning("配方加载校验失败，已跳过：{RecipeId} {RecipeName}（{Errors}）",
                                r.Id, r.Name, string.Join("；", errors));
                            continue;
                        }
                        r.MachineType = (r.MachineType ?? "").Trim();
                        // 历史数据修复：早期版本/手工构造的配方未写时间戳（0001-01-01），
                        // 加载时回填当前时间，避免 UI"更新于"显示 01-01 00:00（下次保存时落盘修正）。
                        if (r.CreatedAt == default) r.CreatedAt = DateTime.Now;
                        if (r.UpdatedAt == default) r.UpdatedAt = DateTime.Now;
                        Recipes.Add(r);
                        _recipeMap[r.Id] = r;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 文件损坏：备份原文件供用户恢复，避免静默清空后覆写导致配方丢失
            Log.Warning(ex, "recipes.json 解析失败，将备份原文件并回退空配方列表");
            var corruptPath = FilePath + ".corrupt";
            try
            {
                if (File.Exists(corruptPath)) File.Delete(corruptPath);
                File.Move(FilePath, corruptPath);
            }
            catch (Exception backupEx)
            {
                Log.Warning(backupEx, "recipes.json 备份为 .corrupt 失败，原文件保留原位");
            }
        }
    }

    public async Task SaveAllAsync()
    {
        if (RemotePersistenceHook != null)
        {
            List<Recipe> snapshot;
            lock (_collectionLock)
                snapshot = Recipes.ToList();
            await RemotePersistenceHook(snapshot);
            return;
        }
        SaveAll();
    }

    public void SaveAll()
    {
        if (RemotePersistenceHook != null)
        {
            List<Recipe> remoteSnapshot;
            lock (_collectionLock)
                remoteSnapshot = Recipes.ToList();
            Task.Run(() => RemotePersistenceHook(remoteSnapshot)).GetAwaiter().GetResult();
            return;
        }

        _appSettings.EnsureDirectory();
        List<Recipe> snapshot;
        lock (_collectionLock)
            snapshot = Recipes.ToList();
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        AppSettings.WriteFileAtomically(FilePath, json);
    }

    public Recipe Upsert(Recipe recipe)
    {
        recipe.UpdatedAt = DateTime.Now;
        if (recipe.CreatedAt == default) recipe.CreatedAt = DateTime.Now; // 导入/手工构造数据兜底
        recipe.MachineType = (recipe.MachineType ?? "").Trim();
        lock (_collectionLock)
        {
            if (_recipeMap.TryGetValue(recipe.Id, out var existing))
            {
                var idx = Recipes.IndexOf(existing);
                if (idx >= 0) Recipes[idx] = recipe;
            }
            else
            {
                Recipes.Add(recipe);
            }
            _recipeMap[recipe.Id] = recipe;
        }
        return recipe;
    }

    public bool Delete(string id)
    {
        lock (_collectionLock)
        {
            if (!_recipeMap.TryRemove(id, out var existing))
                return false;
            Recipes.Remove(existing);
            return true;
        }
    }

    public void ReplaceAll(IEnumerable<Recipe> newRecipes)
    {
        var list = newRecipes as IList<Recipe> ?? newRecipes.ToList();
        lock (_collectionLock)
        {
            Recipes.Clear();
            _recipeMap.Clear();
            foreach (var recipe in list)
            {
                recipe.MachineType = (recipe.MachineType ?? "").Trim();
                Recipes.Add(recipe);
                _recipeMap[recipe.Id] = recipe;
            }
        }
    }
}
