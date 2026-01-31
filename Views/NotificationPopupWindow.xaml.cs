using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodeBridge.Models;

namespace CodeBridge.Views;

/// <summary>
/// 科技感悬浮通知窗口
/// </summary>
public partial class NotificationPopupWindow : Window
{
    private readonly NotificationItem _notification;
    private readonly DispatcherTimer _autoCloseTimer;
    private readonly Action<NotificationItem>? _onClicked;
    private bool _isClosing = false;
    private bool _isMouseOver = false;

    /// <summary>
    /// 自动关闭延迟（秒）
    /// </summary>
    public int AutoCloseDelay { get; set; } = 5;

    public NotificationPopupWindow(NotificationItem notification, Action<NotificationItem>? onClicked = null)
    {
        InitializeComponent();

        _notification = notification;
        _onClicked = onClicked;

        // 设置内容
        TitleText.Text = notification.Title;
        TabNameText.Text = $"📂 {notification.TabName}";
        MessageText.Text = notification.Message;
        TimeText.Text = notification.RelativeTime;

        // 定位到屏幕右上角
        PositionWindow();

        // 自动关闭计时器
        _autoCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(AutoCloseDelay)
        };
        _autoCloseTimer.Tick += (s, e) =>
        {
            _autoCloseTimer.Stop();
            CloseWithAnimation();
        };

        Loaded += OnLoaded;
    }

    /// <summary>
    /// 定位窗口到屏幕右上角
    /// </summary>
    private void PositionWindow(int offsetIndex = 0)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Top + 20 + (offsetIndex * (Height + 10));
    }

    /// <summary>
    /// 设置垂直偏移（用于堆叠多个通知）
    /// </summary>
    public void SetVerticalOffset(int index)
    {
        var workArea = SystemParameters.WorkArea;
        Top = workArea.Top + 20 + (index * (Height + 10));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 播放入场动画
        var slideIn = (Storyboard)FindResource("SlideInAnimation");
        BeginStoryboard(slideIn);

        // 播放发光动画
        var glow = (Storyboard)FindResource("GlowAnimation");
        glow.Begin(this);

        // 仅当非持久化通知时，才启动自动关闭计时器
        if (!_notification.IsPersistent)
        {
            _autoCloseTimer.Start();
        }
    }

    /// <summary>
    /// 带动画关闭窗口
    /// </summary>
    public void CloseWithAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;

        _autoCloseTimer.Stop();

        var slideOut = (Storyboard)FindResource("SlideOutAnimation");
        BeginStoryboard(slideOut);
    }

    private void SlideOutAnimation_Completed(object sender, EventArgs e)
    {
        Close();
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点击通知，触发回调并关闭
        _onClicked?.Invoke(_notification);
        CloseWithAnimation();
    }

    private void Border_MouseEnter(object sender, MouseEventArgs e)
    {
        _isMouseOver = true;
        // 鼠标悬停时暂停自动关闭
        _autoCloseTimer.Stop();
    }

    private void Border_MouseLeave(object sender, MouseEventArgs e)
    {
        _isMouseOver = false;
        // 鼠标离开后重新启动计时器（仅非持久通知）
        if (!_isClosing && !_notification.IsPersistent)
        {
            _autoCloseTimer.Start();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithAnimation();
    }
}
