using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using CodeBridge.Models;
using CodeBridge.Services;

namespace CodeBridge.ViewModels;

/// <summary>
/// 终端标签 ViewModel - 集成 WebView2
/// </summary>
public partial class TerminalTabViewModel : ObservableObject, IDisposable
{
    private static readonly System.Drawing.Color WebViewBackgroundColor =
        System.Drawing.Color.FromArgb(255, 12, 12, 12);

    [ObservableProperty]
    private TabConfig _config;

    [ObservableProperty]
    private bool _isRunning = true;

    [ObservableProperty]
    private bool _isTaskRunning = false;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private WebView2? _webView;

    [ObservableProperty]
    private bool _isDisabled = false;

    /// <summary>
    /// 任务完成检测器
    /// </summary>
    private TaskCompletionDetector? _completionDetector;

    /// <summary>
    /// 任务完成事件（供 MainWindowViewModel 订阅）
    /// </summary>
    public event EventHandler<TaskCompletionDetector.TaskCompletedEventArgs>? TaskCompleted;

    /// <summary>
    /// 显示名称：优先使用备注，否则使用目录名
    /// </summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Config.Note)
        ? Config.Note
        : Config.Name;

    /// <summary>
    /// 通知 DisplayName 属性已更改（供外部调用）
    /// </summary>
    public void NotifyDisplayNameChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
    }

    private readonly TerminalService _terminalService;
    private TerminalService.TerminalSession? _session;
    private bool _isDisposed = false;
    private string _shellType = "powershell";

    /// <summary>
    /// 设置 Shell 类型（powershell 或 cmd）
    /// </summary>
    public string ShellType
    {
        get => _shellType;
        set => _shellType = value;
    }

    public TerminalTabViewModel(TabConfig config, TerminalService terminalService, string shellType = "powershell")
    {
        _config = config;
        _title = config.Name;
        _terminalService = terminalService;
        _shellType = shellType;
        _isDisabled = config.IsDisabled;

        // 如果被禁用，跳过初始化
        if (_isDisabled)
        {
            IsRunning = false;
            return;
        }

        // 初始化任务完成检测器
        _completionDetector = new TaskCompletionDetector(config.Id);
        _completionDetector.EnableHeuristics = false;  // 关闭启发式检测（使用 Hooks）
        _completionDetector.EnableIdleTimeout = false; // 关闭空闲超时检测（使用 Hooks）
        _completionDetector.TaskCompleted += OnTaskCompleted;
        _completionDetector.ActivityChanged += OnActivityChanged;

        InitializeWebView();
    }

    /// <summary>
    /// 禁用标签页（停止终端但保留标签）
    /// </summary>
    public void Disable()
    {
        if (_isDisabled) return;

        IsDisabled = true;
        Config.IsDisabled = true;
        IsRunning = false;
        IsTaskRunning = false;

        // 停止终端
        _terminalService.Stop(Config.Id);

        // 释放任务完成检测器
        if (_completionDetector != null)
        {
            _completionDetector.TaskCompleted -= OnTaskCompleted;
            _completionDetector.ActivityChanged -= OnActivityChanged;
            _completionDetector.Dispose();
            _completionDetector = null;
        }

        // 释放 WebView
        if (WebView != null)
        {
            if (WebView.CoreWebView2 != null)
                WebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            WebView.Dispose();
            WebView = null;
        }
    }

    /// <summary>
    /// 启用标签页（重新初始化终端）
    /// </summary>
    public void Enable()
    {
        if (!_isDisabled) return;

        IsDisabled = false;
        Config.IsDisabled = false;
        IsRunning = true;

        // 重新初始化任务完成检测器
        _completionDetector = new TaskCompletionDetector(Config.Id);
        _completionDetector.EnableHeuristics = false;
        _completionDetector.EnableIdleTimeout = false;
        _completionDetector.TaskCompleted += OnTaskCompleted;
        _completionDetector.ActivityChanged += OnActivityChanged;

        // 重新初始化 WebView
        InitializeWebView();
    }

    /// <summary>
    /// 任务完成回调
    /// </summary>
    private void OnTaskCompleted(object? sender, TaskCompletionDetector.TaskCompletedEventArgs e)
    {
        IsTaskRunning = false;
        TaskCompleted?.Invoke(this, e);
    }

    /// <summary>
    /// 活动状态变化回调
    /// </summary>
    private void OnActivityChanged(object? sender, bool isActive)
    {
        // 需要在 UI 线程更新属性
        WebView?.Dispatcher.InvokeAsync(() =>
        {
            IsTaskRunning = isActive;
        });
    }

    private async void InitializeWebView()
    {
        WebView = new WebView2
        {
            DefaultBackgroundColor = WebViewBackgroundColor
        };

        WebView.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (!e.IsSuccess || WebView?.CoreWebView2 == null)
                return;

            WebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        };

        try
        {
            await WebView.EnsureCoreWebView2Async();

            // 使用 NavigateToString 加载内联 HTML（避免文件路径问题）
            var html = """
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/xterm@5.3.0/css/xterm.css" />
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        html, body { width: 100%; height: 100%; overflow: hidden; background-color: #0c0c0c; }
        #terminal { width: 100%; height: 100%; position: relative; z-index: 10; }

        /* 科技感加载背景 */
        #loading-overlay {
            position: fixed;
            top: 0; left: 0; right: 0; bottom: 0;
            background: linear-gradient(135deg, #0a0a12 0%, #0c0c14 50%, #0a0a12 100%);
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            z-index: 100;
            transition: opacity 0.5s ease-out;
        }
        #loading-overlay.hidden { opacity: 0; pointer-events: none; }

        /* 网格背景 */
        #loading-overlay::before {
            content: '';
            position: absolute;
            top: 0; left: 0; right: 0; bottom: 0;
            background-image:
                linear-gradient(rgba(0,212,255,0.03) 1px, transparent 1px),
                linear-gradient(90deg, rgba(0,212,255,0.03) 1px, transparent 1px);
            background-size: 30px 30px;
            animation: gridMove 20s linear infinite;
        }
        @keyframes gridMove {
            0% { transform: translate(0, 0); }
            100% { transform: translate(30px, 30px); }
        }

        /* 扫描线 */
        #loading-overlay::after {
            content: '';
            position: absolute;
            top: 0; left: 0; right: 0;
            height: 3px;
            background: linear-gradient(90deg, transparent, #00d4ff, transparent);
            animation: scanLine 2s ease-in-out infinite;
        }
        @keyframes scanLine {
            0% { top: 0; opacity: 0; }
            50% { opacity: 1; }
            100% { top: 100%; opacity: 0; }
        }

        .loading-icon {
            font-size: 42px;
            color: #00d4ff;
            text-shadow: 0 0 20px #00d4ff80, 0 0 40px #00d4ff40;
            animation: pulse 1.5s ease-in-out infinite;
        }
        @keyframes pulse {
            0%, 100% { transform: scale(1); text-shadow: 0 0 20px #00d4ff80; }
            50% { transform: scale(1.1); text-shadow: 0 0 30px #00d4ff, 0 0 50px #00d4ff60; }
        }

        .loading-text {
            margin-top: 15px;
            font-family: 'Consolas', monospace;
            font-size: 12px;
            color: #6a6a8a;
            letter-spacing: 2px;
        }

        .loading-bar-container {
            margin-top: 20px;
            width: 150px;
            height: 2px;
            background: #1a1a2a;
            border-radius: 1px;
            overflow: hidden;
        }
        .loading-bar {
            width: 30%;
            height: 100%;
            background: linear-gradient(90deg, #00d4ff, #bd00ff);
            border-radius: 1px;
            animation: loadingBar 1.5s ease-in-out infinite;
        }
        @keyframes loadingBar {
            0% { transform: translateX(-100%); }
            100% { transform: translateX(400%); }
        }

        .xterm-viewport::-webkit-scrollbar { width: 10px; }
        .xterm-viewport::-webkit-scrollbar-track { background: #1e1e1e; }
        .xterm-viewport::-webkit-scrollbar-thumb { background: #424242; border-radius: 5px; }
        .xterm-viewport::-webkit-scrollbar-thumb:hover { background: #555; }

        /* 复制按钮样式 */
        #copy-btn {
            position: fixed;
            top: 10px;
            right: 10px;
            padding: 8px 16px;
            background: linear-gradient(135deg, #00d4ff30, #bd00ff20);
            border: 1px solid #00d4ff60;
            border-radius: 6px;
            color: #fff;
            font-size: 12px;
            font-family: 'Segoe UI', sans-serif;
            cursor: pointer;
            opacity: 0;
            visibility: hidden;
            transition: all 0.2s ease;
            z-index: 1000;
            display: flex;
            align-items: center;
            gap: 6px;
            box-shadow: 0 0 15px #00d4ff40;
        }
        #copy-btn.visible {
            opacity: 1;
            visibility: visible;
        }
        #copy-btn:hover {
            background: linear-gradient(135deg, #00d4ff50, #bd00ff40);
            box-shadow: 0 0 20px #00d4ff60;
            transform: scale(1.05);
        }
        #copy-btn:active {
            transform: scale(0.95);
        }
        #copy-btn.copied {
            background: linear-gradient(135deg, #00ff9d30, #00ff9d20);
            border-color: #00ff9d60;
        }
    </style>
</head>
<body>
    <!-- 科技感加载界面 -->
    <div id="loading-overlay">
        <div class="loading-icon">◈</div>
        <div class="loading-text">INITIALIZING TERMINAL...</div>
        <div class="loading-bar-container">
            <div class="loading-bar"></div>
        </div>
    </div>

    <div id="terminal"></div>
    <button id="copy-btn">📋 复制选中</button>
    <script src="https://cdn.jsdelivr.net/npm/xterm@5.3.0/lib/xterm.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/xterm-addon-fit@0.8.0/lib/xterm-addon-fit.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/xterm-addon-web-links@0.9.0/lib/xterm-addon-web-links.js"></script>
    <script>
        // 终端配置 - 使用 Canvas 渲染器（更稳定，避免 WebGL 字符错乱）
        const term = new Terminal({
            fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "Courier New", monospace',
            fontSize: 14,
            fontWeight: 'normal',
            fontWeightBold: 'bold',
            lineHeight: 1.1,
            letterSpacing: 0,
            cursorBlink: true,
            cursorStyle: 'bar',
            cursorWidth: 2,
            scrollback: 10000,
            smoothScrollDuration: 0,  // 禁用平滑滚动，提升响应速度
            allowProposedApi: true,
            drawBoldTextInBrightColors: true,
            fastScrollModifier: 'alt',
            fastScrollSensitivity: 5,
            // Windows Terminal Campbell 配色
            theme: {
                background: '#0c0c0c',
                foreground: '#cccccc',
                cursor: '#ffffff',
                cursorAccent: '#0c0c0c',
                selectionBackground: '#264f78',
                selectionForeground: '#ffffff',
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

        const fitAddon = new FitAddon.FitAddon();
        const webLinksAddon = new WebLinksAddon.WebLinksAddon();

        term.loadAddon(fitAddon);
        term.loadAddon(webLinksAddon);
        term.open(document.getElementById('terminal'));
        fitAddon.fit();

        // 防抖 resize
        let resizeTimer;
        const doResize = () => {
            clearTimeout(resizeTimer);
            resizeTimer = setTimeout(() => {
                fitAddon.fit();
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'resize', rows: term.rows, cols: term.cols });
                }
            }, 50);
        };

        window.addEventListener('resize', doResize);
        new ResizeObserver(doResize).observe(document.getElementById('terminal'));

        // 接收输出
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.addEventListener('message', e => {
                if (typeof e.data === 'string') {
                    term.write(e.data);
                }
            });
        }

        // 发送输入
        term.onData(data => {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({ type: 'input', data: data });
            }
        });

        // 初始化
        setTimeout(() => {
            fitAddon.fit();
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage({ type: 'ready', rows: term.rows, cols: term.cols });
            }
            term.focus();

            // 隐藏加载界面
            const loadingOverlay = document.getElementById('loading-overlay');
            if (loadingOverlay) {
                loadingOverlay.classList.add('hidden');
                setTimeout(() => loadingOverlay.remove(), 500);
            }

            // 自动修复字符渲染问题：延迟触发多次 resize
            setTimeout(() => {
                fitAddon.fit();
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'resize', rows: term.rows, cols: term.cols });
                }
            }, 500);

            setTimeout(() => {
                fitAddon.fit();
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'resize', rows: term.rows, cols: term.cols });
                }
            }, 1500);
        }, 100);

        document.getElementById('terminal').addEventListener('mousedown', () => term.focus());

        // Ctrl+Shift+C/V 复制粘贴
        term.attachCustomKeyEventHandler(e => {
            if (e.ctrlKey && e.shiftKey && e.key === 'C') {
                const sel = term.getSelection();
                if (sel) navigator.clipboard.writeText(sel);
                return false;
            }
            if (e.ctrlKey && e.shiftKey && e.key === 'V') {
                navigator.clipboard.readText().then(t => {
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({ type: 'input', data: t });
                    }
                });
                return false;
            }
            return true;
        });

        // 禁用右键默认菜单
        document.addEventListener('contextmenu', e => e.preventDefault());

        // 复制按钮逻辑
        const copyBtn = document.getElementById('copy-btn');
        let lastSelection = '';  // 保存最后的选中文本

        // 监听选中变化
        term.onSelectionChange(() => {
            const sel = term.getSelection();
            console.log('[CopyBtn] Selection changed:', sel ? sel.length : 0, 'chars');
            if (sel && sel.length > 0) {
                lastSelection = sel;  // 保存选中文本
                copyBtn.classList.add('visible');
            } else {
                copyBtn.classList.remove('visible');
                copyBtn.classList.remove('copied');
                copyBtn.textContent = '📋 复制选中';
            }
        });

        // 点击复制按钮
        copyBtn.addEventListener('mousedown', async (e) => {
            e.stopPropagation();
            e.preventDefault();
            console.log('[CopyBtn] Clicked, lastSelection:', lastSelection ? lastSelection.length : 0, 'chars');

            // 使用保存的选中文本
            const textToCopy = lastSelection || term.getSelection();
            console.log('[CopyBtn] Text to copy:', textToCopy ? textToCopy.length : 0, 'chars');

            if (textToCopy) {
                try {
                    // 方法1: 使用 WebView2 postMessage 让 C# 处理复制
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage({ type: 'copy', data: textToCopy });
                    }

                    // 方法2: 备选 - 尝试 navigator.clipboard
                    try {
                        await navigator.clipboard.writeText(textToCopy);
                        console.log('[CopyBtn] Clipboard API success');
                    } catch (clipErr) {
                        console.log('[CopyBtn] Clipboard API failed, using fallback');
                    }

                    copyBtn.classList.add('copied');
                    copyBtn.textContent = '✓ 已复制';
                    setTimeout(() => {
                        copyBtn.classList.remove('visible');
                        copyBtn.classList.remove('copied');
                        copyBtn.textContent = '📋 复制选中';
                        lastSelection = '';
                    }, 1500);
                } catch (err) {
                    console.error('[CopyBtn] Error:', err);
                }
            } else {
                console.log('[CopyBtn] No text to copy');
            }
        });

        // 点击终端区域时隐藏复制按钮（清除选中）
        document.getElementById('terminal').addEventListener('click', () => {
            term.focus();
        });
    </script>
</body>
</html>
""";
            WebView.NavigateToString(html);

            // 监听 JavaScript 消息
            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // 等待页面加载完成后启动终端
            WebView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                if (e.IsSuccess)
                {
                    Task.Delay(500).ContinueWith(_ =>
                    {
                        StartTerminal();
                    });
                }
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 初始化失败: {ex.Message}");
        }
    }

    private void StartTerminal()
    {
        _session = _terminalService.Start(Config, OnTerminalOutput, _shellType);
    }

    private void OnTerminalOutput(string output)
    {
        // 防止访问已释放的 WebView2
        if (_isDisposed) return;

        // 将输出传递给任务完成检测器
        _completionDetector?.ProcessOutput(output);

        // 广播输出到远程控制服务
        _ = App.RemoteControlService?.BroadcastOutputAsync(Config.Id, output);

        // 发送输出到 WebView2（必须在 UI 线程）
        WebView?.Dispatcher.InvokeAsync(() =>
        {
            if (_isDisposed) return;
            try
            {
                WebView?.CoreWebView2?.PostWebMessageAsString(output);
            }
            catch (ObjectDisposedException)
            {
                // WebView 已被释放，忽略
            }
        });
    }

    private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 修复：使用 WebMessageAsJson 而非 TryGetWebMessageAsString
        // 因为 JS 端 postMessage 发送的是 object，不是 string
        var message = e.WebMessageAsJson;
        if (string.IsNullOrEmpty(message)) return;

        try
        {
            // 使用 JSON 安全解析（修复解析脆弱性）
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                return;

            var type = typeElement.GetString();

            if (type == "input" && root.TryGetProperty("data", out var dataElement))
            {
                var input = dataElement.GetString();
                if (!string.IsNullOrEmpty(input))
                    _terminalService.SendInput(Config.Id, input);
            }
            else if (type == "copy" && root.TryGetProperty("data", out var copyDataElement))
            {
                // 处理复制请求
                var textToCopy = copyDataElement.GetString();
                if (!string.IsNullOrEmpty(textToCopy))
                {
                    // 在后台线程执行，避免卡顿 UI
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            // 使用 WinForms 的 Clipboard（线程安全，不需要 UI 线程）
                            var thread = new System.Threading.Thread(() =>
                            {
                                try
                                {
                                    System.Windows.Forms.Clipboard.SetText(textToCopy);
                                    System.Diagnostics.Debug.WriteLine($"[Copy] 已复制 {textToCopy.Length} 字符");
                                }
                                catch
                                {
                                    // 静默忽略，数据可能已复制成功
                                }
                            });
                            thread.SetApartmentState(System.Threading.ApartmentState.STA);
                            thread.Start();
                            thread.Join(100); // 最多等 100ms
                        }
                        catch
                        {
                            // 静默忽略
                        }
                    });
                }
            }
            else if (type == "resize" &&
                     root.TryGetProperty("rows", out var rowsElement) &&
                     root.TryGetProperty("cols", out var colsElement))
            {
                if (rowsElement.TryGetInt32(out var rows) && colsElement.TryGetInt32(out var cols))
                {
                    _terminalService.Resize(Config.Id, cols, rows);

                    // 保存 PC 端尺寸到远程控制服务（供移动端断开时恢复）
                    App.RemoteControlService?.SetPcSize(Config.Id, cols, rows);
                }
            }
        }
        catch
        {
            // 忽略无效消息
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // 释放任务完成检测器
        if (_completionDetector != null)
        {
            _completionDetector.TaskCompleted -= OnTaskCompleted;
            _completionDetector.ActivityChanged -= OnActivityChanged;
            _completionDetector.Dispose();
        }

        // 修复重复释放问题：只调用一次 Stop（内部会 Dispose session）
        _terminalService.Stop(Config.Id);

        if (WebView != null)
        {
            if (WebView.CoreWebView2 != null)
                WebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            WebView.Dispose();
        }
    }
}
