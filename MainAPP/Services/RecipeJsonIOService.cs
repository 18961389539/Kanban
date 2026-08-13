using System.IO;
using System.Text.Json;
using Kanban.Core.Data;
using Kanban.Core.Models;
using Kanban.Core.Services;
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
        foreach (var r in imported)
        {
            var errors = RecipeValidator.Validate(r, existing);
            if (errors.Count > 0)
            {
                _logger.LogWarning("导入配方校验失败，已跳过：{RecipeId} {RecipeName}（{Errors}）",
                    r.Id, r.Name, string.Join("；", errors));
                continue;
            }
            valid.Add(r);
            existing.Add(r);
        }

        var skipped = imported.Count - valid.Count;
        if (valid.Count == 0)
        {
            _dialog.NotifyWarning(string.Format(Strings.F322, imported.Count));
            return 0;
        }

        var confirm = _dialog.Show(string.Format(Strings.F317, valid.Count, skipped),
            Strings.K691, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return 0;

        foreach (var r in valid)
            _recipeStore.Upsert(r);
        await _recipeStore.SaveAllAsync();
        _logger.LogInformation("配方导入完成：{Imported} 条（跳过 {Skipped} 条），来源 {Path}", valid.Count, skipped, path);
        _dialog.NotifySuccess(string.Format(Strings.F318, valid.Count, skipped));
        return valid.Count;
    }
}
