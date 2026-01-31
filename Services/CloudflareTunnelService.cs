using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBridge.Services;

/// <summary>
/// Cloudflare Tunnel 服务 - 管理 cloudflared 进程
/// </summary>
public class CloudflareTunnelService : IDisposable
{
    private Process? _process;
    private CancellationTokenSource? _cts;
    private string _publicUrl = string.Empty;

    /// <summary>
    /// 隧道是否正在运行
    /// </summary>
    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>
    /// 公网访问地址
    /// </summary>
    public string PublicUrl => _publicUrl;

    /// <summary>
    /// cloudflared 是否已安装
    /// </summary>
    public bool IsInstalled => !string.IsNullOrEmpty(GetCloudflaredPath());

    /// <summary>
    /// 状态变化事件
    /// </summary>
    public event Action<TunnelStatus>? StatusChanged;

    /// <summary>
    /// 公网 URL 获取事件
    /// </summary>
    public event Action<string>? PublicUrlObtained;

    /// <summary>
    /// 错误事件
    /// </summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// 获取 cloudflared 可执行文件路径或命令名
    /// </summary>
    public string? GetCloudflaredPath()
    {
        Debug.WriteLine("[cloudflared 检测] 开始检测...");

        // 先刷新环境变量，确保能检测到新安装的程序
        RefreshEnvironmentVariables();

        // 获取当前进程的最新 PATH（刷新后的）
        var currentPath = Environment.GetEnvironmentVariable("Path") ?? "";
        Debug.WriteLine($"[cloudflared 检测] 当前进程 PATH 长度: {currentPath.Length}");

        // 方法1：使用 where 命令查找（显式注入最新 PATH）
        try
        {
            Debug.WriteLine("[cloudflared 检测] 方法1: 使用 where 命令查找");
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "cloudflared",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // 关键：显式注入最新的 PATH 到子进程环境
            psi.Environment["Path"] = currentPath;

            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(3000);

            Debug.WriteLine($"[cloudflared 检测] where 退出码: {process.ExitCode}");
            Debug.WriteLine($"[cloudflared 检测] where 输出: {output.Trim()}");
            if (!string.IsNullOrEmpty(error))
                Debug.WriteLine($"[cloudflared 检测] where 错误: {error.Trim()}");

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(firstLine))
                {
                    Debug.WriteLine($"[cloudflared 检测] 方法1 成功，找到: {firstLine.Trim()}");
                    return firstLine.Trim();
                }
            }
            Debug.WriteLine("[cloudflared 检测] 方法1 未找到");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cloudflared 检测] 方法1 异常: {ex.Message}");
        }

        // 方法2：直接尝试运行 cloudflared --version（显式注入最新 PATH）
        try
        {
            Debug.WriteLine("[cloudflared 检测] 方法2: 直接运行 cloudflared --version");
            var psi = new ProcessStartInfo
            {
                FileName = "cloudflared",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // 关键：显式注入最新的 PATH 到子进程环境
            psi.Environment["Path"] = currentPath;

            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            Debug.WriteLine($"[cloudflared 检测] cloudflared --version 退出码: {process.ExitCode}");
            Debug.WriteLine($"[cloudflared 检测] cloudflared --version 输出: {output.Trim()}");

            if (process.ExitCode == 0)
            {
                Debug.WriteLine("[cloudflared 检测] 方法2 成功，命令可用");
                return "cloudflared";
            }
            Debug.WriteLine("[cloudflared 检测] 方法2 失败，退出码非0");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cloudflared 检测] 方法2 异常: {ex.Message}");
        }

        Debug.WriteLine("[cloudflared 检测] 所有方法均未找到 cloudflared，返回 null");
        return null;
    }

    /// <summary>
    /// 启动隧道
    /// </summary>
    /// <param name="localUrl">本地服务 URL</param>
    public async Task StartAsync(string localUrl)
    {
        if (IsRunning) return;

        var cloudflaredPath = GetCloudflaredPath();
        if (string.IsNullOrEmpty(cloudflaredPath))
        {
            ErrorOccurred?.Invoke("cloudflared 未安装");
            StatusChanged?.Invoke(TunnelStatus.NotInstalled);
            return;
        }

        _cts = new CancellationTokenSource();
        _publicUrl = string.Empty;

        try
        {
            StatusChanged?.Invoke(TunnelStatus.Starting);

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = cloudflaredPath,
                    // 使用 http2 协议避免 QUIC 被代理软件拦截
                    Arguments = $"tunnel --url {localUrl} --protocol http2",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            // 设置环境变量，绕过代理（解决 Clash 等代理软件冲突）
            _process.StartInfo.EnvironmentVariables["NO_PROXY"] = "*";
            _process.StartInfo.EnvironmentVariables["no_proxy"] = "*";
            _process.StartInfo.EnvironmentVariables["HTTP_PROXY"] = "";
            _process.StartInfo.EnvironmentVariables["HTTPS_PROXY"] = "";
            _process.StartInfo.EnvironmentVariables["http_proxy"] = "";
            _process.StartInfo.EnvironmentVariables["https_proxy"] = "";

            _process.Exited += (s, e) =>
            {
                _publicUrl = string.Empty;
                StatusChanged?.Invoke(TunnelStatus.Stopped);
            };

            _process.Start();

            // 异步读取输出以获取公网 URL
            _ = ReadOutputAsync(_process, _cts.Token);

            // 等待 URL 获取（最多 30 秒）
            var timeout = DateTime.UtcNow.AddSeconds(30);
            while (string.IsNullOrEmpty(_publicUrl) && DateTime.UtcNow < timeout && IsRunning)
            {
                await Task.Delay(500, _cts.Token);
            }

            if (!string.IsNullOrEmpty(_publicUrl))
            {
                StatusChanged?.Invoke(TunnelStatus.Running);
            }
            else if (IsRunning)
            {
                ErrorOccurred?.Invoke("无法获取公网 URL");
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"启动隧道失败: {ex.Message}");
            StatusChanged?.Invoke(TunnelStatus.Error);
            Stop();
        }
    }

    /// <summary>
    /// 读取进程输出并解析 URL
    /// </summary>
    private async Task ReadOutputAsync(Process process, CancellationToken ct)
    {
        // URL 匹配正则（匹配 trycloudflare.com 域名）
        var urlRegex = new Regex(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled);

        try
        {
            // 主要从 stderr 读取（cloudflared 的日志输出到 stderr）
            while (!process.HasExited && !ct.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line == null) break;

                Debug.WriteLine($"[cloudflared] {line}");

                // 尝试匹配 URL
                var match = urlRegex.Match(line);
                if (match.Success && string.IsNullOrEmpty(_publicUrl))
                {
                    _publicUrl = match.Value;
                    PublicUrlObtained?.Invoke(_publicUrl);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取 cloudflared 输出失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 停止隧道
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();

        if (_process != null && !_process.HasExited)
        {
            try
            {
                // 在后台线程执行 Kill，避免阻塞 UI
                Task.Run(() =>
                {
                    try
                    {
                        _process?.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // 忽略
                    }
                });
            }
            catch
            {
                // 忽略
            }
        }

        _process?.Dispose();
        _process = null;
        _cts?.Dispose();
        _cts = null;
        _publicUrl = string.Empty;

        StatusChanged?.Invoke(TunnelStatus.Stopped);
    }

    /// <summary>
    /// 打开 cloudflared 下载页面
    /// </summary>
    public static void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/",
                UseShellExecute = true
            });
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 使用 winget 安装 cloudflared
    /// </summary>
    public static async Task<bool> InstallWithWingetAsync()
    {
        Debug.WriteLine("[cloudflared 安装] 开始使用 winget 安装...");
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = "install --id Cloudflare.cloudflared --source winget --accept-package-agreements --accept-source-agreements",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            Debug.WriteLine($"[cloudflared 安装] 执行命令: winget {process.StartInfo.Arguments}");
            process.Start();

            // 异步读取输出
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var output = await outputTask;
            var error = await errorTask;

            Debug.WriteLine($"[cloudflared 安装] 退出码: {process.ExitCode}");
            if (!string.IsNullOrWhiteSpace(output))
                Debug.WriteLine($"[cloudflared 安装] 输出: {output}");
            if (!string.IsNullOrWhiteSpace(error))
                Debug.WriteLine($"[cloudflared 安装] 错误: {error}");

            if (process.ExitCode == 0)
            {
                Debug.WriteLine("[cloudflared 安装] 安装成功！");
                // 刷新当前进程的环境变量
                RefreshEnvironmentVariables();
                return true;
            }
            else
            {
                Debug.WriteLine($"[cloudflared 安装] 安装失败，退出码: {process.ExitCode}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[cloudflared 安装] 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 刷新当前进程的环境变量（从注册表重新读取）
    /// </summary>
    private static void RefreshEnvironmentVariables()
    {
        Debug.WriteLine("[环境变量] 开始刷新...");
        try
        {
            // 使用 .NET API 获取，它会自动处理注册表视图和变量展开
            // Machine 环境变量（系统级）
            var machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
            // User 环境变量（用户级）
            var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            
            // Windows 的 PATH 构造规则是 <MachinePath>;<UserPath>
            // 注意：Environment.GetEnvironmentVariable 已经展开了其中的变量（如 %SystemRoot%, %USERPROFILE%）
            var newPath = $"{machinePath};{userPath}";

            // 更新当前进程的 PATH
            Environment.SetEnvironmentVariable("Path", newPath, EnvironmentVariableTarget.Process);

            Debug.WriteLine("[环境变量] 刷新成功！");
            Debug.WriteLine($"[环境变量] Path 长度: {newPath.Length}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[环境变量] 刷新失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// 隧道状态
/// </summary>
public enum TunnelStatus
{
    Stopped,
    Starting,
    Running,
    Error,
    NotInstalled
}
