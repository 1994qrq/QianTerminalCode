using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using MyAiHelper.ViewModels;
using MyAiHelper.Views;

namespace MyAiHelper
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // 悬浮状态栏实例
        private FloatingStatusBar? _floatingStatusBar;

        // 全局快捷键相关
        private const int HOTKEY_ID = 9000;
        private const int WM_HOTKEY = 0x0312;
        private HwndSource? _hwndSource;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // 修饰键常量
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;

        // 虚拟键码
        private const uint VK_SPACE = 0x20;

        public MainWindow()
        {
            InitializeComponent();
            Closing += MainWindow_Closing;
            Loaded += MainWindow_Loaded;
            SourceInitialized += MainWindow_SourceInitialized;

            // 初始隐藏主内容，显示加载界面
            MainContent.Opacity = 0;
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 阶段1: 加载界面元素
                LoadingText.Text = "正在加载界面元素...";
                await Task.Delay(100);

                // 阶段2: 初始化服务
                LoadingText.Text = "正在初始化服务...";

                // 让 UI 有机会渲染
                await System.Windows.Threading.Dispatcher.Yield();

                // 在 UI 线程创建 ViewModel（必须，因为包含 UI 组件）
                var vm = new MainWindowViewModel();
                DataContext = vm;
                vm.ShowTabDetailsRequested += ShowTabDetailsDialog;

                // 阶段3: 恢复会话
                LoadingText.Text = "正在恢复会话...";
                await Task.Delay(200);

                // 阶段4: 完成
                LoadingText.Text = "加载完成";
                await Task.Delay(150);

                // 淡出加载界面，淡入主内容
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400));

                fadeOut.Completed += (s, _) =>
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                };

                LoadingOverlay.BeginAnimation(OpacityProperty, fadeOut);
                MainContent.BeginAnimation(OpacityProperty, fadeIn);
            }
            catch (Exception ex)
            {
                LoadingText.Text = $"加载失败: {ex.Message}";
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // 注销全局快捷键
            UnregisterGlobalHotkey();

            // 关闭悬浮状态栏
            _floatingStatusBar?.Close();

            // 显示赛博朋克风格的关闭确认对话框
            var result = CyberConfirmDialog.Show(
                owner: this,
                title: "退出确认",
                message: "确定要退出 QianTerminalCode 吗？",
                subMessage: "所有终端会话将被保存，下次启动时可恢复。",
                confirmText: "退出",
                cancelText: "取消"
            );

            if (!result)
            {
                e.Cancel = true;
                // 重新注册快捷键
                RegisterGlobalHotkey();
                return;
            }

            // 保存设置并关闭所有终端
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Shutdown();
            }
        }

        // 标题栏拖动
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // 双击切换最大化
                MaximizeButton_Click(sender, e);
            }
            else
            {
                DragMove();
            }
        }

        // 最小化
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        // 最大化/还原
        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                MaximizeButton.Content = "\uE922"; // 最大化图标
            }
            else
            {
                WindowState = WindowState.Maximized;
                MaximizeButton.Content = "\uE923"; // 还原图标
            }
        }

        // 关闭
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // 双击标签显示详情
        private void TabControl_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 检查是否双击在 TabItem 上
            var element = e.OriginalSource as DependencyObject;
            while (element != null)
            {
                if (element is TabItem tabItem)
                {
                    if (tabItem.DataContext is TerminalTabViewModel tab &&
                        DataContext is MainWindowViewModel vm)
                    {
                        vm.ShowTabDetailsCommand.Execute(tab);
                        e.Handled = true;
                    }
                    break;
                }
                element = System.Windows.Media.VisualTreeHelper.GetParent(element);
            }
        }

        // 历史记录双击恢复
        private void HistoryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBox listBox &&
                listBox.SelectedItem is Models.TabConfig config &&
                DataContext is MainWindowViewModel vm)
            {
                vm.RestoreFromHistoryCommand.Execute(config);
            }
        }

        // 显示标签详情对话框
        private void ShowTabDetailsDialog(TerminalTabViewModel tab)
        {
            var config = tab.Config;
            var createdAt = config.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var lastUsed = config.LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var note = string.IsNullOrWhiteSpace(config.Note) ? "（无备注）" : config.Note;

            var html = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{
            font-family: 'Segoe UI', Consolas, monospace;
            background: linear-gradient(135deg, #0a0a12 0%, #12121c 50%, #0a0a12 100%);
            color: #e0e0ff;
            padding: 0;
            height: 100vh;
            overflow-y: auto;
            overflow-x: hidden;
        }}
        /* 科技感网格背景 */
        body::before {{
            content: '';
            position: fixed;
            top: 0; left: 0; right: 0; bottom: 0;
            background-image:
                linear-gradient(rgba(0,212,255,0.03) 1px, transparent 1px),
                linear-gradient(90deg, rgba(0,212,255,0.03) 1px, transparent 1px);
            background-size: 20px 20px;
            pointer-events: none;
            z-index: -1;
        }}
        /* 自定义滚动条 */
        ::-webkit-scrollbar {{ width: 8px; }}
        ::-webkit-scrollbar-track {{ background: #0a0a12; }}
        ::-webkit-scrollbar-thumb {{
            background: linear-gradient(180deg, #00d4ff40, #bd00ff40);
            border-radius: 4px;
        }}
        ::-webkit-scrollbar-thumb:hover {{
            background: linear-gradient(180deg, #00d4ff60, #bd00ff60);
        }}
        .container {{
            padding: 25px;
            padding-top: 45px;  /* 为关闭按钮留出空间 */
            min-height: 100%;
            position: relative;
        }}
        .close-btn {{
            position: fixed;
            top: 12px;
            right: 12px;
            width: 30px;
            height: 30px;
            border: 1px solid #2a2a40;
            background: #0a0a14;
            color: #6a6a8a;
            border-radius: 4px;
            cursor: pointer;
            font-size: 16px;
            display: flex;
            align-items: center;
            justify-content: center;
            transition: all 0.2s;
            z-index: 1000;
        }}
        .close-btn:hover {{
            background: #ff2a6d30;
            border-color: #ff2a6d;
            color: #ff2a6d;
        }}
        .header {{
            text-align: center;
            margin-bottom: 20px;
        }}
        .icon {{
            font-size: 32px;
            color: #00d4ff;
            text-shadow: 0 0 15px #00d4ff80;
        }}
        .title {{
            font-size: 18px;
            color: #fff;
            margin: 10px 0 5px 0;
            font-weight: 600;
        }}
        .subtitle {{
            font-size: 11px;
            color: #6a6a8a;
        }}
        .divider {{
            height: 1px;
            background: linear-gradient(90deg, transparent, #00d4ff40, transparent);
            margin: 15px 0;
        }}
        .field {{
            margin: 12px 0;
        }}
        .field-label {{
            font-size: 11px;
            color: #00d4ff;
            text-transform: uppercase;
            letter-spacing: 1px;
            margin-bottom: 5px;
        }}
        .field-value {{
            font-size: 13px;
            color: #c0c0e0;
            background: #0c0c16;
            border: 1px solid #1a1a2a;
            border-radius: 4px;
            padding: 10px 12px;
            word-break: break-all;
            line-height: 1.5;
        }}
        .field-value.note {{
            min-height: 60px;
            white-space: pre-wrap;
        }}
        .meta-row {{
            display: flex;
            gap: 15px;
        }}
        .meta-row .field {{
            flex: 1;
        }}
        .status-badges {{
            display: flex;
            flex-wrap: wrap;
            gap: 8px;
            margin-top: 15px;
            padding-bottom: 20px;
        }}
        .badge {{
            font-size: 10px;
            padding: 4px 10px;
            border-radius: 3px;
            border: 1px solid;
        }}
        .badge.auto {{ background: #00d4ff15; border-color: #00d4ff40; color: #00d4ff; }}
        .badge.continue {{ background: #00ff9d15; border-color: #00ff9d40; color: #00ff9d; }}
        .badge.pinned {{ background: #bd00ff15; border-color: #bd00ff40; color: #bd00ff; }}
    </style>
</head>
<body>
    <div class='container'>
        <button class='close-btn' onclick='window.chrome.webview.postMessage(""close"")'>✕</button>
        <div class='header'>
            <div class='icon'>◈</div>
            <div class='title'>{EscapeHtml(config.Name)}</div>
            <div class='subtitle'>标签详情 // TAB DETAILS</div>
        </div>
        <div class='divider'></div>

        <div class='field'>
            <div class='field-label'>📝 备注</div>
            <div class='field-value note'>{EscapeHtml(note)}</div>
        </div>

        <div class='field'>
            <div class='field-label'>📂 工作目录</div>
            <div class='field-value'>{EscapeHtml(config.WorkingDirectory)}</div>
        </div>

        <div class='meta-row'>
            <div class='field'>
                <div class='field-label'>📅 创建时间</div>
                <div class='field-value'>{createdAt}</div>
            </div>
            <div class='field'>
                <div class='field-label'>🕐 最后使用</div>
                <div class='field-value'>{lastUsed}</div>
            </div>
        </div>

        <div class='status-badges'>
            {(config.AutoRunClaude ? "<span class='badge auto'>🤖 Auto Claude</span>" : "")}
            {(config.ContinueSession ? "<span class='badge continue'>🔄 Continue Session</span>" : "")}
            {(config.IsPinned ? "<span class='badge pinned'>📌 Pinned</span>" : "")}
        </div>
    </div>
</body>
</html>";

            ShowCyberDialog("标签详情", 480, 520, html);
        }

        // 关于对话框
        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var currentYear = DateTime.Now.Year;
            var html = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{
            font-family: 'Segoe UI', 'Microsoft YaHei', sans-serif;
            background: linear-gradient(135deg, #0a0a12 0%, #12121c 50%, #0a0a12 100%);
            color: #e0e0ff;
            padding: 0;
            height: 100vh;
            overflow-y: auto;
            overflow-x: hidden;
        }}
        /* 科技感网格背景 */
        body::before {{
            content: '';
            position: fixed;
            top: 0; left: 0; right: 0; bottom: 0;
            background-image:
                linear-gradient(rgba(0,212,255,0.04) 1px, transparent 1px),
                linear-gradient(90deg, rgba(0,212,255,0.04) 1px, transparent 1px);
            background-size: 25px 25px;
            pointer-events: none;
            z-index: -1;
            animation: gridMove 30s linear infinite;
        }}
        @keyframes gridMove {{
            0% {{ transform: translate(0, 0); }}
            100% {{ transform: translate(25px, 25px); }}
        }}
        /* 扫描线 */
        body::after {{
            content: '';
            position: fixed;
            top: 0; left: 0; right: 0;
            height: 2px;
            background: linear-gradient(90deg, transparent, #00d4ff80, transparent);
            animation: scanLine 4s ease-in-out infinite;
            z-index: 1000;
        }}
        @keyframes scanLine {{
            0% {{ top: 0; opacity: 0; }}
            10% {{ opacity: 1; }}
            90% {{ opacity: 1; }}
            100% {{ top: 100%; opacity: 0; }}
        }}
        /* 自定义滚动条 */
        ::-webkit-scrollbar {{ width: 8px; }}
        ::-webkit-scrollbar-track {{ background: #0a0a12; }}
        ::-webkit-scrollbar-thumb {{
            background: linear-gradient(180deg, #00d4ff40, #bd00ff40);
            border-radius: 4px;
        }}
        ::-webkit-scrollbar-thumb:hover {{
            background: linear-gradient(180deg, #00d4ff60, #bd00ff60);
        }}
        .container {{
            padding: 30px;
            padding-top: 50px;  /* 为关闭按钮留出空间 */
            min-height: 100%;
            display: flex;
            flex-direction: column;
            position: relative;
        }}
        .close-btn {{
            position: fixed;
            top: 12px;
            right: 12px;
            width: 32px;
            height: 32px;
            border: 1px solid #2a2a40;
            background: #0a0a14;
            color: #6a6a8a;
            border-radius: 6px;
            cursor: pointer;
            font-size: 18px;
            display: flex;
            align-items: center;
            justify-content: center;
            transition: all 0.2s;
            z-index: 1000;
        }}
        .close-btn:hover {{
            background: #ff2a6d30;
            border-color: #ff2a6d;
            color: #ff2a6d;
            box-shadow: 0 0 15px #ff2a6d40;
        }}
        .logo-container {{
            text-align: center;
            margin-bottom: 25px;
        }}
        .logo {{
            font-size: 64px;
            color: #00d4ff;
            text-shadow: 0 0 30px #00d4ff80, 0 0 60px #00d4ff40;
            animation: logoPulse 2s ease-in-out infinite;
        }}
        @keyframes logoPulse {{
            0%, 100% {{ transform: scale(1); text-shadow: 0 0 30px #00d4ff80; }}
            50% {{ transform: scale(1.05); text-shadow: 0 0 40px #00d4ff, 0 0 60px #bd00ff60; }}
        }}
        .app-name {{
            font-size: 32px;
            font-weight: bold;
            color: #fff;
            margin: 15px 0 8px 0;
            letter-spacing: 3px;
            text-shadow: 0 0 20px #00d4ff40;
        }}
        .tagline {{
            font-size: 14px;
            color: #00d4ff;
            letter-spacing: 2px;
            margin-bottom: 5px;
        }}
        .version {{
            font-size: 12px;
            color: #6a6a8a;
        }}
        .divider {{
            height: 2px;
            background: linear-gradient(90deg, transparent, #00d4ff60, #bd00ff60, transparent);
            margin: 25px 0;
            border-radius: 1px;
        }}
        .section {{
            margin: 20px 0;
        }}
        .section-title {{
            color: #00d4ff;
            font-size: 16px;
            font-weight: bold;
            margin-bottom: 12px;
            display: flex;
            align-items: center;
            gap: 8px;
        }}
        .section-title::before {{
            content: '▸';
            color: #bd00ff;
        }}
        .section-content {{
            font-size: 15px;
            color: #c0c0e0;
            line-height: 1.8;
        }}
        .features {{
            display: flex;
            flex-wrap: wrap;
            gap: 10px;
            margin-top: 5px;
        }}
        .feature {{
            background: linear-gradient(135deg, #00d4ff15, #bd00ff10);
            border: 1px solid #00d4ff30;
            border-radius: 6px;
            padding: 8px 14px;
            font-size: 13px;
            color: #00d4ff;
            transition: all 0.3s;
            cursor: default;
        }}
        .feature:hover {{
            background: linear-gradient(135deg, #00d4ff25, #bd00ff20);
            border-color: #00d4ff60;
            box-shadow: 0 0 15px #00d4ff30;
            transform: translateY(-2px);
        }}
        .developer-section {{
            background: linear-gradient(135deg, #0c0c18, #14141f);
            border: 1px solid #1a1a2a;
            border-radius: 10px;
            padding: 20px;
            margin: 20px 0;
        }}
        .developer-title {{
            color: #bd00ff;
            font-size: 16px;
            font-weight: bold;
            margin-bottom: 15px;
            display: flex;
            align-items: center;
            gap: 8px;
        }}
        .contact-item {{
            display: flex;
            align-items: center;
            gap: 12px;
            margin: 12px 0;
            padding: 12px 15px;
            background: #0a0a14;
            border: 1px solid #1a1a2a;
            border-radius: 8px;
            transition: all 0.3s;
        }}
        .contact-item:hover {{
            border-color: #00d4ff40;
            box-shadow: 0 0 10px #00d4ff20;
        }}
        .contact-icon {{
            font-size: 24px;
        }}
        .contact-info {{
            flex: 1;
        }}
        .contact-label {{
            font-size: 11px;
            color: #6a6a8a;
            text-transform: uppercase;
            letter-spacing: 1px;
        }}
        .contact-value {{
            font-size: 14px;
            color: #e0e0ff;
            margin-top: 3px;
            word-break: break-all;
        }}
        .contact-value a {{
            color: #00d4ff;
            text-decoration: none;
            transition: all 0.2s;
        }}
        .contact-value a:hover {{
            color: #bd00ff;
            text-shadow: 0 0 10px #00d4ff60;
        }}
        .copy-btn {{
            background: #00d4ff20;
            border: 1px solid #00d4ff40;
            color: #00d4ff;
            padding: 6px 12px;
            border-radius: 4px;
            font-size: 11px;
            cursor: pointer;
            transition: all 0.2s;
        }}
        .copy-btn:hover {{
            background: #00d4ff30;
            box-shadow: 0 0 10px #00d4ff40;
        }}
        .footer {{
            margin-top: auto;
            text-align: center;
            padding: 20px 0;
            border-top: 1px solid #1a1a2a;
        }}
        .footer-text {{
            font-size: 13px;
            color: #4a4a6a;
        }}
        .heart {{
            color: #ff0080;
            animation: heartbeat 1s ease-in-out infinite;
        }}
        @keyframes heartbeat {{
            0%, 100% {{ transform: scale(1); }}
            50% {{ transform: scale(1.2); }}
        }}
    </style>
</head>
<body>
    <div class='container'>
        <button class='close-btn' onclick='window.chrome.webview.postMessage(""close"")'>✕</button>

        <div class='logo-container'>
            <div class='logo'>◈</div>
            <div class='app-name'>QIANTERMINALCODE</div>
            <div class='tagline'>AI 编程助手终端管理器</div>
            <div class='version'>Version 1.0.0 // Terminal Edition</div>
        </div>

        <div class='divider'></div>

        <div class='section'>
            <div class='section-title'>关于本项目</div>
            <div class='section-content'>
                QianTerminalCode 是一款专为 AI 编程助手设计的多标签终端管理器，
                让您可以同时管理多个 Claude Code 工作会话，提升开发效率。
            </div>
        </div>

        <div class='section'>
            <div class='section-title'>功能特性</div>
            <div class='features'>
                <span class='feature'>🖥️ 多标签终端</span>
                <span class='feature'>💾 会话保存/恢复</span>
                <span class='feature'>🤖 自动运行 Claude</span>
                <span class='feature'>📜 历史记录</span>
                <span class='feature'>🏷️ 标签备注</span>
                <span class='feature'>🎨 科技风 UI</span>
            </div>
        </div>

        <div class='developer-section'>
            <div class='developer-title'>👨‍💻 开发者信息</div>

            <div class='contact-item'>
                <div class='contact-icon'>💬</div>
                <div class='contact-info'>
                    <div class='contact-label'>微信号</div>
                    <div class='contact-value'>qian913761489</div>
                </div>
                <button class='copy-btn' onclick=""navigator.clipboard.writeText('qian913761489');this.textContent='已复制!';setTimeout(()=>this.textContent='复制',1500)"">复制</button>
            </div>

            <div class='contact-item'>
                <div class='contact-icon'>🌐</div>
                <div class='contact-info'>
                    <div class='contact-label'>Linux.do 主页</div>
                    <div class='contact-value'>
                        <a href='https://linux.do/u/ruiqian_qin/summary' target='_blank'>linux.do/u/ruiqian_qin</a>
                    </div>
                </div>
                <button class='copy-btn' onclick=""navigator.clipboard.writeText('https://linux.do/u/ruiqian_qin/summary');this.textContent='已复制!';setTimeout(()=>this.textContent='复制',1500)"">复制</button>
            </div>
        </div>

        <div class='footer'>
            <div class='footer-text'>
                Made with <span class='heart'>❤️</span> by ruiqian_qin<br>
                © {currentYear} QianTerminalCode // All Rights Reserved
            </div>
        </div>
    </div>
</body>
</html>";

            ShowCyberDialog("关于 QianTerminalCode", 550, 680, html);
        }

        /// <summary>
        /// 通用科技风对话框
        /// </summary>
        private void ShowCyberDialog(string title, int width, int height, string html)
        {
            var dialog = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize
            };

            var webView = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 10, 10, 18)
            };

            var border = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 10, 18)),
                BorderBrush = new System.Windows.Media.LinearGradientBrush(
                    System.Windows.Media.Color.FromRgb(0, 212, 255),
                    System.Windows.Media.Color.FromRgb(189, 0, 255),
                    45),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = webView,
                Margin = new Thickness(5)
            };

            // 添加发光效果
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Color.FromRgb(0, 212, 255),
                BlurRadius = 15,
                ShadowDepth = 0,
                Opacity = 0.3
            };

            dialog.Content = border;
            dialog.MouseLeftButtonDown += (s, _) =>
            {
                try { dialog.DragMove(); } catch { }
            };
            dialog.KeyDown += (s, args) =>
            {
                if (args.Key == Key.Escape) dialog.Close();
            };

            dialog.Loaded += async (s, _) =>
            {
                await webView.EnsureCoreWebView2Async();
                webView.CoreWebView2.WebMessageReceived += (sender, args) =>
                {
                    if (args.TryGetWebMessageAsString() == "close")
                    {
                        dialog.Close();
                    }
                };
                webView.NavigateToString(html);
            };

            dialog.ShowDialog();
        }

        /// <summary>
        /// HTML 转义
        /// </summary>
        private static string EscapeHtml(string text)
        {
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
        }

        #region 全局快捷键和悬浮状态栏

        /// <summary>
        /// 窗口句柄初始化完成后注册全局快捷键
        /// </summary>
        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(WndProc);

            // 注册全局快捷键
            RegisterGlobalHotkey();
        }

        /// <summary>
        /// 注册全局快捷键 (Ctrl+Shift+Space)
        /// </summary>
        private void RegisterGlobalHotkey()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            // 默认: Ctrl+Shift+Space
            uint modifiers = MOD_CONTROL | MOD_SHIFT;
            uint key = VK_SPACE;

            bool success = RegisterHotKey(handle, HOTKEY_ID, modifiers, key);
            if (!success)
            {
                System.Diagnostics.Debug.WriteLine("Failed to register global hotkey Ctrl+Shift+Space");
            }
        }

        /// <summary>
        /// 注销全局快捷键
        /// </summary>
        private void UnregisterGlobalHotkey()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                UnregisterHotKey(handle, HOTKEY_ID);
            }

            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;
        }

        /// <summary>
        /// Windows 消息处理
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                // 触发悬浮状态栏
                ToggleFloatingStatusBar();
                handled = true;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 切换悬浮状态栏显示
        /// </summary>
        private void ToggleFloatingStatusBar()
        {
            if (DataContext is not MainWindowViewModel vm) return;

            // 如果悬浮栏已显示且可见，则隐藏
            if (_floatingStatusBar != null && _floatingStatusBar.IsVisible)
            {
                _floatingStatusBar.HideWithAnimation();
                return;
            }

            // 创建或显示悬浮栏
            if (_floatingStatusBar == null)
            {
                _floatingStatusBar = new FloatingStatusBar(
                    vm.TerminalTabs,
                    OnFloatingStatusBarTabSelected
                );
            }

            _floatingStatusBar.ShowWithAnimation();
        }

        /// <summary>
        /// 悬浮状态栏标签选中回调
        /// </summary>
        private void OnFloatingStatusBarTabSelected(TerminalTabViewModel tab)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // 选中标签
                vm.SelectedTab = tab;

                // 激活主窗口
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Show();
                Activate();
                Focus();
            }
        }

        #endregion
    }
}
