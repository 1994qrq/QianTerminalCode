using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CodeBridge.Services;

/// <summary>
/// 远程控制主服务 - 协调 Web 服务器、认证和隧道
/// </summary>
public class RemoteControlService : IDisposable
{
    private readonly RemoteAuthService _authService;
    private readonly WebServerService _webServer;
    private readonly CloudflareTunnelService _tunnelService;
    private TerminalOutputBuffer? _outputBuffer;

    /// <summary>
    /// 远程控制是否启用
    /// </summary>
    public bool IsEnabled { get; private set; }

    /// <summary>
    /// Web 服务器是否运行中
    /// </summary>
    public bool IsServerRunning => _webServer.IsRunning;

    /// <summary>
    /// 隧道是否运行中
    /// </summary>
    public bool IsTunnelRunning => _tunnelService.IsRunning;

    /// <summary>
    /// cloudflared 是否已安装
    /// </summary>
    public bool IsCloudflaredInstalled => _tunnelService.IsInstalled;

    /// <summary>
    /// 本地访问地址
    /// </summary>
    public string LocalUrl => _webServer.LocalUrl;

    /// <summary>
    /// 公网访问地址
    /// </summary>
    public string PublicUrl => _tunnelService.PublicUrl;

    /// <summary>
    /// 当前访问码
    /// </summary>
    public string AccessToken => _authService.CurrentToken;

    /// <summary>
    /// 访问码过期时间
    /// </summary>
    public DateTime TokenExpiry => _authService.TokenExpiry;

    /// <summary>
    /// 当前连接数
    /// </summary>
    public int ConnectionCount { get; private set; }

    /// <summary>
    /// 服务端口
    /// </summary>
    public int Port => _webServer.Port;

    /// <summary>
    /// 连接数变化事件
    /// </summary>
    public event Action<int>? ConnectionCountChanged;

    /// <summary>
    /// 隧道状态变化事件
    /// </summary>
    public event Action<TunnelStatus>? TunnelStatusChanged;

    /// <summary>
    /// 公网 URL 获取事件
    /// </summary>
    public event Action<string>? PublicUrlObtained;

    /// <summary>
    /// 错误事件
    /// </summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// 远程输入事件（tabId, input）
    /// </summary>
    public event Action<string, string>? RemoteInputReceived;

    /// <summary>
    /// 请求初始化标签事件（tabId）- 当移动端请求初始化未打开的标签时触发
    /// 返回值：(成功, 是否之前已就绪)
    /// </summary>
    public event Func<string, Task<(bool Success, bool AlreadyReady)>>? InitTabRequested;

    /// <summary>
    /// 获取标签列表委托
    /// </summary>
    public Func<IEnumerable<(string Id, string Name)>>? GetTabsFunc
    {
        get => _webServer.GetTabsFunc;
        set => _webServer.GetTabsFunc = value;
    }

    /// <summary>
    /// 发送输入委托
    /// </summary>
    public Action<string, string>? SendInputFunc
    {
        get => _webServer.SendInputFunc;
        set => _webServer.SendInputFunc = value;
    }

    /// <summary>
    /// 调整终端大小委托 (tabId, cols, rows)
    /// </summary>
    public Action<string, int, int>? ResizeFunc
    {
        get => _webServer.ResizeFunc;
        set => _webServer.ResizeFunc = value;
    }

    public RemoteControlService()
    {
        _authService = new RemoteAuthService();
        _webServer = new WebServerService(_authService);
        _tunnelService = new CloudflareTunnelService();

        // 订阅事件
        _webServer.ConnectionCountChanged += count =>
        {
            ConnectionCount = count;
            ConnectionCountChanged?.Invoke(count);
        };

        _webServer.RemoteInputReceived += (tabId, input) =>
        {
            RemoteInputReceived?.Invoke(tabId, input);
        };

        _webServer.InitTabRequested += tabId =>
        {
            return InitTabRequested?.Invoke(tabId) ?? Task.FromResult((false, false));
        };

        _tunnelService.StatusChanged += status => TunnelStatusChanged?.Invoke(status);
        _tunnelService.PublicUrlObtained += url => PublicUrlObtained?.Invoke(url);
        _tunnelService.ErrorOccurred += msg => ErrorOccurred?.Invoke(msg);
    }

    /// <summary>
    /// 启动远程控制服务
    /// </summary>
    public async Task StartAsync(int port = 8765)
    {
        if (IsEnabled) return;

        try
        {
            await _webServer.StartAsync(port);

            // 创建输出缓冲器（50ms 合并一次输出，禁用过滤保持原始 ANSI）
            _outputBuffer = new TerminalOutputBuffer(
                async (tabId, output) => await _webServer.BroadcastOutputAsync(tabId, output),
                50,
                enableMobileFilter: false);  // 禁用过滤，xterm.js 需要原始 ANSI 数据

            IsEnabled = true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"启动 Web 服务器失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 停止远程控制服务
    /// </summary>
    public void Stop()
    {
        StopTunnel();
        _outputBuffer?.Dispose();
        _outputBuffer = null;
        _webServer.Stop();
        IsEnabled = false;
    }

    /// <summary>
    /// 启动 Cloudflare Tunnel
    /// </summary>
    public async Task StartTunnelAsync()
    {
        if (!IsEnabled)
        {
            ErrorOccurred?.Invoke("请先启动远程控制服务");
            return;
        }

        await _tunnelService.StartAsync(LocalUrl);
    }

    /// <summary>
    /// 停止 Cloudflare Tunnel
    /// </summary>
    public void StopTunnel()
    {
        _tunnelService.Stop();
    }

    /// <summary>
    /// 刷新访问码
    /// </summary>
    public string RefreshToken()
    {
        return _authService.RefreshToken();
    }

    /// <summary>
    /// 广播终端输出（通过缓冲器合并后发送）
    /// </summary>
    public Task BroadcastOutputAsync(string tabId, string output)
    {
        if (IsEnabled && _outputBuffer != null)
        {
            _outputBuffer.Append(tabId, output);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 广播标签列表更新
    /// </summary>
    public async Task BroadcastTabsUpdateAsync()
    {
        if (IsEnabled)
        {
            await _webServer.BroadcastTabsUpdateAsync();
        }
    }

    /// <summary>
    /// 打开 cloudflared 下载页面
    /// </summary>
    public void OpenCloudflaredDownload()
    {
        CloudflareTunnelService.OpenDownloadPage();
    }

    /// <summary>
    /// 使用 winget 安装 cloudflared
    /// </summary>
    public Task<bool> InstallCloudflaredAsync()
    {
        return CloudflareTunnelService.InstallWithWingetAsync();
    }

    /// <summary>
    /// 设置 PC 端终端尺寸（供桌面端调用，断开移动端时恢复）
    /// </summary>
    public void SetPcSize(string tabId, int cols, int rows)
    {
        _webServer.SetPcSize(tabId, cols, rows);
    }

    public void Dispose()
    {
        Stop();
        _tunnelService.Dispose();
        _webServer.Dispose();
    }
}
