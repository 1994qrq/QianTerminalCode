using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MyAiHelper.Models;
using MyAiHelper.Services;
using MyAiHelper.Views;

namespace MyAiHelper.ViewModels;

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
        }
        else
        {
            _tabNote = string.Empty;
            OnPropertyChanged(nameof(TabNote));
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

        LoadSettings();
        FilterHistory();
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
            SelectedTab = tab;
        }

        // 恢复并激活主窗口
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
        _startFullScreen = prefs.StartFullScreen;
        _startMaximized = prefs.StartMaximized;
        _minimizeToTrayOnClose = prefs.MinimizeToTrayOnClose;
        _enableDesktopNotifications = prefs.EnableDesktopNotifications;

        foreach (var historyItem in settings.History)
        {
            History.Add(historyItem);
        }

        // 恢复之前打开的标签页
        foreach (var tabConfig in settings.Tabs)
        {
            // 恢复的标签页自动启用 -c（继续会话）
            tabConfig.ContinueSession = true;
            var tabVm = new TerminalTabViewModel(tabConfig, _terminalService);
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

    [RelayCommand]
    private void AddTab()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath)) return;

        var config = new TabConfig
        {
            Name = Path.GetFileName(CurrentPath) ?? "Terminal",
            Note = TabNote,  // 使用用户输入的备注
            WorkingDirectory = CurrentPath,
            AutoRunClaude = AutoRunClaude,
            ContinueSession = ContinueSession
        };

        var tabVm = new TerminalTabViewModel(config, _terminalService);
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
                StartFullScreen = StartFullScreen,
                StartMaximized = StartMaximized,
                MinimizeToTrayOnClose = MinimizeToTrayOnClose,
                EnableDesktopNotifications = EnableDesktopNotifications
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
