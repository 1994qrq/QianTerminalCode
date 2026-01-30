using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using CodeBridge.Models;
using CodeBridge.Services;
using CodeBridge.Views;

namespace CodeBridge.ViewModels;

/// <summary>
/// 主窗口 ViewModel - 更新版本
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly TerminalService _terminalService = new();
    private readonly ConfigService _configService = new();
    private readonly ImportExportService _importExportService = new();
    private readonly NotificationService _notificationService = new();

    // 活跃的悬浮通知窗口列表
    private readonly List<NotificationPopupWindow> _activePopups = new();
    private const int MaxActivePopups = 5;

    [ObservableProperty]
    private ObservableCollection<TerminalTabViewModel> _terminalTabs = new();

    [ObservableProperty]
    private TerminalTabViewModel? _selectedTab;

    [ObservableProperty]
    private string _windowTitle = "飞跃侠·CodeBridge";

    /// <summary>
    /// 通知列表（供通知中心绑定）
    /// </summary>
    public ObservableCollection<NotificationItem> Notifications => _notificationService.Notifications;

    [ObservableProperty]
    private int _unreadNotificationCount;

    [ObservableProperty]
    private bool _isNotificationCenterOpen;

    partial void OnSelectedTabChanged(TerminalTabViewModel? value)
    {
        // 切换标签时，同步显示当前标签的备注
        if (value?.Config != null)
        {
            _tabNote = value.Config.Note ?? string.Empty;
            OnPropertyChanged(nameof(TabNote));

            // 更新窗口标题
            WindowTitle = $"飞跃侠·CodeBridge - {value.DisplayName}";
        }
        else
        {
            _tabNote = string.Empty;
            OnPropertyChanged(nameof(TabNote));

            // 无选中标签时恢复默认标题
            WindowTitle = "飞跃侠·CodeBridge";
        }
    }

    [ObservableProperty]
    private ObservableCollection<TabConfig> _history = new();

    [ObservableProperty]
    private ObservableCollection<TabConfig> _filteredHistory = new();

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private bool _autoRunClaude;

    [ObservableProperty]
    private bool _continueSession;

    [ObservableProperty]
    private string _tabNote = string.Empty;

    partial void OnTabNoteChanged(string value)
    {
        // 修改备注时，同步更新到当前选中的标签
        if (SelectedTab?.Config != null)
        {
            SelectedTab.Config.Note = value;
            SelectedTab.NotifyDisplayNameChanged();
        }
    }

    [ObservableProperty]
    private bool _isHistoryOpen;

    [ObservableProperty]
    private bool _isSettingsOpen;

    #region 用户偏好设置属性

    [ObservableProperty]
    private bool _hasCompletedOnboarding;

    /// <summary>
    /// 标记引导已完成并保存设置
    /// </summary>
    public void MarkOnboardingCompleted()
    {
        HasCompletedOnboarding = true;
        SaveSettings();
    }

    [ObservableProperty]
    private bool _startFullScreen = true;

    partial void OnStartFullScreenChanged(bool value)
    {
        SaveSettings();
        ApplyWindowMode();
    }

    [ObservableProperty]
    private bool _startMaximized = true;

    partial void OnStartMaximizedChanged(bool value)
    {
        SaveSettings();
    }

    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        SaveSettings();
    }

    [ObservableProperty]
    private bool _enableDesktopNotifications = true;

    partial void OnEnableDesktopNotificationsChanged(bool value)
    {
        SaveSettings();
    }

    [ObservableProperty]
    private string _shellType = "powershell";

    partial void OnShellTypeChanged(string value)
    {
        SaveSettings();
    }

    #endregion

    #region Hook 配置属性

    private readonly HookConfigService _hookConfigService = new();

    [ObservableProperty]
    private bool _isHookConfigured;

    [ObservableProperty]
    private string _hookStatusText = "检测中...";

    [ObservableProperty]
    private bool _isConfiguringHook;

    /// <summary>
    /// 检查 Hook 配置状态
    /// </summary>
    public void CheckHookStatus()
    {
        try
        {
            var status = _hookConfigService.CheckHookStatus();
            IsHookConfigured = status.IsFullyConfigured;
            HookStatusText = status.Message;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HookConfig] 检查状态失败: {ex.Message}");
            IsHookConfigured = false;
            HookStatusText = "检测失败，请重试";
        }
    }

    /// <summary>
    /// 一键配置 Hook
    /// </summary>
    [RelayCommand]
    private async Task ConfigureHookAsync()
    {
        if (IsConfiguringHook) return;

        IsConfiguringHook = true;
        try
        {
            var (success, message) = await _hookConfigService.ConfigureHookAsync();
            HookStatusText = message;

            if (success)
            {
                IsHookConfigured = true;
                _notificationService.Add(new NotificationItem
                {
                    Title = "Hook 配置成功",
                    Message = "任务完成通知已启用",
                    Kind = NotificationKind.Success,
                    Source = "System"
                });
            }
        }
        finally
        {
            IsConfiguringHook = false;
        }
    }

    #endregion

    #region 远程控制属性

    [ObservableProperty]
    private bool _remoteControlEnabled;

    [ObservableProperty]
    private int _remoteControlPort = 8765;

    [ObservableProperty]
    private string _remoteLocalUrl = string.Empty;

    [ObservableProperty]
    private string _remotePublicUrl = string.Empty;

    [ObservableProperty]
    private string _remoteAccessToken = string.Empty;

    [ObservableProperty]
    private string _tokenTimeRemaining = string.Empty;

    private System.Windows.Threading.DispatcherTimer? _tokenCountdownTimer;

    [ObservableProperty]
    private int _remoteConnectionCount;

    [ObservableProperty]
    private bool _isTunnelRunning;

    [ObservableProperty]
    private bool _isCloudflaredInstalled;

    [ObservableProperty]
    private string _tunnelStatusText = "未安装";

    [ObservableProperty]
    private bool _isInstallingCloudflared;

    partial void OnRemoteControlEnabledChanged(bool value)
    {
        if (value)
        {
            _ = StartRemoteControlAsync();
        }
        else
        {
            StopRemoteControl();
        }
        SaveSettings();
    }

    partial void OnRemoteControlPortChanged(int value)
    {
        SaveSettings();
    }

    #endregion

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                FilterHistory();
            }
        }
    }

    public MainWindowViewModel()
    {
        // 订阅通知服务事件
        _notificationService.NotificationAdded += OnNotificationAdded;
        _notificationService.UnreadCountChanged += count => UnreadNotificationCount = count;

        // 订阅 IPC 服务事件（来自 Claude Hooks）
        if (App.IpcService != null)
        {
            App.IpcService.MessageReceived += OnHookMessageReceived;
        }

        // 初始化远程控制服务
        InitializeRemoteControlService();

        // 检查 Hook 配置状态
        CheckHookStatus();

        LoadSettings();
        FilterHistory();
    }

    /// <summary>
    /// 初始化远程控制服务
    /// </summary>
    private void InitializeRemoteControlService()
    {
        var remoteService = App.RemoteControlService;
        if (remoteService == null) return;

        // 设置获取标签列表委托
        remoteService.GetTabsFunc = () => TerminalTabs.Select(t => (t.Config.Id, t.DisplayName));

        // 设置发送输入委托
        remoteService.SendInputFunc = (tabId, input) =>
        {
            _terminalService.SendInput(tabId, input);
        };

        // 设置调整大小委托
        remoteService.ResizeFunc = (tabId, cols, rows) =>
        {
            _terminalService.Resize(tabId, cols, rows);
        };

        // 订阅事件
        remoteService.ConnectionCountChanged += count =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => RemoteConnectionCount = count);
        };

        remoteService.TunnelStatusChanged += status =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsTunnelRunning = status == TunnelStatus.Running;
                TunnelStatusText = status switch
                {
                    TunnelStatus.Stopped => "未启动",
                    TunnelStatus.Starting => "启动中...",
                    TunnelStatus.Running => "运行中",
                    TunnelStatus.Error => "错误",
                    TunnelStatus.NotInstalled => "未安装",
                    _ => "未知"
                };
            });
        };

        remoteService.PublicUrlObtained += url =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => RemotePublicUrl = url);
        };

        remoteService.ErrorOccurred += msg =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _notificationService.Add(new NotificationItem
                {
                    Title = "远程控制",
                    Message = msg,
                    Kind = NotificationKind.Warning,
                    Source = "RemoteControl"
                });
            });
        };

        // 订阅初始化标签请求事件（移动端请求初始化未打开的终端）
        remoteService.InitTabRequested += async tabId =>
        {
            return await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                // 查找对应的标签
                var tab = TerminalTabs.FirstOrDefault(t => t.Config.Id == tabId);
                if (tab == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[RemoteInit] 未找到标签: {tabId}");
                    return (Success: false, AlreadyReady: false);
                }

                // 检查终端是否已经初始化（WebView 已创建）
                if (tab.WebView?.CoreWebView2 != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[RemoteInit] 标签已初始化: {tabId}");
                    return (Success: true, AlreadyReady: true);  // 已就绪，跳过状态通知
                }

                // 终端未初始化，选中该标签触发初始化
                System.Diagnostics.Debug.WriteLine($"[RemoteInit] 正在初始化标签: {tabId}");
                SelectedTab = tab;

                // 等待 WebView 初始化完成（最多 10 秒）
                for (int i = 0; i < 100; i++)
                {
                    await Task.Delay(100);
                    if (tab.WebView?.CoreWebView2 != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[RemoteInit] 标签初始化完成: {tabId}");
                        return (Success: true, AlreadyReady: false);  // 新初始化完成
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[RemoteInit] 标签初始化超时: {tabId}");
                return (Success: false, AlreadyReady: false);
            }).Task.Unwrap();
        };

        // 检查 cloudflared 是否安装
        IsCloudflaredInstalled = remoteService.IsCloudflaredInstalled;
        TunnelStatusText = IsCloudflaredInstalled ? "未启动" : "未安装";
    }

    /// <summary>
    /// 处理来自 Claude Hook 的消息
    /// </summary>
    private void OnHookMessageReceived(IpcService.HookMessage message)
    {
        // 查找对应的标签页
        var tab = TerminalTabs.FirstOrDefault(t => t.Config.Id == message.TabId);
        var tabName = tab?.DisplayName ?? Path.GetFileName(message.Cwd) ?? "Claude";

        // 根据严重级别设置通知类型
        var kind = message.Severity?.ToLower() switch
        {
            "warning" => NotificationKind.Warning,
            "error" => NotificationKind.Error,
            "success" => NotificationKind.Success,
            _ => NotificationKind.Info
        };

        // 创建持久通知（Hook 通知始终持久，不自动消失）
        _notificationService.Add(new NotificationItem
        {
            TabId = message.TabId,
            TabName = tabName,
            Title = message.Title,
            Message = message.Message,
            Kind = kind,
            IsPersistent = true, // Hook 通知始终持久
            Source = "ClaudeHooks"
        });
    }

    /// <summary>
    /// 新通知添加时显示悬浮通知
    /// </summary>
    private void OnNotificationAdded(NotificationItem notification)
    {
        // 检查是否启用桌面通知
        if (!EnableDesktopNotifications) return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            // 清理已关闭的弹窗
            _activePopups.RemoveAll(p => !p.IsVisible);

            // 限制最大弹窗数量
            while (_activePopups.Count >= MaxActivePopups)
            {
                var oldest = _activePopups[0];
                oldest.CloseWithAnimation();
                _activePopups.RemoveAt(0);
            }

            // 创建新弹窗
            var popup = new NotificationPopupWindow(notification, OnNotificationClicked);
            popup.SetVerticalOffset(_activePopups.Count);
            popup.Closed += (s, e) => _activePopups.Remove(popup);
            _activePopups.Add(popup);
            popup.Show();
        });
    }

    /// <summary>
    /// 点击通知时跳转到对应标签
    /// </summary>
    private void OnNotificationClicked(NotificationItem notification)
    {
        // 标记为已读
        _notificationService.MarkAsRead(notification.Id);

        // 查找并选中对应标签
        var tab = TerminalTabs.FirstOrDefault(t => t.Config.Id == notification.TabId);

        if (tab != null)
        {
            // 应用内标签，直接跳转
            SelectedTab = tab;
            // 恢复并激活主窗口
            ActivateMainWindow();
        }
        else
        {
            // 外部会话通知，询问用户是否新建标签
            // 只有用户确认导入时才激活主窗口
            if (HandleExternalNotificationClick(notification))
            {
                ActivateMainWindow();
            }
        }
    }

    /// <summary>
    /// 处理外部会话通知点击
    /// </summary>
    /// <returns>用户是否确认导入或跳转</returns>
    private bool HandleExternalNotificationClick(NotificationItem notification)
    {
        // 获取工作目录（从 TabId 或 TabName 推断，Hook 脚本使用 cwd.Path 作为 tabId）
        var workingDirectory = notification.TabId;

        // 检查目录是否有效
        if (string.IsNullOrWhiteSpace(workingDirectory) || !System.IO.Directory.Exists(workingDirectory))
        {
            // 目录无效，不激活窗口
            return false;
        }

        // 检查是否已存在相同路径的标签
        var existingTab = TerminalTabs.FirstOrDefault(t =>
            string.Equals(t.Config.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase));

        if (existingTab != null)
        {
            // 已存在相同路径的标签，直接跳转（用户点击通知的意图就是查看）
            SelectedTab = existingTab;
            return true;
        }

        // 不存在相同路径的标签，弹出导入确认对话框
        var result = CyberConfirmDialog.Show(
            owner: Application.Current?.MainWindow,
            title: "Claude 完成任务",
            message: workingDirectory,
            subMessage: "此任务在命令行中启动，不在程序内。\n是否导入程序统一管理？",
            confirmText: "导入程序",
            cancelText: "不用了"
        );

        if (result)
        {
            // 用户确认，创建新标签并接管会话
            var config = new TabConfig
            {
                Name = System.IO.Path.GetFileName(workingDirectory) ?? "External",
                Note = "从外部通知接管的会话",
                WorkingDirectory = workingDirectory,
                AutoRunClaude = true,      // 自动运行
                ContinueSession = true     // 继续会话模式
            };

            var tabVm = new TerminalTabViewModel(config, _terminalService, ShellType);
            SubscribeTabEvents(tabVm);
            TerminalTabs.Add(tabVm);
            SelectedTab = tabVm;

            // 添加到历史记录
            if (!History.Any(h => h.WorkingDirectory == config.WorkingDirectory))
            {
                History.Add(config);
                FilterHistory();
            }

            SaveSettings();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 激活主窗口
    /// </summary>
    private void ActivateMainWindow()
    {
        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow != null)
        {
            // 如果窗口最小化，先恢复
            if (mainWindow.WindowState == WindowState.Minimized)
            {
                mainWindow.WindowState = WindowState.Normal;
            }

            // 确保窗口可见
            mainWindow.Show();

            // 激活窗口并置顶
            mainWindow.Activate();
            mainWindow.Topmost = true;
            mainWindow.Topmost = false;
            mainWindow.Focus();
        }
    }

    /// <summary>
    /// 为标签订阅任务完成事件
    /// </summary>
    private void SubscribeTabEvents(TerminalTabViewModel tab)
    {
        tab.TaskCompleted += OnTabTaskCompleted;
    }

    /// <summary>
    /// 取消订阅标签事件
    /// </summary>
    private void UnsubscribeTabEvents(TerminalTabViewModel tab)
    {
        tab.TaskCompleted -= OnTabTaskCompleted;
    }

    /// <summary>
    /// 标签任务完成回调
    /// </summary>
    private void OnTabTaskCompleted(object? sender, TaskCompletionDetector.TaskCompletedEventArgs e)
    {
        if (sender is TerminalTabViewModel tab)
        {
            // 根据触发类型设置不同的消息
            var title = e.IsIdleTimeout ? "需要关注" : "任务完成";
            var message = e.IsIdleTimeout
                ? "Claude 已停止输出，可能需要您的输入"
                : "Claude Code 任务已完成";

            _notificationService.Add(new NotificationItem
            {
                TabId = e.TabId,
                TabName = tab.DisplayName,
                Title = title,
                Message = message,
                Kind = e.IsIdleTimeout ? NotificationKind.Info : NotificationKind.Success,
                Source = "Claude"
            });
        }
    }

    private void LoadSettings()
    {
        var settings = _configService.Load();
        CurrentPath = settings.LastDirectory;

        // 加载用户偏好设置
        var prefs = settings.Preferences;
        _hasCompletedOnboarding = prefs.HasCompletedOnboarding;
        _startFullScreen = prefs.StartFullScreen;
        _startMaximized = prefs.StartMaximized;
        _minimizeToTrayOnClose = prefs.MinimizeToTrayOnClose;
        _enableDesktopNotifications = prefs.EnableDesktopNotifications;
        _shellType = prefs.ShellType;

        // 加载远程控制设置
        var remoteSettings = prefs.RemoteControl;
        _remoteControlPort = remoteSettings.Port;
        _remoteControlEnabled = remoteSettings.IsEnabled;

        // 如果启用了远程控制，启动服务
        if (_remoteControlEnabled)
        {
            _ = StartRemoteControlAsync();
        }

        foreach (var historyItem in settings.History)
        {
            History.Add(historyItem);
        }

        // 恢复之前打开的标签页
        foreach (var tabConfig in settings.Tabs)
        {
            // 恢复的标签页自动启用 -c（继续会话）
            tabConfig.ContinueSession = true;
            var tabVm = new TerminalTabViewModel(tabConfig, _terminalService, _shellType);
            SubscribeTabEvents(tabVm);  // 订阅任务完成事件
            TerminalTabs.Add(tabVm);
        }

        // 选中第一个标签页
        if (TerminalTabs.Count > 0)
        {
            SelectedTab = TerminalTabs[0];
        }

        // 应用窗口模式
        ApplyWindowMode();
    }

    private void FilterHistory()
    {
        FilteredHistory.Clear();

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            // 显示所有历史记录
            foreach (var item in History)
            {
                FilteredHistory.Add(item);
            }
        }
        else
        {
            // 模糊搜索
            var query = SearchText.ToLower();
            foreach (var item in History)
            {
                if (item.Name.ToLower().Contains(query) ||
                    item.WorkingDirectory.ToLower().Contains(query) ||
                    (item.Note?.ToLower().Contains(query) ?? false))
                {
                    FilteredHistory.Add(item);
                }
            }
        }
    }

    /// <summary>
    /// 最大标签数量限制
    /// </summary>
    private const int MaxTabCount = 20;

    /// <summary>
    /// 从对话框创建新标签（供 View 调用）
    /// </summary>
    public void CreateNewTab(string path, string note, bool autoRun, bool continueSession)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // 检查标签数量限制
        if (TerminalTabs.Count >= MaxTabCount)
        {
            CyberConfirmDialog.Show(
                owner: Application.Current?.MainWindow,
                title: "标签数量已达上限",
                message: $"最多只能打开 {MaxTabCount} 个标签。",
                subMessage: "请关闭一些不需要的标签后再试。",
                confirmText: "知道了",
                cancelText: ""
            );
            return;
        }

        var config = new TabConfig
        {
            Name = Path.GetFileName(path) ?? "Terminal",
            Note = note,
            WorkingDirectory = path,
            AutoRunClaude = autoRun,
            ContinueSession = continueSession
        };

        var tabVm = new TerminalTabViewModel(config, _terminalService, ShellType);
        SubscribeTabEvents(tabVm);
        TerminalTabs.Add(tabVm);
        SelectedTab = tabVm;

        // 添加到历史记录
        if (!History.Any(h => h.WorkingDirectory == config.WorkingDirectory))
        {
            History.Add(config);
            FilterHistory();
            SaveSettings();
        }
    }

    [RelayCommand]
    private void AddTab()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath)) return;

        // 检查标签数量限制
        if (TerminalTabs.Count >= MaxTabCount)
        {
            CyberConfirmDialog.Show(
                owner: Application.Current?.MainWindow,
                title: "标签数量已达上限",
                message: $"最多只能打开 {MaxTabCount} 个标签。",
                subMessage: "请关闭一些不需要的标签后再试。",
                confirmText: "知道了",
                cancelText: ""
            );
            return;
        }

        var config = new TabConfig
        {
            Name = Path.GetFileName(CurrentPath) ?? "Terminal",
            Note = TabNote,  // 使用用户输入的备注
            WorkingDirectory = CurrentPath,
            AutoRunClaude = AutoRunClaude,
            ContinueSession = ContinueSession
        };

        var tabVm = new TerminalTabViewModel(config, _terminalService, ShellType);
        SubscribeTabEvents(tabVm);  // 订阅任务完成事件
        TerminalTabs.Add(tabVm);
        SelectedTab = tabVm;

        // 清空备注输入框
        TabNote = string.Empty;

        // 添加到历史记录
        if (!History.Any(h => h.WorkingDirectory == config.WorkingDirectory))
        {
            History.Add(config);
            FilterHistory();
            SaveSettings();
        }
    }

    [RelayCommand]
    private void RestoreFromHistory(TabConfig? config)
    {
        if (config == null) return;

        CurrentPath = config.WorkingDirectory;
        AutoRunClaude = config.AutoRunClaude;
        AddTab();
        IsHistoryOpen = false;
    }

    /// <summary>
    /// 重新载入标签（路径更改后调用）
    /// </summary>
    public void ReloadTab(TerminalTabViewModel tab)
    {
        if (tab == null) return;

        var index = TerminalTabs.IndexOf(tab);
        var wasSelected = SelectedTab == tab;

        // 取消订阅并释放旧标签
        UnsubscribeTabEvents(tab);
        tab.Dispose();

        // 创建新标签（使用更新后的配置）
        var newTab = new TerminalTabViewModel(tab.Config, _terminalService, ShellType);
        SubscribeTabEvents(newTab);

        // 替换标签
        if (index >= 0 && index < TerminalTabs.Count)
        {
            TerminalTabs[index] = newTab;
        }
        else
        {
            TerminalTabs.Add(newTab);
        }

        // 如果之前是选中状态，重新选中
        if (wasSelected)
        {
            SelectedTab = newTab;
        }

        SaveSettings();
    }

    [RelayCommand]
    private void CloseTab(TerminalTabViewModel? tab)
    {
        if (tab == null) return;

        // 显示赛博朋克风格的关闭确认对话框
        var result = CyberConfirmDialog.Show(
            owner: Application.Current?.MainWindow,
            title: "关闭标签",
            message: $"确定要关闭标签 \"{tab.DisplayName}\" 吗？",
            subMessage: "终端会话将被终止。",
            confirmText: "关闭",
            cancelText: "取消"
        );

        if (!result) return;

        UnsubscribeTabEvents(tab);  // 取消订阅事件
        tab.Dispose();
        TerminalTabs.Remove(tab);
    }

    [RelayCommand]
    private void ToggleTabDisabled(TerminalTabViewModel? tab)
    {
        if (tab == null) return;

        if (!tab.IsDisabled)
        {
            // 正在运行的标签需要确认
            if (tab.IsRunning)
            {
                var result = CyberConfirmDialog.Show(
                    owner: Application.Current?.MainWindow,
                    title: "禁用标签",
                    message: $"标签 \"{tab.DisplayName}\" 正在运行",
                    subMessage: "禁用后终端会话将停止，但标签仍会保留。确定禁用吗？",
                    confirmText: "禁用",
                    cancelText: "取消"
                );
                if (!result) return;
            }

            tab.Disable();
        }
        else
        {
            tab.Enable();
        }

        SaveSettings();
    }

    /// <summary>
    /// 显示标签详情的事件（由 View 订阅处理）
    /// </summary>
    public event Action<TerminalTabViewModel>? ShowTabDetailsRequested;

    [RelayCommand]
    private void ShowTabDetails(TerminalTabViewModel? tab)
    {
        if (tab == null) return;

        // 更新最后使用时间
        tab.Config.LastUsedUtc = DateTime.UtcNow;

        // 触发事件，由 View 处理显示对话框
        ShowTabDetailsRequested?.Invoke(tab);
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsHistoryOpen = !IsHistoryOpen;
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择工作目录",
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            CurrentPath = dialog.SelectedPath;
        }
    }

    [RelayCommand]
    private void ImportConfig()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON 文件|*.json",
            Title = "导入配置"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var imported = _importExportService.Import(dialog.FileName);
                var current = _configService.Load();
                var merged = _importExportService.Merge(current, imported);
                _configService.Save(merged);

                // 刷新历史列表
                History.Clear();
                foreach (var item in merged.History)
                {
                    History.Add(item);
                }

                System.Windows.MessageBox.Show("配置导入成功！", "成功", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"导入失败：{ex.Message}", "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void ExportConfig()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON 文件|*.json",
            Title = "导出配置",
            FileName = "myaihelper-config.json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var settings = _configService.Load();
                _importExportService.Export(settings, dialog.FileName);
                System.Windows.MessageBox.Show("配置导出成功！", "成功", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"导出失败：{ex.Message}", "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    private void SaveSettings()
    {
        var settings = new AppSettings
        {
            LastDirectory = CurrentPath,
            History = History.ToList(),
            Tabs = TerminalTabs.Select(t => t.Config).ToList(),
            Preferences = new UserPreferences
            {
                HasCompletedOnboarding = HasCompletedOnboarding,
                StartFullScreen = StartFullScreen,
                StartMaximized = StartMaximized,
                MinimizeToTrayOnClose = MinimizeToTrayOnClose,
                EnableDesktopNotifications = EnableDesktopNotifications,
                ShellType = ShellType,
                RemoteControl = new RemoteControlSettings
                {
                    IsEnabled = RemoteControlEnabled,
                    Port = RemoteControlPort
                }
            }
        };
        _configService.Save(settings);
    }

    /// <summary>
    /// 应用窗口模式（全屏/普通）
    /// </summary>
    private void ApplyWindowMode()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow == null) return;

            if (StartFullScreen)
            {
                // 全屏模式：无边框最大化，覆盖任务栏
                mainWindow.WindowStyle = WindowStyle.None;
                mainWindow.WindowState = WindowState.Maximized;
                mainWindow.Topmost = false;
            }
            else
            {
                // 普通最大化：显示任务栏
                mainWindow.WindowStyle = WindowStyle.None; // 保持无边框风格
                mainWindow.WindowState = WindowState.Normal;

                // 获取工作区域（不包含任务栏）
                var workArea = SystemParameters.WorkArea;
                mainWindow.Left = workArea.Left;
                mainWindow.Top = workArea.Top;
                mainWindow.Width = workArea.Width;
                mainWindow.Height = workArea.Height;
            }
        });
    }

    #region 设置中心命令

    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
    }

    #endregion

    #region 远程控制命令

    /// <summary>
    /// 启动远程控制服务
    /// </summary>
    private async Task StartRemoteControlAsync()
    {
        var remoteService = App.RemoteControlService;
        if (remoteService == null) return;

        try
        {
            await remoteService.StartAsync(RemoteControlPort);
            RemoteLocalUrl = remoteService.LocalUrl;
            RemoteAccessToken = remoteService.AccessToken;

            // 启动倒计时定时器
            StartTokenCountdownTimer();
        }
        catch (Exception ex)
        {
            _notificationService.Add(new NotificationItem
            {
                Title = "远程控制",
                Message = $"启动失败: {ex.Message}",
                Kind = NotificationKind.Error,
                Source = "RemoteControl"
            });
            _remoteControlEnabled = false;
            OnPropertyChanged(nameof(RemoteControlEnabled));
        }
    }

    /// <summary>
    /// 停止远程控制服务
    /// </summary>
    private void StopRemoteControl()
    {
        // 停止倒计时定时器
        StopTokenCountdownTimer();

        App.RemoteControlService?.Stop();
        RemoteLocalUrl = string.Empty;
        RemotePublicUrl = string.Empty;
        RemoteAccessToken = string.Empty;
        TokenTimeRemaining = string.Empty;
        RemoteConnectionCount = 0;
        IsTunnelRunning = false;
        TunnelStatusText = "未启动";
    }

    /// <summary>
    /// 启动 Token 倒计时定时器
    /// </summary>
    private void StartTokenCountdownTimer()
    {
        StopTokenCountdownTimer();

        _tokenCountdownTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _tokenCountdownTimer.Tick += (s, e) => UpdateTokenTimeRemaining();
        _tokenCountdownTimer.Start();

        // 立即更新一次
        UpdateTokenTimeRemaining();
    }

    /// <summary>
    /// 停止 Token 倒计时定时器
    /// </summary>
    private void StopTokenCountdownTimer()
    {
        if (_tokenCountdownTimer != null)
        {
            _tokenCountdownTimer.Stop();
            _tokenCountdownTimer = null;
        }
    }

    /// <summary>
    /// 更新 Token 剩余时间显示
    /// </summary>
    private void UpdateTokenTimeRemaining()
    {
        var remoteService = App.RemoteControlService;
        if (remoteService == null)
        {
            TokenTimeRemaining = string.Empty;
            return;
        }

        var expiry = remoteService.TokenExpiry;
        var remaining = expiry - DateTime.UtcNow;

        if (remaining.TotalSeconds <= 0)
        {
            // Token 已过期，刷新
            RemoteAccessToken = remoteService.RefreshToken();
            remaining = remoteService.TokenExpiry - DateTime.UtcNow;
        }

        // 格式化显示
        if (remaining.TotalHours >= 1)
        {
            TokenTimeRemaining = $"{(int)remaining.TotalHours}小时{remaining.Minutes}分后刷新";
        }
        else if (remaining.TotalMinutes >= 1)
        {
            TokenTimeRemaining = $"{remaining.Minutes}分{remaining.Seconds}秒后刷新";
        }
        else
        {
            TokenTimeRemaining = $"{remaining.Seconds}秒后刷新";
        }
    }

    [RelayCommand]
    private async Task StartTunnel()
    {
        var remoteService = App.RemoteControlService;
        if (remoteService == null) return;

        await remoteService.StartTunnelAsync();
    }

    [RelayCommand]
    private void StopTunnel()
    {
        App.RemoteControlService?.StopTunnel();
        RemotePublicUrl = string.Empty;
    }

    [RelayCommand]
    private void RefreshAccessToken()
    {
        var remoteService = App.RemoteControlService;
        if (remoteService != null)
        {
            RemoteAccessToken = remoteService.RefreshToken();
            UpdateTokenTimeRemaining();
        }
    }

    [RelayCommand]
    private void CopyRemoteUrl()
    {
        var url = !string.IsNullOrEmpty(RemotePublicUrl) ? RemotePublicUrl : RemoteLocalUrl;
        if (string.IsNullOrEmpty(url)) return;

        // 简单复制，不重试，异常静默忽略
        try
        {
            System.Windows.Clipboard.SetText(url);
        }
        catch
        {
            // 忽略所有异常，能复制就复制，不能就手动输入
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallCloudflared))]
    private async Task InstallCloudflaredAsync()
    {
        if (IsInstallingCloudflared) return;

        IsInstallingCloudflared = true;
        InstallCloudflaredCommand.NotifyCanExecuteChanged();

        try
        {
            // 添加通知
            _notificationService.Add(new NotificationItem
            {
                Title = "Cloudflare Tunnel",
                Message = "正在使用 winget 安装 cloudflared...",
                Kind = NotificationKind.Info,
                Source = "RemoteControl"
            });

            var success = await Task.Run(async () =>
            {
                return await CloudflareTunnelService.InstallWithWingetAsync();
            });

            if (success)
            {
                // 重新检查安装状态
                IsCloudflaredInstalled = App.RemoteControlService?.IsCloudflaredInstalled ?? false;
                // 同步更新状态文本
                TunnelStatusText = IsCloudflaredInstalled ? "未启动" : "未安装";

                _notificationService.Add(new NotificationItem
                {
                    Title = "Cloudflare Tunnel",
                    Message = "cloudflared 安装成功！",
                    Kind = NotificationKind.Success,
                    Source = "RemoteControl"
                });
            }
            else
            {
                _notificationService.Add(new NotificationItem
                {
                    Title = "Cloudflare Tunnel",
                    Message = "安装失败，请手动安装或检查 winget 是否可用",
                    Kind = NotificationKind.Warning,
                    Source = "RemoteControl"
                });

                // 打开下载页面作为备选
                App.RemoteControlService?.OpenCloudflaredDownload();
            }
        }
        catch (Exception ex)
        {
            _notificationService.Add(new NotificationItem
            {
                Title = "Cloudflare Tunnel",
                Message = $"安装出错: {ex.Message}",
                Kind = NotificationKind.Error,
                Source = "RemoteControl"
            });
        }
        finally
        {
            IsInstallingCloudflared = false;
            InstallCloudflaredCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanInstallCloudflared() => !IsInstallingCloudflared;

    #endregion

    #region 通知中心命令

    [RelayCommand]
    private void ToggleNotificationCenter()
    {
        IsNotificationCenterOpen = !IsNotificationCenterOpen;
    }

    [RelayCommand]
    private void MarkAllNotificationsRead()
    {
        _notificationService.MarkAllAsRead();
    }

    [RelayCommand]
    private void ClearAllNotifications()
    {
        _notificationService.ClearAll();
    }

    [RelayCommand]
    private void RemoveNotification(NotificationItem? notification)
    {
        if (notification != null)
        {
            _notificationService.Remove(notification.Id);
        }
    }

    [RelayCommand]
    private void OpenNotification(NotificationItem? notification)
    {
        if (notification == null) return;

        OnNotificationClicked(notification);
        IsNotificationCenterOpen = false;
    }

    #endregion

    /// <summary>
    /// 关闭所有标签并保存设置（窗口关闭时调用）
    /// </summary>
    public void Shutdown()
    {
        SaveSettings();

        // 释放所有终端会话
        foreach (var tab in TerminalTabs)
        {
            UnsubscribeTabEvents(tab);
            tab.Dispose();
        }
        TerminalTabs.Clear();

        _terminalService.Dispose();
    }
}
