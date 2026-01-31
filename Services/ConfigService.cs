using System;
using System.IO;
using System.Text.Json;
using CodeBridge.Models;

namespace CodeBridge.Services;

/// <summary>
/// 配置持久化服务
/// </summary>
public class ConfigService
{
    private readonly string _configPath;

    public ConfigService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "CodeBridge");
        Directory.CreateDirectory(appFolder);
        _configPath = Path.Combine(appFolder, "config.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_configPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(_configPath, json);
    }
}
