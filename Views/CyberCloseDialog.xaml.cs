using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CodeBridge.Views
{
    /// <summary>
    /// 关闭操作类型
    /// </summary>
    public enum CloseAction
    {
        Cancel,
        MinimizeToTray,
        Exit
    }

    /// <summary>
    /// 赛博朋克风格的关闭选项对话框
    /// </summary>
    public partial class CyberCloseDialog : Window
    {
        public CloseAction Result { get; private set; } = CloseAction.Cancel;

        public CyberCloseDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 入场动画
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            BeginAnimation(OpacityProperty, fadeIn);
        }

        private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
        {
            Result = CloseAction.MinimizeToTray;
            CloseWithAnimation();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Result = CloseAction.Exit;
            CloseWithAnimation();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = CloseAction.Cancel;
            CloseWithAnimation();
        }

        private void CloseWithAnimation()
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (s, e) => DialogResult = true;
            BeginAnimation(OpacityProperty, fadeOut);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Result = CloseAction.Cancel;
                CloseWithAnimation();
            }
        }

        private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender)
            {
                Result = CloseAction.Cancel;
                CloseWithAnimation();
            }
        }

        /// <summary>
        /// 显示对话框并返回用户选择
        /// </summary>
        public static CloseAction Show(Window owner)
        {
            var dialog = new CyberCloseDialog
            {
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.ShowDialog();
            return dialog.Result;
        }
    }
}
