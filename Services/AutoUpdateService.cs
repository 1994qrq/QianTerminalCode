using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodeBridge.Services;

/// <summary>
/// GitHub Release 自动更新服务
/// </summary>
public class AutoUpdateService
{
    private const string GitHubOwner = "1994qrq";
    private const string GitHubRepo = "QianTerminalCode";
    private const string GitHubApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    private readonly HttpClient _httpClient;

    /// <summary>
    /// 当前版本
    /// </summary>
    public string CurrentVersion { get; }

    /// <summary>
    /// 最新版本信息
    /// </summary>
    public ReleaseInfo? LatestRelease { get; private set; }

    /// <summary>
    /// 是否有新版本
    /// </summary>
    public bool HasUpdate => LatestRelease != null &&
        CompareVersions(LatestRelease.Version, CurrentVersion) > 0;

    /// <summary>
    /// 下载进度事件 (0-100)
    /// </summary>
    public event Action<int>? DownloadProgressChanged;

    /// <summary>
    /// 状态消息事件
    /// </summary>
    public event Action<string>? StatusChanged;

    public AutoUpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "CodeBridge-AutoUpdater");

        // 获取当前版本
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        CurrentVersion = version ?? "1.0.0";
    }

    /// <summary>
    /// 检查更新
    /// </summary>
    public async Task<bool> CheckForUpdateAsync()
    {
        try
        {
            StatusChanged?.Invoke("正在检查更新...");

            var response = await _httpClient.GetStringAsync(GitHubApiUrl);
            var json = JsonDocument.Parse(response).RootElement;

            var tagName = json.GetProperty("tag_name").GetString() ?? "";
            var version = tagName.TrimStart('v', 'V');
            var body = json.GetProperty("body").GetString() ?? "";
            var publishedAt = json.GetProperty("published_at").GetDateTime();
            var htmlUrl = json.GetProperty("html_url").GetString() ?? "";

            // 查找 exe 下载链接
            string? downloadUrl = null;
            long fileSize = 0;

            if (json.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        fileSize = asset.GetProperty("size").GetInt64();
                        break;
                    }
                }
            }

            LatestRelease = new ReleaseInfo
            {
                Version = version,
                TagName = tagName,
                ReleaseNotes = body,
                PublishedAt = publishedAt,
                DownloadUrl = downloadUrl,
                FileSize = fileSize,
                HtmlUrl = htmlUrl
            };

            if (HasUpdate)
            {
                StatusChanged?.Invoke($"发现新版本: v{LatestRelease.Version}");
                return true;
            }
            else
            {
                StatusChanged?.Invoke("已是最新版本");
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            StatusChanged?.Invoke($"网络错误: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"检查更新失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 下载并安装更新
    /// </summary>
    public async Task<bool> DownloadAndInstallAsync()
    {
        if (LatestRelease?.DownloadUrl == null)
        {
            StatusChanged?.Invoke("没有可用的下载链接");
            return false;
        }

        try
        {
            StatusChanged?.Invoke("正在下载更新...");

            // 下载到临时目录
            var tempDir = Path.Combine(Path.GetTempPath(), "CodeBridge_Update");
            Directory.CreateDirectory(tempDir);

            var fileName = $"CodeBridge_v{LatestRelease.Version}.exe";
            var tempFile = Path.Combine(tempDir, fileName);

            // 下载文件（带进度）
            using var response = await _httpClient.GetAsync(LatestRelease.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? LatestRelease.FileSize;

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    var progress = (int)(totalRead * 100 / totalBytes);
                    DownloadProgressChanged?.Invoke(progress);
                }
            }

            StatusChanged?.Invoke("下载完成，准备安装...");
            DownloadProgressChanged?.Invoke(100);

            // 创建更新脚本
            var scriptPath = Path.Combine(tempDir, "update.bat");
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var script = $@"@echo off
chcp 65001 >nul
echo 正在更新 CodeBridge...
timeout /t 2 /nobreak >nul
copy /Y ""{tempFile}"" ""{currentExe}""
if %errorlevel% equ 0 (
    echo 更新成功！正在重启...
    start """" ""{currentExe}""
) else (
    echo 更新失败，请手动替换文件
    echo 新版本位置: {tempFile}
    pause
)
del ""%~f0""
";
            await File.WriteAllTextAsync(scriptPath, script, System.Text.Encoding.GetEncoding("GBK"));

            // 启动更新脚本并退出当前程序
            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                CreateNoWindow = false
            });

            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"下载失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 在浏览器中打开 Release 页面
    /// </summary>
    public void OpenReleasePage()
    {
        var url = LatestRelease?.HtmlUrl ?? $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases";
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// 比较版本号
    /// </summary>
    private static int CompareVersions(string v1, string v2)
    {
        var parts1 = v1.Split('.', '-')[0..3];
        var parts2 = v2.Split('.', '-')[0..3];

        for (int i = 0; i < Math.Max(parts1.Length, parts2.Length); i++)
        {
            var p1 = i < parts1.Length && int.TryParse(parts1[i], out var n1) ? n1 : 0;
            var p2 = i < parts2.Length && int.TryParse(parts2[i], out var n2) ? n2 : 0;

            if (p1 > p2) return 1;
            if (p1 < p2) return -1;
        }
        return 0;
    }
}

/// <summary>
/// Release 信息
/// </summary>
public class ReleaseInfo
{
    public string Version { get; set; } = "";
    public string TagName { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public string? DownloadUrl { get; set; }
    public long FileSize { get; set; }
    public string HtmlUrl { get; set; } = "";

    public string FileSizeFormatted => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:F1} KB",
        _ => $"{FileSize / 1024.0 / 1024.0:F1} MB"
    };
}
