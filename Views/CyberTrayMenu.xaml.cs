using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace CodeBridge.Views
{
    /// <summary>
    /// 赛博朋克风格的托盘右键菜单
    /// </summary>
    public partial class CyberTrayMenu : Window
    {
        public event Action? ShowWindowRequested;
        public event Action? ExitRequested;

        public CyberTrayMenu()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 在指定位置显示菜单
        /// </summary>
        public void ShowAt(int x, int y)
        {
            // 计算位置（确保菜单在屏幕内）
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            // 预估菜单尺寸
            var menuWidth = 200.0;
            var menuHeight = 160.0;

            // 调整位置，确保不超出屏幕
            double left = x;
            double top = y - menuHeight; // 默认显示在鼠标上方

            if (left + menuWidth > screenWidth)
                left = screenWidth - menuWidth - 10;
            if (left < 0)
                left = 10;

            if (top < 0)
                top = y + 10; // 如果上方空间不够，显示在下方

            Left = left;
            Top = top;

            Show();
            Activate();

            // 入场动画
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
            var slideIn = new DoubleAnimation(top + 10, top, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            BeginAnimation(OpacityProperty, fadeIn);
            BeginAnimation(TopProperty, slideIn);
        }

        /// <summary>
        /// 关闭菜单（带动画）
        /// </summary>
        private void CloseMenu()
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
            fadeOut.Completed += (s, e) => Hide();
            BeginAnimation(OpacityProperty, fadeOut);
        }

        private void ShowWindow_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            ShowWindowRequested?.Invoke();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            CloseMenu();
            ExitRequested?.Invoke();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // 失去焦点时关闭菜单
            CloseMenu();
        }
    }
}
