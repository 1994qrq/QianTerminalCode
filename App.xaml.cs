using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MyAiHelper.Services;

namespace MyAiHelper
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// IPC 服务实例（全局单例）
        /// </summary>
        public static IpcService? IpcService { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 启动 IPC 监听服务
            IpcService = new IpcService();
            IpcService.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 停止 IPC 服务
            IpcService?.Stop();
            IpcService?.Dispose();

            base.OnExit(e);
        }
    }
}
