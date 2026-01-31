using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using CodeBridge.ViewModels;

namespace CodeBridge.Views;

/// <summary>
/// 悬浮状态栏 - 灵动岛风格
/// </summary>
public partial class FloatingStatusBar : Window
{
    private readonly ObservableCollection<TerminalTabViewModel> _allTabs;
    private readonly Action<TerminalTabViewModel> _onTabSelected;
    private bool _isClosing = false;
    private System.Windows.Threading.DispatcherTimer? _refreshTimer;

    /// <summary>
    /// 筛选后的标签列表
    /// </summary>
    public ObservableCollection<TerminalTabViewModel> FilteredTabs { get; } = new();

    public FloatingStatusBar(
        ObservableCollection<TerminalTabViewModel> tabs,
        Action<TerminalTabViewModel> onTabSelected)
    {
        InitializeComponent();

        _allTabs = tabs;
        _onTabSelected = onTabSelected;

        // 绑定数据
        TabItemsControl.ItemsSource = FilteredTabs;

        // 初始化位置（屏幕顶部中央，隐藏状态）
        PositionWindow();

        // 初始化定时刷新器（每秒刷新一次状态）
        _refreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += (s, e) => RefreshTabs();

        // 监听 ESC 键关闭
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HideWithAnimation();
            }
        };

        // 初始加载
        RefreshTabs();
    }

    /// <summary>
    /// 定位窗口到屏幕顶部中央
    /// </summary>
    private void PositionWindow()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (screenWidth - Width) / 2;
        Top = -100; // 初始隐藏在屏幕外
    }

    /// <summary>
    /// 刷新标签列表
    /// </summary>
    public void RefreshTabs()
    {
        FilteredTabs.Clear();

        bool filterWorking = FilterWorkingButton.IsChecked == true;
        bool filterIdle = FilterIdleButton.IsChecked == true;

        foreach (var tab in _allTabs)
        {
            // 如果没有任何筛选，显示所有
            if (!filterWorking && !filterIdle)
            {
                FilteredTabs.Add(tab);
            }
            // 筛选工作中
            else if (filterWorking && tab.IsTaskRunning)
            {
                FilteredTabs.Add(tab);
            }
            // 筛选待机中
            else if (filterIdle && !tab.IsTaskRunning)
            {
                FilteredTabs.Add(tab);
            }
        }

        // 更新空状态显示
        EmptyState.Visibility = FilteredTabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TabItemsControl.Visibility = FilteredTabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 动态调整高度
        UpdateWindowHeight();
    }

    /// <summary>
    /// 动态调整窗口高度
    /// </summary>
    private void UpdateWindowHeight()
    {
        // 头部 44 + 分隔线 1 + 边距 20 + 外边距 20
        const double headerHeight = 85;
        // 每个标签项高度（紧凑版，含边距）
        const double itemHeight = 36;
        // 双列布局，每行 2 个
        const int columnsCount = 2;
        // 最大显示行数
        const int maxVisibleRows = 10;
        // 空状态高度
        const double emptyStateHeight = 100;

        double contentHeight;
        if (FilteredTabs.Count == 0)
        {
            contentHeight = emptyStateHeight;
        }
        else
        {
            // 计算行数（向上取整）
            int rowCount = (int)Math.Ceiling((double)FilteredTabs.Count / columnsCount);
            int visibleRows = Math.Min(rowCount, maxVisibleRows);
            contentHeight = visibleRows * itemHeight;
        }

        Height = headerHeight + contentHeight;
    }

    /// <summary>
    /// 显示动画
    /// </summary>
    public void ShowWithAnimation()
    {
        _isClosing = false;

        // 刷新数据
        RefreshTabs();

        // 启动定时刷新
        _refreshTimer?.Start();

        // 确保窗口可见
        Show();
        Activate();
        Focus();

        // 重置位置
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (screenWidth - Width) / 2;

        // 滑入动画
        var slideIn = new DoubleAnimation
        {
            From = -100,
            To = 20,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        // 淡入动画
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200)
        };

        BeginAnimation(TopProperty, slideIn);
        BeginAnimation(OpacityProperty, fadeIn);
    }

    /// <summary>
    /// 隐藏动画
    /// </summary>
    public void HideWithAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;

        // 停止定时刷新
        _refreshTimer?.Stop();

        // 滑出动画
        var slideOut = new DoubleAnimation
        {
            From = Top,
            To = -100,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        // 淡出动画
        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(150)
        };

        slideOut.Completed += (s, e) =>
        {
            Hide();
            _isClosing = false;
        };

        BeginAnimation(TopProperty, slideOut);
        BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>
    /// 关闭按钮点击
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideWithAnimation();
    }

    /// <summary>
    /// 筛选按钮状态变化
    /// </summary>
    private void FilterButton_Changed(object sender, RoutedEventArgs e)
    {
        // 互斥逻辑：只能选中一个筛选条件
        if (sender is ToggleButton clickedButton && clickedButton.IsChecked == true)
        {
            if (clickedButton == FilterWorkingButton)
            {
                FilterIdleButton.IsChecked = false;
            }
            else if (clickedButton == FilterIdleButton)
            {
                FilterWorkingButton.IsChecked = false;
            }
        }

        RefreshTabs();
    }

    /// <summary>
    /// 点击标签项
    /// </summary>
    private void TabItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button &&
            button.Tag is TerminalTabViewModel tab)
        {
            // 隐藏悬浮栏
            HideWithAnimation();

            // 延迟执行跳转，确保动画完成
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _onTabSelected?.Invoke(tab);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
