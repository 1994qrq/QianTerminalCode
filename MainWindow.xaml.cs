using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using CodeBridge.ViewModels;
using CodeBridge.Views;
using CodeBridge.Services;
using System.Drawing;
using System.IO;
using WinForms = System.Windows.Forms;

namespace CodeBridge
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // 悬浮状态栏实例
        private FloatingStatusBar? _floatingStatusBar;

        // 系统托盘图标
        private WinForms.NotifyIcon? _notifyIcon;
        private CyberTrayMenu? _trayMenu;
        private bool _isReallyClosing = false;

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

            // 初始化系统托盘图标
            InitializeNotifyIcon();

            // 初始隐藏主内容，显示加载界面
            MainContent.Opacity = 0;
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 初始化系统托盘图标
        /// </summary>
        private void InitializeNotifyIcon()
        {
            _notifyIcon = new WinForms.NotifyIcon
            {
                Text = "飞跃侠·CodeBridge - AI终端管理器",
                Visible = false
            };

            // 加载图标
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(iconPath))
                {
                    _notifyIcon.Icon = new Icon(iconPath);
                }
                else
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }

            // 双击托盘图标显示窗口
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();

            // 右键显示自定义菜单
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Right)
                {
                    ShowCyberTrayMenu();
                }
            };
        }

        /// <summary>
        /// 显示赛博朋克风格的托盘菜单
        /// </summary>
        private void ShowCyberTrayMenu()
        {
            if (_trayMenu == null)
            {
                _trayMenu = new CyberTrayMenu();
                _trayMenu.ShowWindowRequested += ShowMainWindow;
                _trayMenu.ExitRequested += TrayRequestExit;  // 改为显示确认对话框
            }

            // 获取鼠标位置
            var cursorPos = WinForms.Cursor.Position;
            _trayMenu.ShowAt(cursorPos.X, cursorPos.Y);
        }

        /// <summary>
        /// 显示主窗口
        /// </summary>
        private void ShowMainWindow()
        {
            Show();
            // 根据用户配置决定是否全屏
            if (DataContext is MainWindowViewModel vm && vm.StartFullScreen)
            {
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            // 托盘图标保持可见，不隐藏
        }

        /// <summary>
        /// 从托盘请求退出（显示确认对话框）
        /// </summary>
        private void TrayRequestExit()
        {
            // 先显示主窗口
            Show();
            if (DataContext is MainWindowViewModel vm && vm.StartFullScreen)
            {
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = WindowState.Normal;
            }
            Activate();

            // 显示关闭确认对话框
            var result = CyberCloseDialog.Show(this);

            switch (result)
            {
                case CloseAction.MinimizeToTray:
                    // 最小化到托盘
                    Hide();
                    _notifyIcon!.ShowBalloonTip(2000, "飞跃侠·CodeBridge", "程序已最小化到托盘，双击图标恢复窗口", WinForms.ToolTipIcon.Info);
                    break;

                case CloseAction.Exit:
                    // 完全退出
                    RealExit();
                    break;

                case CloseAction.Cancel:
                default:
                    // 取消，什么都不做
                    break;
            }
        }

        /// <summary>
        /// 真正退出程序
        /// </summary>
        private void RealExit()
        {
            _isReallyClosing = true;

            // 清理托盘图标
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            // 关闭托盘菜单
            _trayMenu?.Close();

            // 注销全局快捷键
            UnregisterGlobalHotkey();

            // 关闭悬浮状态栏
            _floatingStatusBar?.Close();

            // 保存设置并关闭所有终端
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Shutdown();
            }

            // 强制退出应用程序
            Application.Current.Shutdown();
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

                // 检查是否需要显示首次启动引导
                if (!vm.HasCompletedOnboarding)
                {
                    // 延迟显示引导，让主界面先完全加载
                    await Task.Delay(500);
                    ShowOnboardingGuide(vm);
                }
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

            // 如果是真正退出（从托盘菜单选择），直接退出
            if (_isReallyClosing)
            {
                // 清理托盘图标
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }

                // 保存设置并关闭所有终端
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.Shutdown();
                }
                return;
            }

            // 显示赛博朋克风格的关闭选项对话框
            var result = CyberCloseDialog.Show(this);

            switch (result)
            {
                case CloseAction.MinimizeToTray:
                    // 最小化到托盘
                    e.Cancel = true;
                    Hide();
                    _notifyIcon!.Visible = true;
                    _notifyIcon.ShowBalloonTip(2000, "CodeBridge", "程序已最小化到托盘，双击图标恢复窗口", WinForms.ToolTipIcon.Info);
                    // 重新注册快捷键
                    RegisterGlobalHotkey();
                    break;

                case CloseAction.Exit:
                    // 完全退出
                    // 清理托盘图标
                    if (_notifyIcon != null)
                    {
                        _notifyIcon.Visible = false;
                        _notifyIcon.Dispose();
                        _notifyIcon = null;
                    }

                    // 保存设置并关闭所有终端
                    if (DataContext is MainWindowViewModel vm2)
                    {
                        vm2.Shutdown();
                    }
                    break;

                case CloseAction.Cancel:
                default:
                    // 取消关闭
                    e.Cancel = true;
                    // 重新注册快捷键
                    RegisterGlobalHotkey();
                    break;
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

        #region 标签页拖拽排序

        private System.Windows.Point _tabDragStartPoint;
        private bool _isTabDragging = false;
        private TabItem? _draggedTabItem = null;

        private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TabItem tabItem)
            {
                _tabDragStartPoint = e.GetPosition(null);
                _draggedTabItem = tabItem;
                _isTabDragging = false;
            }
        }

        private void TabItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedTabItem == null)
                return;

            System.Windows.Point currentPos = e.GetPosition(null);
            Vector diff = _tabDragStartPoint - currentPos;

            // 检查是否移动足够距离以开始拖拽
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (!_isTabDragging && _draggedTabItem.DataContext is TerminalTabViewModel tabData)
                {
                    _isTabDragging = true;
                    _draggedTabItem.Opacity = 0.6;

                    var data = new DataObject("TabItem", tabData);
                    DragDrop.DoDragDrop(_draggedTabItem, data, DragDropEffects.Move);

                    _draggedTabItem.Opacity = 1.0;
                    _isTabDragging = false;
                    _draggedTabItem = null;
                }
            }
        }

        private void TabItem_DragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("TabItem") || sender == _draggedTabItem)
            {
                e.Effects = DragDropEffects.None;
            }
            else
            {
                e.Effects = DragDropEffects.Move;
                if (sender is TabItem targetTabItem)
                {
                    // 高亮显示目标位置
                    targetTabItem.BorderBrush = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0, 212, 255));
                    targetTabItem.BorderThickness = new Thickness(2, 0, 0, 0);
                }
            }
            e.Handled = true;
        }

        private void TabItem_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is TabItem targetTabItem)
            {
                // 移除高亮
                targetTabItem.BorderBrush = null;
                targetTabItem.BorderThickness = new Thickness(0);
            }
        }

        private void TabItem_Drop(object sender, DragEventArgs e)
        {
            if (sender is TabItem targetTabItem)
            {
                // 移除高亮
                targetTabItem.BorderBrush = null;
                targetTabItem.BorderThickness = new Thickness(0);

                if (e.Data.GetDataPresent("TabItem"))
                {
                    var draggedTab = e.Data.GetData("TabItem") as TerminalTabViewModel;
                    var targetTab = targetTabItem.DataContext as TerminalTabViewModel;

                    if (draggedTab != null && targetTab != null && draggedTab != targetTab)
                    {
                        if (DataContext is MainWindowViewModel vm)
                        {
                            var draggedIndex = vm.TerminalTabs.IndexOf(draggedTab);
                            var targetIndex = vm.TerminalTabs.IndexOf(targetTab);

                            if (draggedIndex >= 0 && targetIndex >= 0)
                            {
                                vm.TerminalTabs.Move(draggedIndex, targetIndex);
                            }
                        }
                    }
                }
            }
            e.Handled = true;
        }

        #endregion

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

        // 显示标签详情对话框（可编辑）
        private void ShowTabDetailsDialog(TerminalTabViewModel tab)
        {
            var config = tab.Config;
            var createdAt = config.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var lastUsed = config.LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var originalPath = config.WorkingDirectory;
            var originalNote = config.Note ?? "";

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
        ::-webkit-scrollbar {{ width: 8px; }}
        ::-webkit-scrollbar-track {{ background: #0a0a12; }}
        ::-webkit-scrollbar-thumb {{
            background: linear-gradient(180deg, #00d4ff40, #bd00ff40);
            border-radius: 4px;
        }}
        .container {{
            padding: 25px;
            padding-top: 45px;
            min-height: 100%;
            display: flex;
            flex-direction: column;
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
        .input {{
            width: 100%;
            padding: 10px 12px;
            background: #0c0c16;
            border: 1px solid #2a2a40;
            border-radius: 4px;
            color: #e0e0ff;
            font-size: 13px;
            outline: none;
            transition: all 0.2s;
        }}
        .input:focus {{
            border-color: #00d4ff;
            box-shadow: 0 0 8px #00d4ff30;
        }}
        .textarea {{
            width: 100%;
            min-height: 80px;
            padding: 10px 12px;
            background: #0c0c16;
            border: 1px solid #2a2a40;
            border-radius: 4px;
            color: #e0e0ff;
            font-size: 13px;
            outline: none;
            resize: vertical;
            font-family: inherit;
            transition: all 0.2s;
        }}
        .textarea:focus {{
            border-color: #00d4ff;
            box-shadow: 0 0 8px #00d4ff30;
        }}
        .path-row {{
            display: flex;
            gap: 8px;
        }}
        .path-row .input {{
            flex: 1;
        }}
        .browse-btn {{
            padding: 10px 14px;
            background: #14141f;
            border: 1px solid #2a2a40;
            border-radius: 4px;
            color: #00d4ff;
            font-size: 14px;
            cursor: pointer;
            transition: all 0.2s;
        }}
        .browse-btn:hover {{
            background: #00d4ff20;
            border-color: #00d4ff;
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
            margin-top: 10px;
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
        .options {{
            display: flex;
            gap: 15px;
            margin-top: 10px;
        }}
        .option {{
            display: flex;
            align-items: center;
            gap: 8px;
            cursor: pointer;
            padding: 8px 12px;
            background: #0c0c16;
            border: 1px solid #2a2a40;
            border-radius: 6px;
            transition: all 0.2s;
        }}
        .option:hover {{
            border-color: #00d4ff40;
        }}
        .option.checked {{
            border-color: #00d4ff60;
            background: #00d4ff10;
        }}
        .option input {{
            width: 16px;
            height: 16px;
            accent-color: #00d4ff;
            cursor: pointer;
        }}
        .option span {{
            font-size: 12px;
            color: #c0c0e0;
        }}
        .option .label-icon {{
            font-size: 14px;
        }}
        .buttons {{
            display: flex;
            gap: 12px;
            margin-top: auto;
            padding-top: 20px;
        }}
        .btn {{
            flex: 1;
            padding: 12px;
            border-radius: 6px;
            font-size: 13px;
            font-weight: bold;
            cursor: pointer;
            transition: all 0.2s;
            border: 1px solid;
        }}
        .btn-primary {{
            background: linear-gradient(135deg, #00d4ff30, #bd00ff20);
            border-color: #00d4ff60;
            color: #fff;
        }}
        .btn-primary:hover {{
            background: linear-gradient(135deg, #00d4ff50, #bd00ff40);
            box-shadow: 0 0 15px #00d4ff40;
        }}
        .btn-secondary {{
            background: transparent;
            border-color: #2a2a40;
            color: #6a6a8a;
        }}
        .btn-secondary:hover {{
            border-color: #4a4a6a;
            color: #a0a0c0;
        }}
        .btn-restart {{
            background: linear-gradient(135deg, #ff6b3520, #ff2a6d20);
            border-color: #ff6b3560;
            color: #ff6b35;
        }}
        .btn-restart:hover {{
            background: linear-gradient(135deg, #ff6b3540, #ff2a6d40);
            box-shadow: 0 0 15px #ff6b3540;
        }}
        .hint {{
            font-size: 10px;
            color: #4a4a6a;
            margin-top: 4px;
        }}
    </style>
</head>
<body>
    <button class='close-btn' onclick='cancel()'>✕</button>
    <div class='container'>
        <div class='header'>
            <div class='icon'>◈</div>
            <div class='title'>{EscapeHtml(config.Name)}</div>
            <div class='subtitle'>标签详情 // TAB DETAILS</div>
        </div>
        <div class='divider'></div>

        <div class='field'>
            <div class='field-label'>📝 备注</div>
            <textarea id='noteInput' class='textarea' placeholder='添加备注便于识别...'>{EscapeHtml(originalNote)}</textarea>
        </div>

        <div class='field'>
            <div class='field-label'>📂 工作目录</div>
            <div class='path-row'>
                <input type='text' id='pathInput' class='input' value='{EscapeHtml(originalPath)}'/>
                <button class='browse-btn' onclick='browseFolder()'>📁</button>
            </div>
            <div class='hint'>⚠️ 修改路径后需要重新载入终端</div>
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
            {(config.IsPinned ? "<span class='badge pinned'>📌 Pinned</span>" : "")}
        </div>

        <div class='field'>
            <div class='field-label'>⚙️ 选项</div>
            <div class='options'>
                <label class='option' id='autoOption'>
                    <input type='checkbox' id='autoRunCheck' {(config.AutoRunClaude ? "checked" : "")}/>
                    <span class='label-icon'>🤖</span>
                    <span>Auto (跳过权限确认)</span>
                </label>
                <label class='option' id='continueOption'>
                    <input type='checkbox' id='continueCheck' {(config.ContinueSession ? "checked" : "")}/>
                    <span class='label-icon'>🔄</span>
                    <span>-c (继续会话)</span>
                </label>
            </div>
        </div>

        <div class='buttons'>
            <button class='btn btn-secondary' onclick='cancel()'>取消</button>
            <button class='btn btn-restart' onclick='restart()'>💾 保存并重启</button>
            <button class='btn btn-primary' onclick='save()'>保存修改</button>
        </div>
    </div>

    <script>
        const originalPath = '{EscapeJs(originalPath)}';
        const originalNote = '{EscapeJs(originalNote)}';

        function browseFolder() {{
            window.chrome.webview.postMessage(JSON.stringify({{ action: 'browse' }}));
        }}

        function setPath(path) {{
            document.getElementById('pathInput').value = path;
        }}

        function save() {{
            const newPath = document.getElementById('pathInput').value.trim();
            const newNote = document.getElementById('noteInput').value.trim();
            const autoRun = document.getElementById('autoRunCheck').checked;
            const continueSession = document.getElementById('continueCheck').checked;
            const pathChanged = newPath !== originalPath;

            window.chrome.webview.postMessage(JSON.stringify({{
                action: 'save',
                path: newPath,
                note: newNote,
                autoRun: autoRun,
                continueSession: continueSession,
                pathChanged: pathChanged
            }}));
        }}

        function cancel() {{
            window.chrome.webview.postMessage(JSON.stringify({{ action: 'close' }}));
        }}

        function restart() {{
            const newPath = document.getElementById('pathInput').value.trim();
            const newNote = document.getElementById('noteInput').value.trim();
            const autoRun = document.getElementById('autoRunCheck').checked;
            const continueSession = document.getElementById('continueCheck').checked;
            const pathChanged = newPath !== originalPath;

            window.chrome.webview.postMessage(JSON.stringify({{
                action: 'restart',
                path: newPath,
                note: newNote,
                autoRun: autoRun,
                continueSession: continueSession,
                pathChanged: pathChanged
            }}));
        }}

        document.addEventListener('keydown', function(e) {{
            if (e.key === 'Escape') cancel();
            if (e.ctrlKey && e.key === 's') {{ e.preventDefault(); save(); }}
        }});
    </script>
</body>
</html>";

            var dialog = new Window
            {
                Title = "标签详情",
                Width = 500,
                Height = 560,
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

            dialog.Loaded += async (s, _) =>
            {
                await webView.EnsureCoreWebView2Async();
                webView.CoreWebView2.WebMessageReceived += (sender, args) =>
                {
                    try
                    {
                        var json = args.TryGetWebMessageAsString();
                        var msg = System.Text.Json.JsonDocument.Parse(json).RootElement;
                        var action = msg.GetProperty("action").GetString();

                        // 关键修复：使用 BeginInvoke 延迟执行，避免 WebView2 崩溃
                        Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                HandleTabDetailsAction(action, msg, dialog, webView, tab, config);
                            }
                            catch { }
                        });
                    }
                    catch { }
                };
                webView.NavigateToString(html);
            };

            dialog.ShowDialog();
        }

        /// <summary>
        /// 处理标签详情对话框的操作（从 WebView2 回调中分离出来避免崩溃）
        /// </summary>
        private void HandleTabDetailsAction(string? action, System.Text.Json.JsonElement msg, Window dialog, Microsoft.Web.WebView2.Wpf.WebView2 webView, TerminalTabViewModel tab, Models.TabConfig config)
        {
            switch (action)
            {
                case "browse":
                    var folderDialog = new System.Windows.Forms.FolderBrowserDialog
                    {
                        Description = "选择工作目录",
                        ShowNewFolderButton = true,
                        SelectedPath = config.WorkingDirectory
                    };
                    if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        var escapedPath = folderDialog.SelectedPath.Replace("\\", "\\\\").Replace("'", "\\'");
                        webView.CoreWebView2.ExecuteScriptAsync($"setPath('{escapedPath}')");
                    }
                    break;

                case "save":
                    var newPath = msg.GetProperty("path").GetString() ?? "";
                    var newNote = msg.GetProperty("note").GetString() ?? "";
                    var autoRun = msg.GetProperty("autoRun").GetBoolean();
                    var continueSession = msg.GetProperty("continueSession").GetBoolean();
                    var pathChanged = msg.GetProperty("pathChanged").GetBoolean();

                    // 检查目录是否存在
                    if (!string.IsNullOrWhiteSpace(newPath) && !System.IO.Directory.Exists(newPath))
                    {
                        // 目录不存在，询问用户是否创建
                        var createResult = CyberConfirmDialog.Show(
                            owner: this,
                            title: "目录不存在",
                            message: $"目录 \"{newPath}\" 不存在。",
                            subMessage: "是否创建该目录？",
                            confirmText: "创建目录",
                            cancelText: "取消"
                        );

                        if (createResult)
                        {
                            try
                            {
                                System.IO.Directory.CreateDirectory(newPath);
                            }
                            catch (Exception ex)
                            {
                                CyberConfirmDialog.Show(
                                    owner: this,
                                    title: "创建失败",
                                    message: $"无法创建目录: {ex.Message}",
                                    subMessage: "请检查路径是否有效或是否有权限。",
                                    confirmText: "确定",
                                    cancelText: ""
                                );
                                break; // 创建失败，不保存
                            }
                        }
                        else
                        {
                            // 用户不创建，不允许保存
                            break;
                        }
                    }

                    if (pathChanged && !string.IsNullOrWhiteSpace(newPath))
                    {
                        // 路径已改变，询问用户是否重新载入
                        var confirmResult = CyberConfirmDialog.Show(
                            owner: this,
                            title: "路径已更改",
                            message: "工作目录已更改，需要重新载入终端。",
                            subMessage: "当前终端会话将被关闭并重新启动。",
                            confirmText: "重新载入",
                            cancelText: "取消修改"
                        );

                        if (confirmResult)
                        {
                            // 用户确认，更新配置并重新载入
                            config.Note = newNote;
                            config.AutoRunClaude = autoRun;
                            config.ContinueSession = continueSession;
                            config.WorkingDirectory = newPath;
                            config.Name = System.IO.Path.GetFileName(newPath) ?? "Terminal";
                            tab.NotifyDisplayNameChanged();

                            // 重新载入终端
                            if (DataContext is MainWindowViewModel vm)
                            {
                                vm.ReloadTab(tab);
                            }
                            dialog.Close();
                        }
                        // 用户取消，不做任何操作，对话框保持打开
                    }
                    else
                    {
                        // 保存所有修改
                        config.Note = newNote;
                        config.AutoRunClaude = autoRun;
                        config.ContinueSession = continueSession;
                        tab.NotifyDisplayNameChanged();
                        dialog.Close();
                    }
                    break;

                case "restart":
                    // 获取当前设置
                    var restartPath = msg.GetProperty("path").GetString() ?? "";
                    var restartNote = msg.GetProperty("note").GetString() ?? "";
                    var restartAutoRun = msg.GetProperty("autoRun").GetBoolean();
                    var restartContinueSession = msg.GetProperty("continueSession").GetBoolean();
                    var restartPathChanged = msg.GetProperty("pathChanged").GetBoolean();

                    // 检查目录是否存在（如果路径变更了）
                    if (restartPathChanged && !string.IsNullOrWhiteSpace(restartPath) && !System.IO.Directory.Exists(restartPath))
                    {
                        var createDirResult = CyberConfirmDialog.Show(
                            owner: this,
                            title: "目录不存在",
                            message: $"目录 \"{restartPath}\" 不存在。",
                            subMessage: "是否创建该目录？",
                            confirmText: "创建目录",
                            cancelText: "取消"
                        );

                        if (createDirResult)
                        {
                            try
                            {
                                System.IO.Directory.CreateDirectory(restartPath);
                            }
                            catch (Exception ex)
                            {
                                CyberConfirmDialog.Show(
                                    owner: this,
                                    title: "创建失败",
                                    message: $"无法创建目录: {ex.Message}",
                                    subMessage: "请检查路径是否有效或是否有权限。",
                                    confirmText: "确定",
                                    cancelText: ""
                                );
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }

                    // 确认保存并重启
                    var restartResult = CyberConfirmDialog.Show(
                        owner: this,
                        title: "保存并重启",
                        message: "确定要保存设置并重启终端会话吗？",
                        subMessage: "当前终端进程将被终止，设置将被保存后重新启动。",
                        confirmText: "保存并重启",
                        cancelText: "取消"
                    );

                    if (restartResult)
                    {
                        try
                        {
                            // 先保存设置
                            config.Note = restartNote;
                            config.AutoRunClaude = restartAutoRun;
                            config.ContinueSession = restartContinueSession;
                            if (restartPathChanged && !string.IsNullOrWhiteSpace(restartPath))
                            {
                                config.WorkingDirectory = restartPath;
                                config.Name = System.IO.Path.GetFileName(restartPath) ?? "Terminal";
                            }
                            tab.NotifyDisplayNameChanged();

                            // 重新载入终端
                            if (DataContext is MainWindowViewModel vmRestart)
                            {
                                vmRestart.ReloadTab(tab);
                            }
                            dialog.Close();
                        }
                        catch (Exception ex)
                        {
                            CyberConfirmDialog.Show(
                                owner: this,
                                title: "重启失败",
                                message: $"无法重启终端会话: {ex.Message}",
                                subMessage: "请稍后重试。",
                                confirmText: "确定",
                                cancelText: ""
                            );
                        }
                    }
                    break;

                case "close":
                    dialog.Close();
                    break;
            }
        }

        /// <summary>
        /// JavaScript 字符串转义
        /// </summary>
        private static string EscapeJs(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        // 新建标签按钮点击
        private void NewTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            ShowNewTabDialog(vm);
        }

        /// <summary>
        /// 显示新建标签对话框
        /// </summary>
        private void ShowNewTabDialog(MainWindowViewModel vm)
        {
            // 获取历史记录用于下拉选择
            var historyOptions = string.Join("", vm.History.Select(h =>
                $"<option value='{EscapeHtml(h.WorkingDirectory)}'>{EscapeHtml(h.WorkingDirectory)}</option>"));

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
        }}
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
        }}
        /* 自定义滚动条 */
        ::-webkit-scrollbar {{ width: 6px; }}
        ::-webkit-scrollbar-track {{ background: #0a0a12; }}
        ::-webkit-scrollbar-thumb {{
            background: linear-gradient(180deg, #00d4ff40, #bd00ff40);
            border-radius: 3px;
        }}
        .container {{
            padding: 25px;
            padding-top: 45px;
            min-height: 100%;
            display: flex;
            flex-direction: column;
        }}
        .close-btn {{
            position: fixed;
            top: 10px;
            right: 10px;
            width: 28px;
            height: 28px;
            border: 1px solid #2a2a40;
            background: #0a0a14;
            color: #6a6a8a;
            border-radius: 4px;
            cursor: pointer;
            font-size: 14px;
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
        .logo {{
            font-size: 36px;
            color: #00d4ff;
            text-shadow: 0 0 20px #00d4ff80;
        }}
        .title {{
            font-size: 18px;
            font-weight: bold;
            color: #fff;
            margin: 10px 0 5px 0;
        }}
        .subtitle {{
            font-size: 12px;
            color: #6a6a8a;
        }}
        .divider {{
            height: 1px;
            background: linear-gradient(90deg, transparent, #00d4ff40, transparent);
            margin: 15px 0;
        }}
        .form-group {{
            margin-bottom: 20px;
        }}
        .label {{
            font-size: 12px;
            color: #00d4ff;
            text-transform: uppercase;
            letter-spacing: 1px;
            margin-bottom: 8px;
            display: block;
        }}
        .input, .select {{
            width: 100%;
            padding: 12px 15px;
            background: #0c0c16;
            border: 1px solid #2a2a40;
            border-radius: 6px;
            color: #e0e0ff;
            font-size: 14px;
            outline: none;
            transition: all 0.2s;
        }}
        .input:focus, .select:focus {{
            border-color: #00d4ff;
            box-shadow: 0 0 10px #00d4ff30;
        }}
        .input::placeholder {{
            color: #4a4a6a;
        }}
        .select {{
            cursor: pointer;
        }}
        .select option {{
            background: #0c0c16;
            color: #e0e0ff;
        }}
        .path-row {{
            display: flex;
            gap: 10px;
        }}
        .path-row .input {{
            flex: 1;
        }}
        .browse-btn {{
            padding: 12px 16px;
            background: #14141f;
            border: 1px solid #2a2a40;
            border-radius: 6px;
            color: #00d4ff;
            font-size: 16px;
            cursor: pointer;
            transition: all 0.2s;
        }}
        .browse-btn:hover {{
            background: #00d4ff20;
            border-color: #00d4ff;
        }}
        .options {{
            display: flex;
            gap: 20px;
            margin-top: 5px;
        }}
        .option {{
            display: flex;
            align-items: center;
            gap: 8px;
            cursor: pointer;
        }}
        .option input {{
            width: 18px;
            height: 18px;
            accent-color: #00d4ff;
            cursor: pointer;
        }}
        .option span {{
            font-size: 13px;
            color: #c0c0e0;
        }}
        .buttons {{
            display: flex;
            gap: 12px;
            margin-top: auto;
            padding-top: 20px;
        }}
        .btn {{
            flex: 1;
            padding: 14px;
            border-radius: 6px;
            font-size: 14px;
            font-weight: bold;
            cursor: pointer;
            transition: all 0.2s;
            border: 1px solid;
        }}
        .btn-primary {{
            background: linear-gradient(135deg, #00d4ff30, #bd00ff20);
            border-color: #00d4ff60;
            color: #fff;
        }}
        .btn-primary:hover {{
            background: linear-gradient(135deg, #00d4ff50, #bd00ff40);
            box-shadow: 0 0 20px #00d4ff40;
        }}
        .btn-secondary {{
            background: transparent;
            border-color: #2a2a40;
            color: #6a6a8a;
        }}
        .btn-secondary:hover {{
            border-color: #4a4a6a;
            color: #a0a0c0;
        }}
        .error {{
            color: #ff2a6d;
            font-size: 11px;
            margin-top: 5px;
            display: none;
        }}
        .error.show {{
            display: block;
        }}
    </style>
</head>
<body>
    <button class='close-btn' onclick='cancel()'>✕</button>
    <div class='container'>
        <div class='header'>
            <div class='logo'>+</div>
            <div class='title'>新建终端标签</div>
            <div class='subtitle'>Create New Terminal Tab</div>
        </div>

        <div class='divider'></div>

        <div class='form-group'>
            <label class='label'>📂 工作目录 *</label>
            <div class='path-row'>
                <input type='text' id='pathInput' class='input' placeholder='输入或选择工作目录路径...' list='historyList'/>
                <button class='browse-btn' onclick='browseFolder()'>📁</button>
            </div>
            <datalist id='historyList'>
                {historyOptions}
            </datalist>
            <div id='pathError' class='error'>请输入有效的工作目录路径</div>
        </div>

        <div class='form-group'>
            <label class='label'>📝 标签备注 (可选)</label>
            <input type='text' id='noteInput' class='input' placeholder='为标签添加备注便于识别...'/>
        </div>

        <div class='form-group'>
            <label class='label'>⚙️ 选项</label>
            <div class='options'>
                <label class='option'>
                    <input type='checkbox' id='autoRun'/>
                    <span>Auto (跳过权限确认)</span>
                </label>
                <label class='option'>
                    <input type='checkbox' id='continueSession'/>
                    <span>-c (继续上次会话)</span>
                </label>
            </div>
        </div>

        <div class='buttons'>
            <button class='btn btn-secondary' onclick='cancel()'>取消</button>
            <button class='btn btn-primary' onclick='create()'>创建标签</button>
        </div>
    </div>

    <script>
        function browseFolder() {{
            window.chrome.webview.postMessage(JSON.stringify({{ action: 'browse' }}));
        }}

        function create() {{
            const path = document.getElementById('pathInput').value.trim();
            const note = document.getElementById('noteInput').value.trim();
            const autoRun = document.getElementById('autoRun').checked;
            const continueSession = document.getElementById('continueSession').checked;

            if (!path) {{
                document.getElementById('pathError').classList.add('show');
                return;
            }}
            document.getElementById('pathError').classList.remove('show');

            window.chrome.webview.postMessage(JSON.stringify({{
                action: 'create',
                path: path,
                note: note,
                autoRun: autoRun,
                continueSession: continueSession
            }}));
        }}

        function cancel() {{
            window.chrome.webview.postMessage(JSON.stringify({{ action: 'cancel' }}));
        }}

        // 设置路径（从浏览对话框返回）
        function setPath(path) {{
            document.getElementById('pathInput').value = path;
        }}

        // 按 Enter 键创建
        document.addEventListener('keydown', function(e) {{
            if (e.key === 'Enter') create();
            if (e.key === 'Escape') cancel();
        }});
    </script>
</body>
</html>";

            var dialog = new Window
            {
                Title = "新建终端标签",
                Width = 500,
                Height = 480,
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

            dialog.Loaded += async (s, _) =>
            {
                await webView.EnsureCoreWebView2Async();
                webView.CoreWebView2.WebMessageReceived += (sender, args) =>
                {
                    try
                    {
                        var json = args.TryGetWebMessageAsString();
                        var msg = System.Text.Json.JsonDocument.Parse(json).RootElement;
                        var action = msg.GetProperty("action").GetString();

                        // 关键修复：使用 BeginInvoke 延迟执行，避免 WebView2 崩溃
                        Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                switch (action)
                                {
                                    case "browse":
                                        var folderDialog = new System.Windows.Forms.FolderBrowserDialog
                                        {
                                            Description = "选择工作目录",
                                            ShowNewFolderButton = true
                                        };
                                        if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                                        {
                                            var escapedPath = folderDialog.SelectedPath.Replace("\\", "\\\\").Replace("'", "\\'");
                                            webView.CoreWebView2.ExecuteScriptAsync($"setPath('{escapedPath}')");
                                        }
                                        break;

                                    case "create":
                                        var path = msg.GetProperty("path").GetString();
                                        var note = msg.GetProperty("note").GetString();
                                        var autoRun = msg.GetProperty("autoRun").GetBoolean();
                                        var continueSession = msg.GetProperty("continueSession").GetBoolean();

                                        if (!string.IsNullOrWhiteSpace(path))
                                        {
                                            vm.CreateNewTab(path!, note ?? "", autoRun, continueSession);
                                            dialog.Close();
                                        }
                                        break;

                                    case "cancel":
                                        dialog.Close();
                                        break;
                                }
                            }
                            catch { }
                        });
                    }
                    catch { }
                };
                webView.NavigateToString(html);
            };

            dialog.ShowDialog();
        }

        // 关于对话框
        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowAboutDialog();
        }

        // 帮助指南菜单点击
        private void HelpMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowOnboardingGuide(DataContext as MainWindowViewModel, isManualTrigger: true);
        }

        // Hook 配置诊断菜单点击
        private void HookConfigMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowHookConfigDialog();
        }

        /// <summary>
        /// 显示 Hook 配置诊断对话框
        /// </summary>
        private void ShowHookConfigDialog()
        {
            var hookService = new HookConfigService();
            var html = GenerateHookConfigHtml(hookService);

            var dialog = new Window
            {
                Title = "Hook 通知配置",
                Width = 500,
                Height = 650,
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
                webView.CoreWebView2.WebMessageReceived += async (sender, args) =>
                {
                    try
                    {
                        var json = args.TryGetWebMessageAsString();
                        var msg = System.Text.Json.JsonDocument.Parse(json).RootElement;
                        var action = msg.GetProperty("action").GetString();

                        bool success = false;
                        string message = "";

                        switch (action)
                        {
                            case "close":
                                dialog.Close();
                                return;

                            case "refresh":
                                // 重新生成 HTML 并刷新页面
                                var newHtml = GenerateHookConfigHtml(hookService);
                                webView.NavigateToString(newHtml);
                                return;

                            case "backup":
                                var backupResult = hookService.BackupSettings();
                                success = backupResult.Success;
                                message = backupResult.Message;
                                break;

                            case "repair":
                                var repairResult = await hookService.RepairHookConfigurationAsync();
                                success = repairResult.Success;
                                message = repairResult.Message;
                                break;

                            case "remove":
                                var removeResult = await hookService.RemoveHookAsync();
                                success = removeResult.Success;
                                message = removeResult.Message;
                                break;

                            case "restore":
                                var fileName = msg.GetProperty("fileName").GetString() ?? "";
                                var restoreResult = await hookService.RestoreFromBackupAsync(fileName);
                                success = restoreResult.Success;
                                message = restoreResult.Message;
                                break;
                        }

                        await webView.CoreWebView2.ExecuteScriptAsync(
                            $"handleResult({success.ToString().ToLower()}, '{EscapeJs(message)}')");
                    }
                    catch (Exception ex)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync(
                            $"handleResult(false, '操作失败: {EscapeJs(ex.Message)}')");
                    }
                };
                webView.NavigateToString(html);
            };

            dialog.ShowDialog();
        }

        /// <summary>
        /// 生成 Hook 配置对话框的 HTML
        /// </summary>
        private string GenerateHookConfigHtml(HookConfigService hookService)
        {
            var diagnosis = hookService.DiagnoseHookConfiguration();
            var backups = hookService.ListBackups();

            // 生成问题列表 HTML
            var issuesHtml = diagnosis.Issues.Count > 0
                ? string.Join("", diagnosis.Issues.Select(i => $"<div class='issue-item'>⚠️ {EscapeHtml(i)}</div>"))
                : "<div class='ok-item'>✅ 没有发现问题</div>";

            // 生成建议列表 HTML
            var suggestionsHtml = diagnosis.Suggestions.Count > 0
                ? string.Join("", diagnosis.Suggestions.Select(s => $"<div class='suggestion-item'>💡 {EscapeHtml(s)}</div>"))
                : "";

            // 生成备份列表 HTML
            var backupsHtml = backups.Count > 0
                ? string.Join("", backups.Take(5).Select(b =>
                    $"<div class='backup-item' onclick=\"restore('{EscapeJs(b.FileName)}')\"><span class='backup-name'>{EscapeHtml(b.FileName)}</span><span class='backup-date'>{b.Created:yyyy-MM-dd HH:mm}</span></div>"))
                : "<div class='no-backup'>暂无备份</div>";

            // 状态颜色
            var statusColor = diagnosis.Status switch
            {
                HookConfigService.DiagnosisStatus.OK => "#00FF9D",
                HookConfigService.DiagnosisStatus.Warning => "#FFB800",
                HookConfigService.DiagnosisStatus.Error => "#FF2A6D",
                _ => "#6A6A8A"
            };

            var statusText = diagnosis.Status switch
            {
                HookConfigService.DiagnosisStatus.OK => "正常",
                HookConfigService.DiagnosisStatus.Warning => "需要配置",
                HookConfigService.DiagnosisStatus.Error => "配置错误",
                HookConfigService.DiagnosisStatus.NotInstalled => "未安装",
                _ => "未知"
            };

            return $@"
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
        }}
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
        ::-webkit-scrollbar {{ width: 6px; }}
        ::-webkit-scrollbar-track {{ background: #0a0a12; }}
        ::-webkit-scrollbar-thumb {{ background: linear-gradient(180deg, #00d4ff40, #bd00ff40); border-radius: 3px; }}
        .container {{ padding: 25px; padding-top: 50px; min-height: 100%; }}
        .close-btn {{
            position: fixed; top: 12px; right: 12px;
            width: 30px; height: 30px;
            border: 1px solid #2a2a40; background: #0a0a14; color: #6a6a8a;
            border-radius: 4px; cursor: pointer; font-size: 16px;
            display: flex; align-items: center; justify-content: center;
            transition: all 0.2s; z-index: 1000;
        }}
        .close-btn:hover {{ background: #ff2a6d30; border-color: #ff2a6d; color: #ff2a6d; }}
        .header {{ text-align: center; margin-bottom: 20px; }}
        .icon {{ font-size: 40px; color: #00d4ff; text-shadow: 0 0 20px #00d4ff80; }}
        .title {{ font-size: 20px; font-weight: bold; color: #fff; margin: 10px 0 5px 0; }}
        .subtitle {{ font-size: 12px; color: #6a6a8a; }}
        .divider {{ height: 1px; background: linear-gradient(90deg, transparent, #00d4ff40, transparent); margin: 15px 0; }}

        .status-card {{
            background: linear-gradient(135deg, #0c0c18, #14141f);
            border: 1px solid #1a1a2a; border-radius: 8px;
            padding: 15px; margin: 15px 0;
        }}
        .status-header {{ display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }}
        .status-dot {{ width: 12px; height: 12px; border-radius: 50%; background: {statusColor}; box-shadow: 0 0 10px {statusColor}; }}
        .status-text {{ font-size: 16px; font-weight: bold; color: {statusColor}; }}
        .status-message {{ font-size: 13px; color: #a0a0c0; }}

        .section {{ margin: 15px 0; }}
        .section-title {{ font-size: 13px; color: #00d4ff; font-weight: bold; margin-bottom: 10px; text-transform: uppercase; letter-spacing: 1px; }}

        .check-item {{ display: flex; align-items: center; gap: 10px; padding: 8px 0; border-bottom: 1px solid #1a1a2a; }}
        .check-icon {{ font-size: 16px; }}
        .check-label {{ font-size: 13px; color: #c0c0e0; flex: 1; }}
        .check-status {{ font-size: 12px; padding: 2px 8px; border-radius: 3px; }}
        .check-ok {{ background: #00ff9d20; color: #00ff9d; }}
        .check-fail {{ background: #ff2a6d20; color: #ff2a6d; }}

        .issue-item {{ padding: 8px 12px; background: #ff2a6d15; border-left: 3px solid #ff2a6d; margin: 5px 0; font-size: 12px; color: #ffa0b0; }}
        .ok-item {{ padding: 8px 12px; background: #00ff9d15; border-left: 3px solid #00ff9d; margin: 5px 0; font-size: 12px; color: #a0ffc0; }}
        .suggestion-item {{ padding: 8px 12px; background: #ffb80015; border-left: 3px solid #ffb800; margin: 5px 0; font-size: 12px; color: #ffe0a0; }}

        .backup-section {{ margin-top: 15px; }}
        .backup-item {{ display: flex; justify-content: space-between; padding: 10px 12px; background: #0c0c16; border: 1px solid #1a1a2a; border-radius: 4px; margin: 5px 0; cursor: pointer; transition: all 0.2s; }}
        .backup-item:hover {{ border-color: #00d4ff40; background: #00d4ff10; }}
        .backup-name {{ font-size: 11px; color: #00d4ff; font-family: Consolas, monospace; }}
        .backup-date {{ font-size: 11px; color: #6a6a8a; }}
        .no-backup {{ font-size: 12px; color: #4a4a6a; text-align: center; padding: 15px; }}

        .buttons {{ display: flex; gap: 10px; margin-top: 20px; }}
        .btn {{ flex: 1; padding: 12px; border-radius: 6px; font-size: 13px; font-weight: bold; cursor: pointer; transition: all 0.2s; border: 1px solid; }}
        .btn-primary {{ background: linear-gradient(135deg, #00d4ff30, #bd00ff20); border-color: #00d4ff60; color: #fff; }}
        .btn-primary:hover {{ background: linear-gradient(135deg, #00d4ff50, #bd00ff40); box-shadow: 0 0 15px #00d4ff40; }}
        .btn-secondary {{ background: transparent; border-color: #2a2a40; color: #6a6a8a; }}
        .btn-secondary:hover {{ border-color: #4a4a6a; color: #a0a0c0; }}
        .btn-danger {{ background: #ff2a6d20; border-color: #ff2a6d60; color: #ff2a6d; }}
        .btn-danger:hover {{ background: #ff2a6d40; box-shadow: 0 0 15px #ff2a6d40; }}

        .loading {{ display: none; position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: #0a0a12e0; z-index: 100; align-items: center; justify-content: center; }}
        .loading.show {{ display: flex; }}
        .loading-text {{ color: #00d4ff; font-size: 14px; }}

        .toast {{ position: fixed; bottom: 20px; left: 50%; transform: translateX(-50%); padding: 12px 24px; border-radius: 6px; font-size: 13px; opacity: 0; transition: opacity 0.3s; z-index: 200; }}
        .toast.show {{ opacity: 1; }}
        .toast.success {{ background: #00ff9d30; border: 1px solid #00ff9d; color: #00ff9d; }}
        .toast.error {{ background: #ff2a6d30; border: 1px solid #ff2a6d; color: #ff2a6d; }}
    </style>
</head>
<body>
    <div class='loading' id='loading'><span class='loading-text'>处理中...</span></div>
    <div class='toast' id='toast'></div>

    <div class='container'>
        <button class='close-btn' onclick='closeDialog()'>✕</button>

        <div class='header'>
            <div class='icon'>🔔</div>
            <div class='title'>Hook 通知配置</div>
            <div class='subtitle'>诊断和配置 Claude Code 任务完成通知</div>
        </div>

        <div class='divider'></div>

        <div class='status-card'>
            <div class='status-header'>
                <div class='status-dot'></div>
                <div class='status-text'>{statusText}</div>
            </div>
            <div class='status-message'>{EscapeHtml(diagnosis.Message)}</div>
        </div>

        <div class='section'>
            <div class='section-title'>配置检查</div>
            <div class='check-item'>
                <span class='check-icon'>📁</span>
                <span class='check-label'>.claude 目录</span>
                <span class='check-status {(diagnosis.ClaudeConfigExists ? "check-ok" : "check-fail")}'>{(diagnosis.ClaudeConfigExists ? "存在" : "不存在")}</span>
            </div>
            <div class='check-item'>
                <span class='check-icon'>📄</span>
                <span class='check-label'>settings.json</span>
                <span class='check-status {(diagnosis.SettingsFileExists ? "check-ok" : "check-fail")}'>{(diagnosis.SettingsFileExists ? "存在" : "不存在")}</span>
            </div>
            <div class='check-item'>
                <span class='check-icon'>📜</span>
                <span class='check-label'>Hook 脚本</span>
                <span class='check-status {(diagnosis.HookScriptExists ? "check-ok" : "check-fail")}'>{(diagnosis.HookScriptExists ? "已安装" : "未安装")}</span>
            </div>
            <div class='check-item'>
                <span class='check-icon'>⚙️</span>
                <span class='check-label'>JSON 格式</span>
                <span class='check-status {(diagnosis.IsValidJson ? "check-ok" : "check-fail")}'>{(diagnosis.IsValidJson ? "有效" : "无效")}</span>
            </div>
            <div class='check-item'>
                <span class='check-icon'>🔗</span>
                <span class='check-label'>Hook 已配置</span>
                <span class='check-status {(diagnosis.HookConfigured ? "check-ok" : "check-fail")}'>{(diagnosis.HookConfigured ? "是" : "否")}</span>
            </div>
        </div>

        <div class='section'>
            <div class='section-title'>诊断结果</div>
            {issuesHtml}
            {suggestionsHtml}
        </div>

        <div class='section backup-section'>
            <div class='section-title'>配置备份 (点击恢复)</div>
            {backupsHtml}
        </div>

        <div class='buttons'>
            <button class='btn btn-secondary' onclick='backup()'>📦 手动备份</button>
            <button class='btn btn-primary' onclick='repair()'>🔧 一键修复</button>
        </div>

        <div class='buttons' style='margin-top: 10px;'>
            <button class='btn btn-danger' onclick='remove()'>🗑️ 移除配置</button>
        </div>
    </div>

    <script>
        function showLoading() {{ document.getElementById('loading').classList.add('show'); }}
        function hideLoading() {{ document.getElementById('loading').classList.remove('show'); }}
        function showToast(msg, type) {{
            var t = document.getElementById('toast');
            t.textContent = msg;
            t.className = 'toast show ' + type;
            setTimeout(() => t.classList.remove('show'), 3000);
        }}
        function closeDialog() {{ window.chrome.webview.postMessage(JSON.stringify({{ action: 'close' }})); }}
        function backup() {{ showLoading(); window.chrome.webview.postMessage(JSON.stringify({{ action: 'backup' }})); }}
        function repair() {{ showLoading(); window.chrome.webview.postMessage(JSON.stringify({{ action: 'repair' }})); }}
        function remove() {{ showLoading(); window.chrome.webview.postMessage(JSON.stringify({{ action: 'remove' }})); }}
        function restore(fileName) {{ showLoading(); window.chrome.webview.postMessage(JSON.stringify({{ action: 'restore', fileName: fileName }})); }}
        function handleResult(success, message) {{
            hideLoading();
            showToast(message, success ? 'success' : 'error');
            if (success) setTimeout(() => window.chrome.webview.postMessage(JSON.stringify({{ action: 'refresh' }})), 1500);
        }}
    </script>
</body>
</html>";
        }

        /// <summary>
        /// 显示首次启动引导
        /// </summary>
        private void ShowOnboardingGuide(MainWindowViewModel? vm, bool isManualTrigger = false)
        {
            var html = GenerateOnboardingHtml();
            ShowCyberDialog("欢迎使用 飞跃侠·CodeBridge", 650, 720, html, onClose: () =>
            {
                // 标记引导已完成
                if (!isManualTrigger && vm != null)
                {
                    vm.MarkOnboardingCompleted();
                }
            });
        }

        /// <summary>
        /// 生成引导页面 HTML
        /// </summary>
        private string GenerateOnboardingHtml()
        {
            return @"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: 'Segoe UI', 'Microsoft YaHei', sans-serif;
            background: linear-gradient(135deg, #0a0a12 0%, #12121c 50%, #0a0a12 100%);
            color: #e0e0ff;
            padding: 0;
            height: 100vh;
            overflow-y: auto;
            overflow-x: hidden;
        }
        /* 科技感网格背景 */
        body::before {
            content: '';
            position: fixed;
            top: 0; left: 0; right: 0; bottom: 0;
            background-image:
                linear-gradient(rgba(0,212,255,0.04) 1px, transparent 1px),
                linear-gradient(90deg, rgba(0,212,255,0.04) 1px, transparent 1px);
            background-size: 25px 25px;
            pointer-events: none;
            z-index: -1;
        }
        /* 自定义滚动条 */
        ::-webkit-scrollbar { width: 8px; }
        ::-webkit-scrollbar-track { background: #0a0a12; }
        ::-webkit-scrollbar-thumb {
            background: linear-gradient(180deg, #00d4ff40, #bd00ff40);
            border-radius: 4px;
        }
        .container {
            padding: 30px;
            padding-top: 50px;
            min-height: 100%;
        }
        .close-btn {
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
        }
        .close-btn:hover {
            background: #ff2a6d30;
            border-color: #ff2a6d;
            color: #ff2a6d;
        }
        .header {
            text-align: center;
            margin-bottom: 25px;
        }
        .logo {
            font-size: 56px;
            color: #00d4ff;
            text-shadow: 0 0 30px #00d4ff80;
            animation: logoPulse 2s ease-in-out infinite;
        }
        @keyframes logoPulse {
            0%, 100% { transform: scale(1); }
            50% { transform: scale(1.05); text-shadow: 0 0 40px #00d4ff; }
        }
        .welcome-text {
            font-size: 28px;
            font-weight: bold;
            color: #fff;
            margin: 15px 0 8px 0;
            letter-spacing: 2px;
        }
        .subtitle {
            font-size: 14px;
            color: #6a6a8a;
        }
        .divider {
            height: 2px;
            background: linear-gradient(90deg, transparent, #00d4ff60, #bd00ff60, transparent);
            margin: 20px 0;
        }
        .step-section {
            margin: 20px 0;
        }
        .section-title {
            color: #00d4ff;
            font-size: 16px;
            font-weight: bold;
            margin-bottom: 15px;
            display: flex;
            align-items: center;
            gap: 8px;
        }
        .step-list {
            list-style: none;
        }
        .step-item {
            display: flex;
            align-items: flex-start;
            gap: 15px;
            margin: 15px 0;
            padding: 15px;
            background: linear-gradient(135deg, #0c0c18, #14141f);
            border: 1px solid #1a1a2a;
            border-radius: 10px;
            transition: all 0.3s;
        }
        .step-item:hover {
            border-color: #00d4ff40;
            box-shadow: 0 0 15px #00d4ff20;
        }
        .step-number {
            width: 32px;
            height: 32px;
            background: linear-gradient(135deg, #00d4ff30, #bd00ff20);
            border: 1px solid #00d4ff50;
            border-radius: 50%;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 14px;
            font-weight: bold;
            color: #00d4ff;
            flex-shrink: 0;
        }
        .step-content {
            flex: 1;
        }
        .step-title {
            font-size: 15px;
            font-weight: 600;
            color: #e0e0ff;
            margin-bottom: 5px;
        }
        .step-desc {
            font-size: 13px;
            color: #8080a0;
            line-height: 1.6;
        }
        .hotkey-section {
            background: linear-gradient(135deg, #0c0c18, #14141f);
            border: 1px solid #bd00ff30;
            border-radius: 10px;
            padding: 20px;
            margin: 20px 0;
        }
        .hotkey-title {
            color: #bd00ff;
            font-size: 14px;
            font-weight: bold;
            margin-bottom: 12px;
        }
        .hotkey-item {
            display: flex;
            justify-content: space-between;
            align-items: center;
            padding: 8px 0;
            border-bottom: 1px solid #1a1a2a;
        }
        .hotkey-item:last-child {
            border-bottom: none;
        }
        .hotkey-label {
            font-size: 13px;
            color: #c0c0e0;
        }
        .hotkey-key {
            background: #00d4ff20;
            border: 1px solid #00d4ff40;
            border-radius: 4px;
            padding: 4px 10px;
            font-size: 12px;
            font-family: Consolas, monospace;
            color: #00d4ff;
        }
        .start-btn {
            width: 100%;
            padding: 15px;
            background: linear-gradient(135deg, #00d4ff30, #bd00ff20);
            border: 1px solid #00d4ff60;
            border-radius: 8px;
            color: #fff;
            font-size: 16px;
            font-weight: bold;
            cursor: pointer;
            transition: all 0.3s;
            margin-top: 20px;
        }
        .start-btn:hover {
            background: linear-gradient(135deg, #00d4ff50, #bd00ff40);
            box-shadow: 0 0 20px #00d4ff40;
            transform: translateY(-2px);
        }
        .tip {
            text-align: center;
            font-size: 12px;
            color: #6a6a8a;
            margin-top: 15px;
        }
    </style>
</head>
<body>
    <div class='container'>
        <button class='close-btn' onclick='window.chrome.webview.postMessage(""close"")'>✕</button>

        <div class='header'>
            <div class='logo'>◈</div>
            <div class='welcome-text'>欢迎使用 飞跃侠·CodeBridge</div>
            <div class='subtitle'>AI 编程助手多标签终端管理器</div>
        </div>

        <div class='divider'></div>

        <div class='step-section'>
            <div class='section-title'>🚀 快速入门</div>
            <ul class='step-list'>
                <li class='step-item'>
                    <div class='step-number'>1</div>
                    <div class='step-content'>
                        <div class='step-title'>选择工作目录</div>
                        <div class='step-desc'>在工具栏输入或浏览选择您的项目目录，这将作为终端的工作路径。</div>
                    </div>
                </li>
                <li class='step-item'>
                    <div class='step-number'>2</div>
                    <div class='step-content'>
                        <div class='step-title'>创建新标签</div>
                        <div class='step-desc'>点击 [+ NEW] 按钮创建新的终端标签。勾选 [Auto] 可自动运行 Claude Code。</div>
                    </div>
                </li>
                <li class='step-item'>
                    <div class='step-number'>3</div>
                    <div class='step-content'>
                        <div class='step-title'>管理多个会话</div>
                        <div class='step-desc'>您可以同时打开多个终端标签，每个标签独立运行。双击标签可查看详情。</div>
                    </div>
                </li>
                <li class='step-item'>
                    <div class='step-number'>4</div>
                    <div class='step-content'>
                        <div class='step-title'>会话保存与恢复</div>
                        <div class='step-desc'>关闭程序时会话自动保存，下次启动时自动恢复。勾选 [-c] 可继续上次对话。</div>
                    </div>
                </li>
            </ul>
        </div>

        <div class='hotkey-section'>
            <div class='hotkey-title'>⌨️ 快捷键</div>
            <div class='hotkey-item'>
                <span class='hotkey-label'>悬浮状态栏</span>
                <span class='hotkey-key'>Ctrl + Shift + Space</span>
            </div>
            <div class='hotkey-item'>
                <span class='hotkey-label'>关闭当前标签</span>
                <span class='hotkey-key'>点击标签上的 ✕</span>
            </div>
            <div class='hotkey-item'>
                <span class='hotkey-label'>查看历史记录</span>
                <span class='hotkey-key'>点击 ☰ 按钮</span>
            </div>
        </div>

        <button class='start-btn' onclick='window.chrome.webview.postMessage(""close"")'>
            开始使用 →
        </button>

        <div class='tip'>
            💡 提示：您可以随时通过菜单中的【帮助指南】再次查看此引导
        </div>
    </div>
</body>
</html>";
        }

        private void ShowAboutDialog()
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
        private void ShowCyberDialog(string title, int width, int height, string html, Action? onClose = null)
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

            // 对话框关闭后执行回调
            onClose?.Invoke();
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

        #region Shell 类型切换

        /// <summary>
        /// 切换到 PowerShell
        /// </summary>
        private void ShellPowerShell_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ShellType = "powershell";
            }
        }

        /// <summary>
        /// 切换到 CMD
        /// </summary>
        private void ShellCmd_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ShellType = "cmd";
            }
        }

        #endregion
    }
}
