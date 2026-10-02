using System.IO;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Serilog;

namespace Kanban.Collector.Core.Data;

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

    /// <summary>异步保存全部配方。Remote 模式经显式远程存储端口委托 Collector 落盘。</summary>
    Task SaveAllAsync();

    /// <summary>同步保存全部配方（Local 模式使用；Remote 模式必须使用异步 API）。</summary>
    void SaveAll();

    /// <summary>新增或更新配方（按 Id），并返回落库后的实体（UpdatedAt 已刷新）。</summary>
    Recipe Upsert(Recipe recipe);

    /// <summary>整体替换配方列表（Remote 同步语义，对齐 DeviceRepository.ReplaceAll）。</summary>
    void ReplaceAll(IEnumerable<Recipe> recipes);

    /// <summary>按 Id 删除配方。</summary>
    bool Delete(string id);

    /// <summary>
    /// 先把含本次新增/更新的快照落盘，成功后再改内存。
    /// 落盘失败时内存集合保持原样。
    /// </summary>
    Task<Recipe> CommitUpsertAsync(Recipe recipe);

    /// <summary>先落盘删除后的快照，成功后再从内存移除。配方不存在时返回 false 且不落盘。</summary>
    Task<bool> CommitDeleteAsync(string id);

    /// <summary>先落盘整表替换，成功后再替换内存。Remote 模式必须使用 <see cref="CommitReplaceAllAsync"/>。</summary>
    void CommitReplaceAll(IEnumerable<Recipe> recipes);

    /// <summary>先落盘整表替换，成功后再替换内存。</summary>
    Task CommitReplaceAllAsync(IEnumerable<Recipe> recipes);

}

/// <summary>
/// 配方库存储：内存集合 + recipes.json 全量覆写持久化。
/// 与 DeviceRepository 同模式（原子写 + .corrupt 备份 + 显式远程存储端口），
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
    private readonly IRemoteRecipeStore? _remoteStore;
    private readonly object _collectionLock = new();
    private readonly SemaphoreSlim _commitGate = new(1, 1);

    /// <summary>集合同步锁（只读暴露）：供 WPF 绑定引擎注册跨线程同步（MainAPP 启动时调用
    /// BindingOperations.EnableCollectionSynchronization(Recipes, SyncRoot)）。</summary>
    public object SyncRoot => _collectionLock;

    public RecipeStore(
        AppSettings appSettings,
        IRemoteRecipeStore? remoteStore = null)
    {
        _appSettings = appSettings;
        _remoteStore = remoteStore;
        // 注：WPF 绑定同步锁不在此注册（UI 进程关注点，Core 不依赖 WPF），
        // 由 MainAPP.WpfCollectionBindingRegistrar 经 SyncRoot 注册。
    }

    public ObservableCollection<Recipe> Recipes { get; } = new();

    private readonly ConcurrentDictionary<string, Recipe> _recipeMap = new();

    public string FilePath => _appSettings.GetFilePath("recipes.json");

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
                    // 校验失败的条目仍然入库并记入 LoadErrors（禁止下发），避免下一次全量保存把它们从文件里抹掉。
                    // existing 传已加载集合，顺带标记历史文件中的同机型重名。
                    foreach (var r in recipes)
                    {
                        var errors = RecipeValidator.Validate(r, Recipes);
                        r.MachineType = (r.MachineType ?? "").Trim();
                        // 历史数据修复：早期版本/手工构造的配方未写时间戳（0001-01-01），
                        // 加载时回填当前时间，避免 UI"更新于"显示 01-01 00:00（下次保存时落盘修正）。
                        if (r.CreatedAt == default) r.CreatedAt = DateTime.Now;
                        if (r.UpdatedAt == default) r.UpdatedAt = DateTime.Now;
                        if (errors.Count > 0)
                        {
                            r.LoadErrors.AddRange(errors);
                            Log.Warning("配方加载校验失败，已保留但禁止下发：{RecipeId} {RecipeName}（{Errors}）",
                                r.Id, r.Name, string.Join("；", errors));
                        }
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
        await _commitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<Recipe> snapshot;
            lock (_collectionLock)
                snapshot = Recipes.ToList();
            await PersistSnapshotAsync(snapshot).ConfigureAwait(false);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    public void SaveAll()
    {
        if (_remoteStore?.IsEnabled == true)
            throw new InvalidOperationException("Remote 模式不支持同步保存配方，请使用 SaveAllAsync。");

        _commitGate.Wait();
        try
        {
            List<Recipe> snapshot;
            lock (_collectionLock)
                snapshot = Recipes.ToList();
            PersistSnapshot(snapshot);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    public async Task<Recipe> CommitUpsertAsync(Recipe recipe)
    {
        Normalize(recipe);
        await _commitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = ProjectUpsert(recipe);
            await PersistSnapshotAsync(snapshot).ConfigureAwait(false);
            lock (_collectionLock)
                ApplyUpsertLocked(recipe);
            return recipe;
        }
        finally
        {
            _commitGate.Release();
        }
    }

    public async Task<bool> CommitDeleteAsync(string id)
    {
        await _commitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<Recipe> snapshot;
            lock (_collectionLock)
            {
                if (!_recipeMap.ContainsKey(id))
                    return false;
                snapshot = Recipes.Where(r => !string.Equals(r.Id, id, StringComparison.Ordinal)).ToList();
            }

            await PersistSnapshotAsync(snapshot).ConfigureAwait(false);
            lock (_collectionLock)
            {
                if (_recipeMap.TryRemove(id, out var existing))
                    Recipes.Remove(existing);
            }
            return true;
        }
        finally
        {
            _commitGate.Release();
        }
    }

    public void CommitReplaceAll(IEnumerable<Recipe> recipes)
    {
        if (_remoteStore?.IsEnabled == true)
            throw new InvalidOperationException("Remote 模式不支持同步提交配方，请使用 CommitReplaceAllAsync。");

        var list = NormalizeAll(recipes);
        _commitGate.Wait();
        try
        {
            PersistSnapshot(list);
            ReplaceAll(list);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    public async Task CommitReplaceAllAsync(IEnumerable<Recipe> recipes)
    {
        var list = NormalizeAll(recipes);
        await _commitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await PersistSnapshotAsync(list).ConfigureAwait(false);
            ReplaceAll(list);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    public Recipe Upsert(Recipe recipe)
    {
        Normalize(recipe);
        lock (_collectionLock)
            ApplyUpsertLocked(recipe);
        return recipe;
    }

    private List<Recipe> ProjectUpsert(Recipe recipe)
    {
        lock (_collectionLock)
        {
            var snapshot = new List<Recipe>(Recipes.Count + 1);
            var replaced = false;
            foreach (var existing in Recipes)
            {
                if (!replaced && string.Equals(existing.Id, recipe.Id, StringComparison.Ordinal))
                {
                    snapshot.Add(recipe);
                    replaced = true;
                }
                else
                {
                    snapshot.Add(existing);
                }
            }
            if (!replaced) snapshot.Add(recipe);
            return snapshot;
        }
    }

    private void ApplyUpsertLocked(Recipe recipe)
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

    private static void Normalize(Recipe recipe)
    {
        recipe.UpdatedAt = DateTime.Now;
        if (recipe.CreatedAt == default) recipe.CreatedAt = DateTime.Now;
        recipe.MachineType = (recipe.MachineType ?? "").Trim();
    }

    private static List<Recipe> NormalizeAll(IEnumerable<Recipe> recipes)
    {
        var concrete = recipes.ToList();
        foreach (var recipe in concrete)
        {
            recipe.MachineType = (recipe.MachineType ?? "").Trim();
            if (recipe.CreatedAt == default) recipe.CreatedAt = DateTime.Now;
            if (recipe.UpdatedAt == default) recipe.UpdatedAt = DateTime.Now;
        }
        return concrete;
    }

    private async Task PersistSnapshotAsync(IReadOnlyList<Recipe> snapshot)
    {
        if (_remoteStore?.IsEnabled == true)
        {
            await _remoteStore.SaveRecipesAsync(snapshot).ConfigureAwait(false);
            return;
        }
        PersistSnapshot(snapshot);
    }

    private void PersistSnapshot(IReadOnlyList<Recipe> snapshot)
    {
        _appSettings.EnsureDirectory();
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        AtomicFileWriter.Write(FilePath, json);
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
                if (recipe.CreatedAt == default) recipe.CreatedAt = DateTime.Now;
                if (recipe.UpdatedAt == default) recipe.UpdatedAt = DateTime.Now;
                Recipes.Add(recipe);
                _recipeMap[recipe.Id] = recipe;
            }
        }
    }
}
