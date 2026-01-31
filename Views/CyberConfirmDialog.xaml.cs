using System.Windows;
using System.Windows.Input;

namespace CodeBridge.Views
{
    /// <summary>
    /// 赛博朋克风格的确认对话框
    /// </summary>
    public partial class CyberConfirmDialog : Window
    {
        /// <summary>
        /// 对话框结果，true 表示用户点击确认，false 表示取消
        /// </summary>
        public bool Result { get; private set; } = false;

        /// <summary>
        /// 创建对话框实例
        /// </summary>
        public CyberConfirmDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 显示确认对话框（静态方法）
        /// </summary>
        /// <param name="owner">父窗口</param>
        /// <param name="title">对话框标题</param>
        /// <param name="message">主消息内容</param>
        /// <param name="subMessage">副标题/说明文字（可选）</param>
        /// <param name="confirmText">确认按钮文字</param>
        /// <param name="cancelText">取消按钮文字</param>
        /// <returns>true 表示用户点击确认，false 表示取消</returns>
        public static bool Show(
            Window? owner = null,
            string title = "确认",
            string message = "确定要执行此操作吗？",
            string? subMessage = null,
            string confirmText = "确定",
            string cancelText = "取消")
        {
            var dialog = new CyberConfirmDialog();

            // 设置标题
            dialog.TitleText.Text = title;
            dialog.Title = title;

            // 设置主消息
            dialog.MessageText.Text = message;

            // 设置副消息
            if (!string.IsNullOrWhiteSpace(subMessage))
            {
                dialog.SubMessageText.Text = subMessage;
                dialog.SubMessageText.Visibility = Visibility.Visible;
            }
            else
            {
                dialog.SubMessageText.Visibility = Visibility.Collapsed;
            }

            // 设置按钮文字
            dialog.ConfirmButton.Content = confirmText;
            dialog.CancelButton.Content = cancelText;

            // 设置父窗口
            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            // 显示对话框
            dialog.ShowDialog();

            return dialog.Result;
        }

        /// <summary>
        /// 标题栏拖动
        /// </summary>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
            {
                DragMove();
            }
        }

        /// <summary>
        /// 关闭按钮点击
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Result = false;
            Close();
        }

        /// <summary>
        /// 取消按钮点击
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = false;
            Close();
        }

        /// <summary>
        /// 确认按钮点击
        /// </summary>
        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            Result = true;
            Close();
        }
    }
}
