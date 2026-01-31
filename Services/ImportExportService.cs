using System.IO;
using System.Linq;
using System.Text.Json;
using CodeBridge.Models;

namespace CodeBridge.Services;

/// <summary>
/// 配置导入/导出服务
/// </summary>
public class ImportExportService
{
    public void Export(AppSettings settings, string filePath)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(filePath, json);
    }

    public AppSettings Import(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public AppSettings Merge(AppSettings current, AppSettings imported)
    {
        // 按 ID 去重合并
        foreach (var tab in imported.Tabs)
        {
            if (!current.Tabs.Any(t => t.Id == tab.Id))
                current.Tabs.Add(tab);
        }

        foreach (var history in imported.History)
        {
            if (!current.History.Any(h => h.Id == history.Id))
                current.History.Add(history);
        }

        return current;
    }
}
