using System.IO;
using System.Text.Json;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Resources;
using Microsoft.Extensions.Logging;
using System.Windows;

namespace MainAPP.Services;

/// <summary>
/// 配方 JSON 导入/导出服务：文件格式与 recipes.json 落盘格式同构（备份文件可直接当导入文件）。
/// 导入采用"合并 by Id"语义（Upsert）：只新增/更新文件中的配方，不删除本地任何配方；
/// 校验未通过（数据非法/与本地同机型重名）的条目跳过并汇总计数。
/// Remote 模式经 RecipeStore.RemotePersistenceHook 自动落盘 Collector 侧 recipes.json。
/// </summary>
public class RecipeJsonIOService(IRecipeStore recipeStore, IDialogService dialog, ILogger<RecipeJsonIOService> logger)
{
    // 文件对话框过滤器（三语资源：JSON 文件|*.json|所有文件|*.*）
    private static string RecipeFileFilter => Strings.K695;

    private readonly IRecipeStore _recipeStore = recipeStore;
    private readonly IDialogService _dialog = dialog;
    private readonly ILogger<RecipeJsonIOService> _logger = logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>导出当前全部配方到用户选择的 JSON 文件；空配方库提示不导出。返回是否导出成功（取消/失败 = false）。</summary>
    public bool ExportRecipes()
    {
        if (_recipeStore.Recipes.Count == 0)
        {
            _dialog.NotifyWarning(Strings.K693);
            return false;
        }
        var path = _dialog.ShowSaveFileDialog(Strings.K690, "recipes.json", RecipeFileFilter);
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            // 与 RecipeManagerViewModel.RefreshRecipes 同模式：UI 线程直接读绑定集合
            var snapshot = _recipeStore.Recipes.ToList();
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            AppSettings.WriteFileAtomically(path, json);
            _dialog.NotifySuccess(string.Format(Strings.F319, snapshot.Count));
            _logger.LogInformation("配方已导出：{Count} 条 → {Path}", snapshot.Count, path);
            return true;
        }
        catch (Exception ex)
        {
            _dialog.NotifyError(string.Format(Strings.F320, ex.Message));
            _logger.LogError(ex, "配方导出失败");
            return false;
        }
    }

    /// <summary>从用户选择的 JSON 文件导入配方（合并 by Id）。返回成功导入条数；取消/失败/全部被跳过 = 0。</summary>
    public async Task<int> ImportRecipesAsync()
    {
        var path = _dialog.ShowOpenFileDialog(Strings.K691, RecipeFileFilter);
        if (string.IsNullOrEmpty(path)) return 0;

        List<Recipe> imported;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            imported = JsonSerializer.Deserialize<List<Recipe>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _dialog.NotifyError(string.Format(Strings.F321, ex.Message));
            _logger.LogError(ex, "配方导入文件解析失败：{Path}", path);
            return 0;
        }

        if (imported.Count == 0)
        {
            _dialog.NotifyWarning(Strings.K693);
            return 0;
        }

        // 逐条校验（existing = 本地 + 已通过校验的导入项，顺带拦截与本地/文件内重复的配方名）
        var existing = new List<Recipe>(_recipeStore.Recipes);
        var valid = new List<Recipe>();
        var skipped = 0;
        var seenFileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in imported)
        {
            // 与保存口径对齐：Name/MachineType 去空格，Bool 值归一化，文件内 Id 去重
            r.Name = r.Name?.Trim() ?? "";
            r.MachineType = (r.MachineType ?? "").Trim();
            foreach (var item in r.Items)
            {
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
            }
            if (!seenFileIds.Add(r.Id)) { skipped++; continue; }

            var errors = RecipeValidator.Validate(r, existing);
            if (errors.Count > 0)
            {
                _logger.LogWarning("导入配方校验失败，已跳过：{RecipeId} {RecipeName}（{Errors}）",
                    r.Id, r.Name, string.Join("；", errors));
                skipped++;
                continue;
            }
            valid.Add(r);
            existing.Add(r);
        }

        if (valid.Count == 0)
        {
            _dialog.NotifyWarning(string.Format(Strings.F322, imported.Count));
            return 0;
        }

        var confirm = _dialog.Show(string.Format(Strings.F317, valid.Count, skipped),
            Strings.K691, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return 0;

        // 批量合并：保留本地全部配方，按 Id 覆盖/追加导入项；一次 ReplaceAll 触发一次集合刷新，避免逐条 Upsert O(n²)
        var merged = new List<Recipe>(_recipeStore.Recipes);
        foreach (var r in valid)
        {
            var index = merged.FindIndex(x => string.Equals(x.Id, r.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                // 更新已有配方：无条件保留本地 CreatedAt，避免导入文件的时间戳覆盖原始创建时间
                r.CreatedAt = merged[index].CreatedAt;
                merged[index] = r;
            }
            else merged.Add(r);
        }
        _recipeStore.ReplaceAll(merged);
        await _recipeStore.SaveAllAsync();
        _logger.LogInformation("配方导入完成：{Imported} 条（跳过 {Skipped} 条），来源 {Path}", valid.Count, skipped, path);
        _dialog.NotifySuccess(string.Format(Strings.F318, valid.Count, skipped));
        return valid.Count;
    }
}
