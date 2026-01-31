using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.WebSockets;

namespace CodeBridge.Services;

/// <summary>
/// 终端 WebSocket 模块 - 处理远程终端连接
/// </summary>
public class TerminalWebSocketModule : WebSocketModule
{
    private readonly RemoteAuthService _authService;
    private readonly Func<IEnumerable<(string Id, string Name)>> _getTabsFunc;
    private readonly Action<string, string> _sendInputFunc;
    private readonly Action<string, int, int> _resizeFunc;

    /// <summary>
    /// 连接 -> 订阅的标签 ID
    /// </summary>
    private readonly ConcurrentDictionary<IWebSocketContext, string> _subscriptions = new();

    /// <summary>
    /// 连接 -> 最后心跳时间
    /// </summary>
    private readonly ConcurrentDictionary<IWebSocketContext, DateTime> _lastHeartbeat = new();

    /// <summary>
    /// 保存每个 Tab 的 PC 端原始尺寸 (tabId -> (cols, rows))
    /// </summary>
    private readonly ConcurrentDictionary<string, (int Cols, int Rows)> _pcSizes = new();

    /// <summary>
    /// 心跳检测定时器
    /// </summary>
    private readonly Timer _heartbeatTimer;

    /// <summary>
    /// 心跳超时时间（秒）
    /// </summary>
    private const int HeartbeatTimeoutSeconds = 30;

    /// <summary>
    /// 当前是否有移动端连接
    /// </summary>
    public bool HasMobileConnection => _subscriptions.Count > 0;

    /// <summary>
    /// 连接数变化事件
    /// </summary>
    public event Action<int>? ConnectionCountChanged;

    /// <summary>
    /// 远程输入事件（tabId, input）
    /// </summary>
    public event Action<string, string>? RemoteInputReceived;

    /// <summary>
    /// 请求初始化标签事件（tabId）
    /// 返回值：(成功, 是否之前已就绪)
    /// </summary>
    public event Func<string, Task<(bool Success, bool AlreadyReady)>>? InitTabRequested;

    public TerminalWebSocketModule(
        string urlPath,
        RemoteAuthService authService,
        Func<IEnumerable<(string Id, string Name)>> getTabsFunc,
        Action<string, string> sendInputFunc,
        Action<string, int, int> resizeFunc)
        : base(urlPath, true)
    {
        _authService = authService;
        _getTabsFunc = getTabsFunc;
        _sendInputFunc = sendInputFunc;
        _resizeFunc = resizeFunc;

        // 每 10 秒检查一次心跳
        _heartbeatTimer = new Timer(CheckHeartbeats, null, 10000, 10000);
    }

    /// <summary>
    /// 检查所有连接的心跳，超时则断开
    /// </summary>
    private void CheckHeartbeats(object? state)
    {
        try
        {
            var now = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(HeartbeatTimeoutSeconds);
            var timedOutConnections = new List<IWebSocketContext>();

            foreach (var kvp in _lastHeartbeat)
            {
                if (now - kvp.Value > timeout)
                {
                    timedOutConnections.Add(kvp.Key);
                }
            }

            foreach (var context in timedOutConnections)
            {
                System.Diagnostics.Debug.WriteLine($"[Heartbeat] 连接超时，强制断开: {context.Id}");
                try
                {
                    // 清理连接
                    _lastHeartbeat.TryRemove(context, out _);
                    _subscriptions.TryRemove(context, out _);
                    _authService.RemoveConnection(context.Id);
                    context.WebSocket.CloseAsync();
                }
                catch { }
            }

            // 如果有超时断开，检查是否需要恢复 PC 尺寸
            if (timedOutConnections.Count > 0)
            {
                ConnectionCountChanged?.Invoke(_subscriptions.Count);

                if (_subscriptions.Count == 0)
                {
                    RestorePcSizes();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Heartbeat] CheckHeartbeats 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 恢复所有 Tab 的 PC 尺寸
    /// </summary>
    private void RestorePcSizes()
    {
        foreach (var kvp in _pcSizes)
        {
            System.Diagnostics.Debug.WriteLine($"[RemoteResize] 恢复 PC 尺寸: TabId={kvp.Key}, {kvp.Value.Cols}x{kvp.Value.Rows}");
            try
            {
                _resizeFunc(kvp.Key, kvp.Value.Cols, kvp.Value.Rows);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteResize] 恢复尺寸失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 设置 PC 端的终端尺寸（供桌面端调用）
    /// </summary>
    public void SetPcSize(string tabId, int cols, int rows)
    {
        _pcSizes[tabId] = (cols, rows);
        System.Diagnostics.Debug.WriteLine($"[RemoteResize] 保存 PC 尺寸: TabId={tabId}, {cols}x{rows}");
    }

    protected override Task OnClientConnectedAsync(IWebSocketContext context)
    {
        // 从查询参数获取 token 和 tab
        var query = context.RequestUri.Query;
        var queryParams = System.Web.HttpUtility.ParseQueryString(query);
        var token = queryParams["token"] ?? string.Empty;
        var tabId = queryParams["tab"] ?? string.Empty;

        var connectionId = context.Id;

        // 验证 Token
        if (!_authService.TryAuthenticate(connectionId, token))
        {
            // 发送认证失败消息并关闭连接
            _ = SendAsync(context, JsonSerializer.Serialize(new
            {
                type = "error",
                message = "Authentication failed"
            }));
            context.WebSocket.CloseAsync();
            return Task.CompletedTask;
        }

        // 记录心跳时间
        _lastHeartbeat[context] = DateTime.UtcNow;

        // 订阅标签
        if (!string.IsNullOrEmpty(tabId))
        {
            _subscriptions[context] = tabId;
        }

        // 发送标签列表
        SendTabsList(context);

        // 通知连接数变化
        ConnectionCountChanged?.Invoke(_subscriptions.Count);

        System.Diagnostics.Debug.WriteLine($"[RemoteConnect] 移动端已连接，当前连接数: {_subscriptions.Count}");

        return Task.CompletedTask;
    }

    protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
    {
        // 清理连接
        _subscriptions.TryRemove(context, out _);
        _lastHeartbeat.TryRemove(context, out _);
        _authService.RemoveConnection(context.Id);

        // 通知连接数变化
        ConnectionCountChanged?.Invoke(_subscriptions.Count);

        System.Diagnostics.Debug.WriteLine($"[RemoteDisconnect] 移动端已断开，当前连接数: {_subscriptions.Count}");

        // 如果没有移动端连接了，恢复所有 Tab 的 PC 尺寸
        if (_subscriptions.Count == 0)
        {
            RestorePcSizes();
        }

        return Task.CompletedTask;
    }

    protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
    {
        var message = System.Text.Encoding.UTF8.GetString(buffer);

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                return Task.CompletedTask;

            var type = typeElement.GetString();

            // 更新心跳时间（任何消息都算心跳）
            _lastHeartbeat[context] = DateTime.UtcNow;

            switch (type)
            {
                case "input":
                    HandleInput(context, root);
                    break;
                case "resize":
                    HandleResize(context, root);
                    break;
                case "switch_tab":
                    HandleSwitchTab(context, root);
                    break;
                case "get_tabs":
                    SendTabsList(context);
                    break;
                case "ping":
                    // 心跳响应
                    _ = SendAsync(context, JsonSerializer.Serialize(new { type = "pong" }));
                    break;
                case "init_tab":
                    _ = HandleInitTab(context, root);
                    break;
            }
        }
        catch
        {
            // 忽略无效消息
        }

        return Task.CompletedTask;
    }

    private void HandleInput(IWebSocketContext context, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataElement))
            return;

        var input = dataElement.GetString();
        if (string.IsNullOrEmpty(input))
            return;

        // 获取当前订阅的标签
        if (_subscriptions.TryGetValue(context, out var tabId) && !string.IsNullOrEmpty(tabId))
        {
            _sendInputFunc(tabId, input);
            RemoteInputReceived?.Invoke(tabId, input);
        }
    }

    private void HandleResize(IWebSocketContext context, JsonElement root)
    {
        if (!root.TryGetProperty("cols", out var colsElement) || !root.TryGetProperty("rows", out var rowsElement))
            return;

        var cols = colsElement.GetInt32();
        var rows = rowsElement.GetInt32();

        // 获取 tabId
        string? tabId = null;
        if (root.TryGetProperty("tabId", out var tabIdElement))
        {
            tabId = tabIdElement.GetString();
        }

        if (string.IsNullOrEmpty(tabId))
        {
            _subscriptions.TryGetValue(context, out tabId);
        }

        if (string.IsNullOrEmpty(tabId))
            return;

        System.Diagnostics.Debug.WriteLine($"[RemoteResize] 移动端请求 resize: TabId={tabId}, {cols}x{rows}");

        // 执行 resize
        _resizeFunc(tabId, cols, rows);
    }

    private async Task HandleInitTab(IWebSocketContext context, JsonElement root)
    {
        if (!root.TryGetProperty("tabId", out var tabIdElement))
            return;

        var tabId = tabIdElement.GetString();
        if (string.IsNullOrEmpty(tabId))
            return;

        System.Diagnostics.Debug.WriteLine($"[RemoteInit] 移动端请求初始化终端: TabId={tabId}");

        // 发送初始化中状态
        await SendAsync(context, JsonSerializer.Serialize(new
        {
            type = "tab_status",
            tabId,
            status = "initializing"
        }));

        // 请求初始化
        var (success, alreadyReady) = (false, false);
        if (InitTabRequested != null)
        {
            try
            {
                (success, alreadyReady) = await InitTabRequested(tabId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteInit] 初始化失败: {ex.Message}");
            }
        }

        // 发送初始化结果
        await SendAsync(context, JsonSerializer.Serialize(new
        {
            type = "tab_status",
            tabId,
            status = success ? "ready" : "failed"
        }));
    }

    private void HandleSwitchTab(IWebSocketContext context, JsonElement root)
    {
        if (!root.TryGetProperty("tabId", out var tabIdElement))
            return;

        var tabId = tabIdElement.GetString();
        if (!string.IsNullOrEmpty(tabId))
        {
            _subscriptions[context] = tabId;

            // 确认切换
            _ = SendAsync(context, JsonSerializer.Serialize(new
            {
                type = "tab_switched",
                tabId
            }));

            // 自动触发初始化检查（异步执行）
            _ = TryInitTabAsync(context, tabId);
        }
    }

    /// <summary>
    /// 尝试初始化标签（如果未初始化则触发初始化）
    /// </summary>
    private async Task TryInitTabAsync(IWebSocketContext context, string tabId)
    {
        if (InitTabRequested == null) return;

        // 请求初始化（由 MainWindowViewModel 判断是否需要初始化）
        var (success, alreadyReady) = (false, false);
        try
        {
            (success, alreadyReady) = await InitTabRequested(tabId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RemoteInit] 初始化失败: {ex.Message}");
        }

        // 如果之前已就绪，不发送任何状态（避免闪烁）
        if (alreadyReady)
        {
            System.Diagnostics.Debug.WriteLine($"[RemoteInit] 标签已就绪，跳过状态通知: {tabId}");
            return;
        }

        // 发送初始化结果（仅在需要初始化时）
        await SendAsync(context, JsonSerializer.Serialize(new
        {
            type = "tab_status",
            tabId,
            status = success ? "ready" : "failed"
        }));
    }

    private void SendTabsList(IWebSocketContext context)
    {
        var tabs = _getTabsFunc().Select(t => new { id = t.Id, name = t.Name }).ToList();
        _ = SendAsync(context, JsonSerializer.Serialize(new
        {
            type = "tabs",
            tabs
        }));
    }

    /// <summary>
    /// 广播终端输出到订阅该标签的所有连接
    /// </summary>
    public async Task BroadcastOutputAsync(string tabId, string output)
    {
        var message = JsonSerializer.Serialize(new
        {
            type = "output",
            tabId,
            data = output
        });

        var tasks = new List<Task>();

        foreach (var kvp in _subscriptions)
        {
            if (kvp.Value == tabId)
            {
                tasks.Add(SendAsync(kvp.Key, message));
            }
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 广播标签列表更新
    /// </summary>
    public async Task BroadcastTabsUpdateAsync()
    {
        var tabs = _getTabsFunc().Select(t => new { id = t.Id, name = t.Name }).ToList();
        var message = JsonSerializer.Serialize(new
        {
            type = "tabs",
            tabs
        });

        await BroadcastAsync(message);
    }
}
