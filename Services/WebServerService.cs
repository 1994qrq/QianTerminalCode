using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace CodeBridge.Services;

/// <summary>
/// 内嵌 Web 服务器服务 - 基于 EmbedIO
/// </summary>
public class WebServerService : IDisposable
{
    private WebServer? _server;
    private CancellationTokenSource? _cts;
    private readonly RemoteAuthService _authService;
    private TerminalWebSocketModule? _wsModule;

    /// <summary>
    /// 服务端口
    /// </summary>
    public int Port { get; private set; } = 8765;

    /// <summary>
    /// 服务是否正在运行
    /// </summary>
    public bool IsRunning => _server != null;

    /// <summary>
    /// 本地访问地址
    /// </summary>
    public string LocalUrl => $"http://localhost:{Port}";

    /// <summary>
    /// 连接数变化事件
    /// </summary>
    public event Action<int>? ConnectionCountChanged;

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
    /// 获取标签列表的委托
    /// </summary>
    public Func<IEnumerable<(string Id, string Name)>>? GetTabsFunc { get; set; }

    /// <summary>
    /// 发送输入的委托
    /// </summary>
    public Action<string, string>? SendInputFunc { get; set; }

    /// <summary>
    /// 调整终端大小的委托 (tabId, cols, rows)
    /// </summary>
    public Action<string, int, int>? ResizeFunc { get; set; }

    public WebServerService(RemoteAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>
    /// 启动 Web 服务器
    /// </summary>
    public async Task StartAsync(int port = 8765)
    {
        if (_server != null) return;

        Port = port;
        _cts = new CancellationTokenSource();

        _server = new WebServer(o => o
            .WithUrlPrefix($"http://*:{port}/")
            .WithMode(HttpListenerMode.EmbedIO))
            .WithCors()
            .WithModule(CreateWebSocketModule())
            .WithWebApi("/api", m => m.WithController(() => new RemoteApiController(_authService, GetTabsFunc)))
            .WithModule(new ActionModule("/", HttpVerbs.Get, ctx => ServeIndexPage(ctx)));

        try
        {
            _ = _server.RunAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Web 服务器启动失败: {ex.Message}");
            _server?.Dispose();
            _server = null;
            throw;
        }
    }

    /// <summary>
    /// 停止 Web 服务器
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _server?.Dispose();
        _server = null;
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// 创建 WebSocket 模块
    /// </summary>
    private TerminalWebSocketModule CreateWebSocketModule()
    {
        _wsModule = new TerminalWebSocketModule(
            "/ws/terminal",
            _authService,
            GetTabsFunc ?? (() => Array.Empty<(string, string)>()),
            SendInputFunc ?? ((_, _) => { }),
            ResizeFunc ?? ((_, _, _) => { }));

        _wsModule.ConnectionCountChanged += count => ConnectionCountChanged?.Invoke(count);
        _wsModule.RemoteInputReceived += (tabId, input) => RemoteInputReceived?.Invoke(tabId, input);
        _wsModule.InitTabRequested += tabId => InitTabRequested?.Invoke(tabId) ?? Task.FromResult((false, false));

        return _wsModule;
    }

    /// <summary>
    /// 广播终端输出
    /// </summary>
    public async Task BroadcastOutputAsync(string tabId, string output)
    {
        if (_wsModule != null)
        {
            await _wsModule.BroadcastOutputAsync(tabId, output);
        }
    }

    /// <summary>
    /// 广播标签列表更新
    /// </summary>
    public async Task BroadcastTabsUpdateAsync()
    {
        if (_wsModule != null)
        {
            await _wsModule.BroadcastTabsUpdateAsync();
        }
    }

    /// <summary>
    /// 设置 PC 端终端尺寸（供桌面端调用，断开移动端时恢复）
    /// </summary>
    public void SetPcSize(string tabId, int cols, int rows)
    {
        _wsModule?.SetPcSize(tabId, cols, rows);
    }

    /// <summary>
    /// 提供移动端终端页面
    /// </summary>
    private async Task ServeIndexPage(IHttpContext ctx)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";

        // 通过 URL 参数选择模式：?mode=xterm 使用 xterm.js，否则使用轻量级渲染器
        var query = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url.Query);
        var mode = query["mode"] ?? "lite";

        var html = mode == "xterm" ? GenerateXtermIndexHtml() : GenerateLiteIndexHtml();
        await ctx.SendStringAsync(html, "text/html", System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// 获取远程终端 HTML 页面
    /// </summary>
    private string GetRemoteIndexHtml()
    {
        // 尝试从嵌入资源或文件加载
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var htmlPath = Path.Combine(assemblyDir ?? ".", "Assets", "remote", "index.html");

        if (File.Exists(htmlPath))
        {
            return File.ReadAllText(htmlPath);
        }

        // 返回内置的轻量级 HTML
        return GenerateLiteIndexHtml();
    }

    /// <summary>
    /// 生成轻量级日志渲染器 HTML（方案 B - 推荐移动端使用）
    /// </summary>
    private string GenerateLiteIndexHtml()
    {
        return """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover">
    <title>远程终端</title>
    <script src="https://cdn.jsdelivr.net/npm/ansi_up@5.1.0/ansi_up.min.js"></script>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        html, body {
            width: 100%; height: 100%;
            background: #0a0a12;
            font-family: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, "Liberation Mono", monospace;
            font-size: 13px;
            color: #cccccc;
        }

        /* 认证页面 */
        #auth-page {
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            height: 100vh;
            padding: 20px;
        }

        .auth-container {
            background: linear-gradient(135deg, #12121a 0%, #1a1a2e 100%);
            border: 1px solid #00d4ff30;
            border-radius: 16px;
            padding: 40px;
            max-width: 360px;
            width: 100%;
            box-shadow: 0 0 40px #00d4ff10;
        }

        .auth-title {
            color: #00d4ff;
            font-size: 24px;
            text-align: center;
            margin-bottom: 8px;
            text-shadow: 0 0 20px #00d4ff40;
        }

        .auth-subtitle {
            color: #6a6a8a;
            font-size: 14px;
            text-align: center;
            margin-bottom: 30px;
        }

        .pin-input {
            display: flex;
            justify-content: center;
            gap: 8px;
            margin-bottom: 24px;
        }

        .pin-input input {
            width: 45px;
            height: 55px;
            background: #0a0a12;
            border: 2px solid #2a2a4a;
            border-radius: 8px;
            color: #00d4ff;
            font-size: 24px;
            font-weight: bold;
            text-align: center;
            outline: none;
        }

        .pin-input input:focus {
            border-color: #00d4ff;
            box-shadow: 0 0 15px #00d4ff40;
        }

        .auth-btn {
            width: 100%;
            padding: 14px;
            background: linear-gradient(135deg, #00d4ff 0%, #0099cc 100%);
            border: none;
            border-radius: 8px;
            color: #0a0a12;
            font-size: 16px;
            font-weight: bold;
            cursor: pointer;
        }

        .auth-btn:disabled { opacity: 0.5; }
        .auth-error { color: #ff4757; text-align: center; margin-top: 16px; font-size: 14px; }

        /* 终端页面 */
        #terminal-page {
            display: none;
            flex-direction: column;
            height: 100vh;
            height: 100dvh;
        }

        .toolbar {
            display: flex;
            align-items: center;
            padding: 8px 12px;
            background: #12121a;
            border-bottom: 1px solid #2a2a4a;
            gap: 8px;
            flex-shrink: 0;
        }

        .tab-select {
            flex: 1;
            padding: 8px 12px;
            background: #0a0a12;
            border: 1px solid #2a2a4a;
            border-radius: 6px;
            color: #cccccc;
            font-size: 14px;
        }

        .toolbar-btn {
            padding: 8px 12px;
            background: #1a1a2e;
            border: 1px solid #2a2a4a;
            border-radius: 6px;
            color: #00d4ff;
            font-size: 12px;
            cursor: pointer;
        }

        .status-indicator {
            width: 8px; height: 8px;
            border-radius: 50%;
            background: #16c60c;
            animation: pulse 2s infinite;
        }
        .status-indicator.disconnected { background: #ff4757; animation: none; }
        @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.5; } }

        /* 输出区域 */
        #output-container {
            flex: 1;
            overflow-y: auto;
            overflow-x: hidden;
            padding: 8px 12px;
            background: #0c0c0c;
            -webkit-overflow-scrolling: touch;
        }

        .line {
            min-height: 18px;
            line-height: 1.4;
            white-space: pre-wrap;
            word-break: break-all;
        }

        #current-line {
            min-height: 18px;
            line-height: 1.4;
            white-space: pre-wrap;
            word-break: break-all;
            border-left: 2px solid #00d4ff;
            padding-left: 4px;
            animation: blink 1s infinite;
        }
        @keyframes blink { 0%, 100% { border-color: #00d4ff; } 50% { border-color: transparent; } }

        /* 输入区域 */
        .input-area {
            display: flex;
            padding: 8px;
            background: #12121a;
            border-top: 1px solid #2a2a4a;
            gap: 8px;
            flex-shrink: 0;
            padding-bottom: max(8px, env(safe-area-inset-bottom));
        }

        #cmd-input {
            flex: 1;
            padding: 10px 12px;
            background: #0a0a12;
            border: 1px solid #2a2a4a;
            border-radius: 6px;
            color: #cccccc;
            font-family: inherit;
            font-size: 14px;
            outline: none;
        }

        #cmd-input:focus { border-color: #00d4ff; }

        .send-btn {
            padding: 10px 16px;
            background: #00d4ff;
            border: none;
            border-radius: 6px;
            color: #0a0a12;
            font-weight: bold;
            cursor: pointer;
        }

        /* 快捷键 */
        .shortcuts {
            display: flex;
            gap: 4px;
            padding: 4px 8px;
            background: #12121a;
            overflow-x: auto;
            flex-shrink: 0;
        }

        .shortcut-btn {
            padding: 6px 10px;
            background: #1a1a2e;
            border: 1px solid #2a2a4a;
            border-radius: 4px;
            color: #00d4ff;
            font-size: 11px;
            white-space: nowrap;
            cursor: pointer;
        }

        /* 模式切换提示 */
        .mode-hint {
            position: fixed;
            bottom: 70px;
            right: 10px;
            padding: 4px 8px;
            background: #1a1a2e;
            border: 1px solid #2a2a4a;
            border-radius: 4px;
            color: #6a6a8a;
            font-size: 10px;
        }

        .mode-hint a { color: #00d4ff; }
    </style>
</head>
<body>
    <!-- 认证页面 -->
    <div id="auth-page">
        <div class="auth-container">
            <h1 class="auth-title">◈ 远程终端</h1>
            <p class="auth-subtitle">请输入 6 位访问码</p>
            <div class="pin-input" id="pin-container">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
            </div>
            <button class="auth-btn" id="auth-btn" disabled>连接</button>
            <p class="auth-error" id="auth-error"></p>
        </div>
    </div>

    <!-- 终端页面 -->
    <div id="terminal-page">
        <div class="toolbar">
            <div class="status-indicator" id="status-indicator"></div>
            <select class="tab-select" id="tab-select"></select>
            <button class="toolbar-btn" id="refresh-btn">刷新</button>
            <button class="toolbar-btn" id="clear-btn">清屏</button>
        </div>
        <div id="output-container">
            <div id="output"></div>
            <div id="current-line"></div>
        </div>
        <div class="shortcuts">
            <button class="shortcut-btn" data-key="&#x03;">Ctrl+C</button>
            <button class="shortcut-btn" data-key="&#x04;">Ctrl+D</button>
            <button class="shortcut-btn" data-key="&#x1a;">Ctrl+Z</button>
            <button class="shortcut-btn" data-key="&#x1b;">ESC</button>
            <button class="shortcut-btn" data-key="&#x09;">TAB</button>
            <button class="shortcut-btn" data-key="&#x1b;[A">↑</button>
            <button class="shortcut-btn" data-key="&#x1b;[B">↓</button>
        </div>
        <div class="input-area">
            <input type="text" id="cmd-input" placeholder="输入命令..." autocomplete="off" autocorrect="off" autocapitalize="off">
            <button class="send-btn" id="send-btn">发送</button>
        </div>
        <div class="mode-hint">轻量模式 | <a href="?mode=xterm">切换到完整终端</a></div>
    </div>

    <script>
        // ANSI 解析器
        const ansi_up = new AnsiUp();
        ansi_up.use_classes = false;

        // 状态
        let ws = null;
        let currentTabId = null;
        let reconnectAttempts = 0;
        const maxReconnectAttempts = 5;

        // 行缓冲
        let currentLineBuffer = "";
        let outputLines = [];
        const MAX_LINES = 1000;

        // DOM
        const authPage = document.getElementById('auth-page');
        const terminalPage = document.getElementById('terminal-page');
        const pinInputs = document.querySelectorAll('.pin-input input');
        const authBtn = document.getElementById('auth-btn');
        const authError = document.getElementById('auth-error');
        const tabSelect = document.getElementById('tab-select');
        const statusIndicator = document.getElementById('status-indicator');
        const outputDiv = document.getElementById('output');
        const currentLineDiv = document.getElementById('current-line');
        const outputContainer = document.getElementById('output-container');
        const cmdInput = document.getElementById('cmd-input');

        // PIN 输入
        pinInputs.forEach((input, index) => {
            input.addEventListener('input', (e) => {
                const value = e.target.value.replace(/[^0-9]/g, '');
                e.target.value = value;
                if (value && index < pinInputs.length - 1) {
                    pinInputs[index + 1].focus();
                }
                updateAuthButton();
            });
            input.addEventListener('keydown', (e) => {
                if (e.key === 'Backspace' && !e.target.value && index > 0) {
                    pinInputs[index - 1].focus();
                }
            });
            input.addEventListener('paste', (e) => {
                e.preventDefault();
                const paste = (e.clipboardData || window.clipboardData).getData('text');
                const digits = paste.replace(/[^0-9]/g, '').slice(0, 6);
                digits.split('').forEach((digit, i) => {
                    if (pinInputs[i]) pinInputs[i].value = digit;
                });
                updateAuthButton();
            });
        });

        function updateAuthButton() {
            authBtn.disabled = getPin().length !== 6;
        }

        function getPin() {
            return Array.from(pinInputs).map(i => i.value).join('');
        }

        // 连接
        authBtn.addEventListener('click', connect);

        function connect() {
            const token = getPin();
            if (token.length !== 6) return;

            authBtn.disabled = true;
            authError.textContent = '';

            const wsProtocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
            ws = new WebSocket(`${wsProtocol}//${location.host}/ws/terminal?token=${token}`);

            ws.onopen = () => {
                reconnectAttempts = 0;
                showTerminalPage();
            };

            ws.onmessage = (e) => {
                try {
                    const msg = JSON.parse(e.data);
                    handleMessage(msg);
                } catch {}
            };

            ws.onerror = () => {
                authError.textContent = '连接失败，请检查访问码';
                authBtn.disabled = false;
            };

            ws.onclose = () => {
                statusIndicator.classList.add('disconnected');
                if (terminalPage.style.display !== 'none' && reconnectAttempts < maxReconnectAttempts) {
                    reconnectAttempts++;
                    setTimeout(() => connect(), 2000 * reconnectAttempts);
                }
            };
        }

        function handleMessage(msg) {
            switch (msg.type) {
                case 'tabs':
                    updateTabs(msg.tabs);
                    break;
                case 'output':
                    if (msg.tabId === currentTabId) {
                        processOutput(msg.data);
                    }
                    break;
                case 'tab_switched':
                    currentTabId = msg.tabId;
                    clearOutput();
                    break;
                case 'error':
                    authError.textContent = msg.message || '认证失败';
                    authBtn.disabled = false;
                    break;
            }
        }

        // 核心：处理输出流
        function processOutput(text) {
            for (let i = 0; i < text.length; i++) {
                const char = text[i];

                if (char === '\r') {
                    // 回车：清空当前行缓冲区（光标回到行首）
                    currentLineBuffer = "";
                } else if (char === '\n') {
                    // 换行：提交当前行
                    if (currentLineBuffer.trim() || outputLines.length === 0) {
                        commitLine(currentLineBuffer);
                    }
                    currentLineBuffer = "";
                } else {
                    currentLineBuffer += char;
                }
            }

            // 更新当前行显示
            updateCurrentLine();

            // 自动滚动到底部
            outputContainer.scrollTop = outputContainer.scrollHeight;
        }

        function commitLine(text) {
            // 清理重复的 spinner 行
            const stripped = stripAnsi(text).trim();
            if (stripped && outputLines.length > 0) {
                const lastStripped = stripAnsi(outputLines[outputLines.length - 1]).trim();
                // 如果是相同的 spinner 状态，跳过
                if (stripped === lastStripped && stripped.includes('…')) {
                    return;
                }
            }

            outputLines.push(text);

            // 限制行数
            while (outputLines.length > MAX_LINES) {
                outputLines.shift();
            }

            // 渲染到 DOM
            const div = document.createElement('div');
            div.className = 'line';
            div.innerHTML = ansi_up.ansi_to_html(text);
            outputDiv.appendChild(div);

            // 限制 DOM 节点
            while (outputDiv.children.length > MAX_LINES) {
                outputDiv.removeChild(outputDiv.firstChild);
            }
        }

        function updateCurrentLine() {
            currentLineDiv.innerHTML = ansi_up.ansi_to_html(currentLineBuffer);
        }

        function stripAnsi(str) {
            return str.replace(/\x1b\[[0-9;]*[a-zA-Z]/g, '');
        }

        function clearOutput() {
            outputLines = [];
            currentLineBuffer = "";
            outputDiv.innerHTML = '';
            currentLineDiv.innerHTML = '';
        }

        function updateTabs(tabs) {
            tabSelect.innerHTML = '';
            tabs.forEach(tab => {
                const option = document.createElement('option');
                option.value = tab.id;
                option.textContent = tab.name;
                tabSelect.appendChild(option);
            });

            if (tabs.length > 0 && !currentTabId) {
                currentTabId = tabs[0].id;
                switchTab(currentTabId);
            }
        }

        function switchTab(tabId) {
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify({ type: 'switch_tab', tabId }));
            }
        }

        tabSelect.addEventListener('change', (e) => switchTab(e.target.value));

        document.getElementById('refresh-btn').addEventListener('click', () => {
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify({ type: 'get_tabs' }));
            }
        });

        document.getElementById('clear-btn').addEventListener('click', clearOutput);

        function showTerminalPage() {
            authPage.style.display = 'none';
            terminalPage.style.display = 'flex';
            statusIndicator.classList.remove('disconnected');
            cmdInput.focus();
        }

        // 发送命令
        function sendCommand(cmd) {
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify({ type: 'input', data: cmd }));
            }
        }

        document.getElementById('send-btn').addEventListener('click', () => {
            const cmd = cmdInput.value;
            if (cmd) {
                sendCommand(cmd + '\r');
                cmdInput.value = '';
            }
            cmdInput.focus();
        });

        cmdInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                const cmd = cmdInput.value;
                sendCommand(cmd + '\r');
                cmdInput.value = '';
            }
        });

        // 快捷键
        document.querySelectorAll('.shortcut-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                sendCommand(btn.dataset.key);
                cmdInput.focus();
            });
        });

        // 初始化
        pinInputs[0].focus();
    </script>
</body>
</html>
""";
    }

    /// <summary>
    /// 生成 xterm.js 版本的 HTML（方案 A - 完整终端模式）
    /// </summary>
    private string GenerateXtermIndexHtml()
    {
        return """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover">
    <title>远程终端</title>
    <!-- 使用系统字体栈，移除 Google Fonts 以避免加载延迟和宽度计算错误 -->
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/xterm@5.3.0/css/xterm.css" />
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        html, body {
            width: 100%; height: 100%;
            overflow: hidden;
            background: #0a0a12;
            font-family: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, "Liberation Mono", monospace;
        }

        /* 认证页面 */
        #auth-page {
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            height: 100vh;
            padding: 20px;
        }

        .auth-container {
            background: linear-gradient(135deg, #12121a 0%, #1a1a2e 100%);
            border: 1px solid #00d4ff30;
            border-radius: 16px;
            padding: 40px;
            max-width: 360px;
            width: 100%;
            box-shadow: 0 0 40px #00d4ff10;
        }

        .auth-title {
            color: #00d4ff;
            font-size: 24px;
            text-align: center;
            margin-bottom: 8px;
            text-shadow: 0 0 20px #00d4ff40;
        }

        .auth-subtitle {
            color: #6a6a8a;
            font-size: 14px;
            text-align: center;
            margin-bottom: 30px;
        }

        .pin-input {
            display: flex;
            justify-content: center;
            gap: 8px;
            margin-bottom: 24px;
        }

        .pin-input input {
            width: 45px;
            height: 55px;
            background: #0a0a12;
            border: 2px solid #2a2a4a;
            border-radius: 8px;
            color: #00d4ff;
            font-size: 24px;
            font-weight: bold;
            text-align: center;
            outline: none;
            transition: all 0.2s;
        }

        .pin-input input:focus {
            border-color: #00d4ff;
            box-shadow: 0 0 15px #00d4ff40;
        }

        .auth-btn {
            width: 100%;
            padding: 14px;
            background: linear-gradient(135deg, #00d4ff 0%, #0099cc 100%);
            border: none;
            border-radius: 8px;
            color: #0a0a12;
            font-size: 16px;
            font-weight: bold;
            cursor: pointer;
            transition: all 0.2s;
        }

        .auth-btn:hover {
            transform: translateY(-2px);
            box-shadow: 0 5px 20px #00d4ff40;
        }

        .auth-btn:disabled {
            opacity: 0.5;
            cursor: not-allowed;
            transform: none;
        }

        .auth-error {
            color: #ff4757;
            text-align: center;
            margin-top: 16px;
            font-size: 14px;
        }

        /* 终端页面 */
        #terminal-page {
            display: none;
            flex-direction: column;
            height: 100vh;
        }

        .toolbar {
            display: flex;
            align-items: center;
            padding: 8px 12px;
            background: #12121a;
            border-bottom: 1px solid #2a2a4a;
            gap: 8px;
            flex-shrink: 0;
        }

        .tab-select {
            flex: 1;
            padding: 8px 12px;
            background: #0a0a12;
            border: 1px solid #2a2a4a;
            border-radius: 6px;
            color: #cccccc;
            font-size: 14px;
            outline: none;
        }

        .toolbar-btn {
            padding: 8px 12px;
            background: #1a1a2e;
            border: 1px solid #2a2a4a;
            border-radius: 6px;
            color: #00d4ff;
            font-size: 12px;
            cursor: pointer;
        }

        .toolbar-btn:active {
            background: #2a2a4a;
        }

        .status-indicator {
            width: 8px;
            height: 8px;
            border-radius: 50%;
            background: #16c60c;
            animation: pulse 2s infinite;
        }

        .status-indicator.disconnected {
            background: #ff4757;
            animation: none;
        }

        @keyframes pulse {
            0%, 100% { opacity: 1; }
            50% { opacity: 0.5; }
        }

        #terminal-container {
            flex: 1;
            position: relative;
            background: #000;
            overflow: hidden;
            padding: 4px; 
        }

        #terminal {
            width: 100%;
            height: 100%;
        }

        /* 虚拟键盘 */
        .virtual-keyboard {
            display: flex;
            flex-wrap: wrap;
            padding: 8px;
            background: #12121a;
            border-top: 1px solid #2a2a4a;
            gap: 4px;
            flex-shrink: 0;
            padding-bottom: env(safe-area-inset-bottom);
        }

        .vk-btn {
            flex: 1;
            min-width: 40px;
            padding: 10px 8px;
            background: #1a1a2e;
            border: 1px solid #2a2a4a;
            border-radius: 4px;
            color: #cccccc;
            font-size: 12px;
            font-family: monospace;
            cursor: pointer;
            touch-action: manipulation;
            user-select: none;
        }

        .vk-btn:active {
            background: #00d4ff;
            color: #0a0a12;
        }

        .vk-btn.special {
            color: #00d4ff;
        }

        /* 移动端适配 */
        @media (max-width: 480px) {
            .auth-container {
                padding: 30px 20px;
            }
            .pin-input input {
                width: 40px;
                height: 50px;
                font-size: 20px;
            }
        }

        /* 滚动条 */
        .xterm-viewport::-webkit-scrollbar { width: 8px; }
        .xterm-viewport::-webkit-scrollbar-track { background: #1e1e1e; }
        .xterm-viewport::-webkit-scrollbar-thumb { background: #424242; border-radius: 4px; }
    </style>
</head>
<body>
    <!-- 认证页面 -->
    <div id="auth-page">
        <div class="auth-container">
            <h1 class="auth-title">◈ 远程终端</h1>
            <p class="auth-subtitle">请输入 6 位访问码</p>
            <div class="pin-input" id="pin-container">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
                <input type="tel" maxlength="1" pattern="[0-9]" inputmode="numeric">
            </div>
            <button class="auth-btn" id="auth-btn" disabled>连接</button>
            <p class="auth-error" id="auth-error"></p>
        </div>
    </div>

    <!-- 终端页面 -->
    <div id="terminal-page">
        <div class="toolbar">
            <div class="status-indicator" id="status-indicator"></div>
            <select class="tab-select" id="tab-select"></select>
            <button class="toolbar-btn" id="refresh-btn">刷新</button>
        </div>
        <div id="terminal-container">
             <div id="terminal"></div>
        </div>
        <div class="virtual-keyboard" id="virtual-keyboard">
            <button class="vk-btn special" data-key="&#x1b;">ESC</button>
            <button class="vk-btn special" data-key="&#x09;">TAB</button>
            <button class="vk-btn special" data-key="&#x03;">Ctrl+C</button>
            <button class="vk-btn special" data-key="&#x04;">Ctrl+D</button>
            <button class="vk-btn special" data-key="&#x1a;">Ctrl+Z</button>
            <button class="vk-btn" data-key="↑">↑</button>
            <button class="vk-btn" data-key="↓">↓</button>
            <button class="vk-btn" data-key="←">←</button>
            <button class="vk-btn" data-key="→">→</button>
        </div>
    </div>

    <script src="https://cdn.jsdelivr.net/npm/xterm@5.3.0/lib/xterm.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/xterm-addon-fit@0.8.0/lib/xterm-addon-fit.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/xterm-addon-unicode11@0.6.0/lib/xterm-addon-unicode11.js"></script>
    <script>
        // 状态
        let ws = null;
        let term = null;
        let fitAddon = null;
        let currentTabId = null;
        let reconnectAttempts = 0;
        const maxReconnectAttempts = 5;
        let resizeTimeout = null;

        // 心跳定时器
        let heartbeatInterval = null;
        const HEARTBEAT_INTERVAL = 15000; // 15秒发送一次心跳

        // 标签初始化状态
        let tabInitializing = false;

        // 写入缓冲，解决移动端高频小包渲染卡顿和撕裂问题
        let writeBuffer = [];
        let writeTimer = null;
        const WRITE_INTERVAL = 16; // ~60fps

        // DOM 元素
        const authPage = document.getElementById('auth-page');
        const terminalPage = document.getElementById('terminal-page');
        const pinInputs = document.querySelectorAll('.pin-input input');
        const authBtn = document.getElementById('auth-btn');
        const authError = document.getElementById('auth-error');
        const tabSelect = document.getElementById('tab-select');
        const statusIndicator = document.getElementById('status-indicator');
        const refreshBtn = document.getElementById('refresh-btn');

        // PIN 输入处理
        pinInputs.forEach((input, index) => {
            input.addEventListener('input', (e) => {
                const value = e.target.value.replace(/[^0-9]/g, '');
                e.target.value = value;

                if (value && index < pinInputs.length - 1) {
                    pinInputs[index + 1].focus();
                }

                updateAuthButton();
            });

            input.addEventListener('keydown', (e) => {
                if (e.key === 'Backspace' && !e.target.value && index > 0) {
                    pinInputs[index - 1].focus();
                }
            });

            input.addEventListener('paste', (e) => {
                e.preventDefault();
                const paste = (e.clipboardData || window.clipboardData).getData('text');
                const digits = paste.replace(/[^0-9]/g, '').slice(0, 6);

                digits.split('').forEach((digit, i) => {
                    if (pinInputs[i]) {
                        pinInputs[i].value = digit;
                    }
                });

                updateAuthButton();
                if (digits.length === 6) {
                    authBtn.focus();
                }
            });
        });

        function updateAuthButton() {
            const pin = getPin();
            authBtn.disabled = pin.length !== 6;
        }

        function getPin() {
            return Array.from(pinInputs).map(i => i.value).join('');
        }

        // 连接按钮
        authBtn.addEventListener('click', connect);

        function connect() {
            const token = getPin();
            if (token.length !== 6) return;

            authBtn.disabled = true;
            authError.textContent = '';

            const wsProtocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
            const wsUrl = `${wsProtocol}//${location.host}/ws/terminal?token=${token}`;

            ws = new WebSocket(wsUrl);

            ws.onopen = () => {
                reconnectAttempts = 0;
                showTerminalPage();
                // 启动心跳
                startHeartbeat();
            };

            ws.onmessage = (e) => {
                try {
                    const msg = JSON.parse(e.data);
                    handleMessage(msg);
                } catch {}
            };

            ws.onerror = () => {
                authError.textContent = '连接失败，请检查访问码';
                authBtn.disabled = false;
            };

            ws.onclose = () => {
                statusIndicator.classList.add('disconnected');
                // 停止心跳
                stopHeartbeat();

                if (terminalPage.style.display !== 'none' && reconnectAttempts < maxReconnectAttempts) {
                    reconnectAttempts++;
                    setTimeout(() => connect(), 2000 * reconnectAttempts);
                }
            };
        }

        // 心跳管理
        function startHeartbeat() {
            stopHeartbeat();
            heartbeatInterval = setInterval(() => {
                if (ws && ws.readyState === WebSocket.OPEN) {
                    ws.send(JSON.stringify({ type: 'ping' }));
                }
            }, HEARTBEAT_INTERVAL);
        }

        function stopHeartbeat() {
            if (heartbeatInterval) {
                clearInterval(heartbeatInterval);
                heartbeatInterval = null;
            }
        }

        function handleMessage(msg) {
            switch (msg.type) {
                case 'tabs':
                    updateTabs(msg.tabs);
                    break;
                case 'output':
                    if (msg.tabId === currentTabId && term && !tabInitializing) {
                        bufferWrite(msg.data);
                    }
                    break;
                case 'tab_switched':
                    currentTabId = msg.tabId;
                    if (term) term.clear();
                    hideInitOverlay();
                    // 切换标签后触发一次重绘和调整大小
                    setTimeout(() => {
                        if (fitAddon) {
                            fitAddon.fit();
                            sendResize();
                        }
                    }, 100);
                    break;
                case 'tab_status':
                    handleTabStatus(msg);
                    break;
                case 'pong':
                    // 心跳响应，连接正常
                    break;
                case 'error':
                    authError.textContent = msg.message || '认证失败';
                    authBtn.disabled = false;
                    break;
            }
        }

        // 处理标签初始化状态
        function handleTabStatus(msg) {
            if (msg.tabId !== currentTabId) return;

            switch (msg.status) {
                case 'initializing':
                    showInitOverlay('正在初始化终端...');
                    tabInitializing = true;
                    break;
                case 'ready':
                    hideInitOverlay();
                    tabInitializing = false;
                    // 初始化完成后触发 resize
                    setTimeout(() => {
                        if (fitAddon) {
                            fitAddon.fit();
                            sendResize();
                        }
                    }, 200);
                    break;
                case 'failed':
                    showInitOverlay('初始化失败，请重试');
                    tabInitializing = false;
                    setTimeout(hideInitOverlay, 3000);
                    break;
            }
        }

        // 初始化遮罩层
        function showInitOverlay(text) {
            let overlay = document.getElementById('init-overlay');
            if (!overlay) {
                overlay = document.createElement('div');
                overlay.id = 'init-overlay';
                overlay.style.cssText = `
                    position: absolute;
                    top: 0; left: 0; right: 0; bottom: 0;
                    background: rgba(10, 10, 18, 0.95);
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                    justify-content: center;
                    z-index: 1000;
                `;
                overlay.innerHTML = `
                    <div style="font-size: 32px; color: #00d4ff; animation: pulse 1.5s infinite;">◈</div>
                    <div id="init-text" style="margin-top: 12px; color: #6a6a8a; font-size: 13px;"></div>
                    <div style="margin-top: 16px; width: 100px; height: 2px; background: #1a1a2a; border-radius: 1px; overflow: hidden;">
                        <div style="width: 30%; height: 100%; background: linear-gradient(90deg, #00d4ff, #bd00ff); animation: loadingBar 1.5s ease-in-out infinite;"></div>
                    </div>
                `;
                document.getElementById('terminal-container').appendChild(overlay);
            }
            document.getElementById('init-text').textContent = text;
            overlay.style.display = 'flex';
        }

        function hideInitOverlay() {
            const overlay = document.getElementById('init-overlay');
            if (overlay) {
                overlay.style.display = 'none';
            }
        }

        function bufferWrite(data) {
            writeBuffer.push(data);
            if (!writeTimer) {
                writeTimer = setTimeout(() => {
                    flushBuffer();
                }, WRITE_INTERVAL);
            }
        }

        function flushBuffer() {
            writeTimer = null;
            if (writeBuffer.length === 0) return;
            
            // 合并数据块
            const chunk = writeBuffer.join('');
            writeBuffer = [];

            if (term) {
                term.write(chunk);
            }
        }

        function updateTabs(tabs) {
            tabSelect.innerHTML = '';
            tabs.forEach(tab => {
                const option = document.createElement('option');
                option.value = tab.id;
                option.textContent = tab.name;
                tabSelect.appendChild(option);
            });

            if (tabs.length > 0 && !currentTabId) {
                currentTabId = tabs[0].id;
                switchTab(currentTabId);
            } else if (currentTabId && tabs.some(t => t.id === currentTabId)) {
                // 当前标签还在列表中，保持选中
                tabSelect.value = currentTabId;
            } else if (tabs.length > 0) {
                 // 当前标签不在了，选中第一个
                 currentTabId = tabs[0].id;
                 switchTab(currentTabId);
            }
        }

        function switchTab(tabId) {
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify({ type: 'switch_tab', tabId }));
            }
        }

        tabSelect.addEventListener('change', (e) => {
            switchTab(e.target.value);
        });

        refreshBtn.addEventListener('click', () => {
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify({ type: 'get_tabs' }));
            }
        });

        function showTerminalPage() {
            authPage.style.display = 'none';
            terminalPage.style.display = 'flex';
            statusIndicator.classList.remove('disconnected');
            // 延迟初始化以确保 DOM 已渲染
            setTimeout(initTerminal, 50);
        }

        function initTerminal() {
            if (term) return;

            // 使用系统等宽字体栈，确保最佳兼容性和加载速度
            const fontStack = 'ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, "Liberation Mono", monospace, "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji"';

            term = new Terminal({
                fontFamily: fontStack,
                fontSize: 13,
                lineHeight: 1.1,
                cursorBlink: true,
                cursorStyle: 'bar',
                scrollback: 5000,
                allowProposedApi: true,
                convertEol: true, // 确保换行符处理正确
                macOptionIsMeta: true,
                theme: {
                    background: '#0c0c0c',
                    foreground: '#cccccc',
                    cursor: '#ffffff',
                    black: '#0c0c0c',
                    red: '#c50f1f',
                    green: '#13a10e',
                    yellow: '#c19c00',
                    blue: '#0037da',
                    magenta: '#881798',
                    cyan: '#3a96dd',
                    white: '#cccccc',
                    brightBlack: '#767676',
                    brightRed: '#e74856',
                    brightGreen: '#16c60c',
                    brightYellow: '#f9f1a5',
                    brightBlue: '#3b78ff',
                    brightMagenta: '#b4009e',
                    brightCyan: '#61d6d6',
                    brightWhite: '#f2f2f2'
                }
            });

            fitAddon = new FitAddon.FitAddon();
            const unicode11Addon = new Unicode11Addon.Unicode11Addon();
            term.loadAddon(fitAddon);
            term.loadAddon(unicode11Addon);
            term.unicode.activeVersion = '11';
            
            term.open(document.getElementById('terminal'));
            
            // 立即适应大小并发送给后端
            fitAddon.fit();
            sendResize();

            // 双重保险：再次触发 resize，防止初始渲染计算错误
            setTimeout(() => {
                fitAddon.fit();
                sendResize();
            }, 500);

            term.onData(data => {
                if (ws && ws.readyState === WebSocket.OPEN) {
                    ws.send(JSON.stringify({ type: 'input', data }));
                }
            });
            
            // 处理窗口大小调整
            window.addEventListener('resize', () => {
                clearTimeout(resizeTimeout);
                resizeTimeout = setTimeout(() => {
                    if (fitAddon) {
                         fitAddon.fit();
                         sendResize();
                    }
                }, 100); // 防抖
            });

            // 虚拟键盘
            document.querySelectorAll('.vk-btn').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    e.preventDefault(); // 防止失去焦点
                    let key = btn.dataset.key;
                    // 方向键映射
                    const keyMap = {
                        '↑': '\x1b[A',
                        '↓': '\x1b[B',
                        '→': '\x1b[C',
                        '←': '\x1b[D'
                    };
                    key = keyMap[key] || key;

                    if (ws && ws.readyState === WebSocket.OPEN) {
                        ws.send(JSON.stringify({ type: 'input', data: key }));
                    }
                    term.focus();
                });
            });
            
            term.focus();
        }
        
        function sendResize() {
            if (term && ws && ws.readyState === WebSocket.OPEN && currentTabId) {
                // 确保尺寸有效
                if (term.cols > 0 && term.rows > 0) {
                    ws.send(JSON.stringify({
                        type: 'resize',
                        cols: term.cols,
                        rows: term.rows,
                        tabId: currentTabId
                    }));
                }
            }
        }

        // 初始化：聚焦第一个输入框
        pinInputs[0].focus();
    </script>
</body>
</html>
""";
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// 远程 API 控制器
/// </summary>
public class RemoteApiController : WebApiController
{
    private readonly RemoteAuthService _authService;
    private readonly Func<IEnumerable<(string Id, string Name)>>? _getTabsFunc;

    public RemoteApiController(RemoteAuthService authService, Func<IEnumerable<(string Id, string Name)>>? getTabsFunc)
    {
        _authService = authService;
        _getTabsFunc = getTabsFunc;
    }

    [Route(HttpVerbs.Get, "/tabs")]
    public object GetTabs()
    {
        var tabs = _getTabsFunc?.Invoke() ?? Array.Empty<(string, string)>();
        return new { tabs = tabs.Select(t => new { id = t.Id, name = t.Name }) };
    }

    [Route(HttpVerbs.Post, "/auth/verify")]
    public async Task<object> VerifyToken()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.GetProperty("token").GetString() ?? "";
            var valid = _authService.VerifyToken(token);
            return new { valid };
        }
        catch
        {
            return new { valid = false };
        }
    }
}
