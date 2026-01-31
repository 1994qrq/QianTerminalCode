using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CodeBridge.Services;

namespace CodeBridge
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private const string MutexName = "CodeBridge_SingleInstance_Mutex";

        /// <summary>
        /// IPC 服务实例（全局单例）
        /// </summary>
        public static IpcService? IpcService { get; private set; }

        /// <summary>
        /// 远程控制服务实例（全局单例）
        /// </summary>
        public static RemoteControlService? RemoteControlService { get; private set; }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 单实例检测
            _mutex = new Mutex(true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                // 已有实例运行，激活已有窗口
                ActivateExistingInstance();
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // 启动 IPC 监听服务
            IpcService = new IpcService();
            IpcService.Start();

            // 创建远程控制服务（由 ViewModel 控制启动）
            RemoteControlService = new RemoteControlService();
        }

        /// <summary>
        /// 激活已有实例的窗口
        /// </summary>
        private void ActivateExistingInstance()
        {
            try
            {
                var currentProcess = Process.GetCurrentProcess();
                var processes = Process.GetProcessesByName(currentProcess.ProcessName);

                foreach (var process in processes)
                {
                    if (process.Id != currentProcess.Id && process.MainWindowHandle != IntPtr.Zero)
                    {
                        // 如果窗口最小化，先恢复
                        if (IsIconic(process.MainWindowHandle))
                        {
                            ShowWindow(process.MainWindowHandle, SW_RESTORE);
                        }

                        // 激活窗口
                        SetForegroundWindow(process.MainWindowHandle);
                        break;
                    }
                }
            }
            catch
            {
                // 忽略激活失败
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 停止远程控制服务
            RemoteControlService?.Dispose();

            // 停止 IPC 服务
            IpcService?.Stop();
            IpcService?.Dispose();

            // 释放 Mutex
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();

            base.OnExit(e);
        }
    }
}
