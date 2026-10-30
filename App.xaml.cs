using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MiLauncher
{
    public partial class App : Application
    {
        /// <summary>单实例互斥体。 held 住直到进程退出，防止双开两个窗口互相覆盖配置。</summary>
        private Mutex _instanceMutex;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_RESTORE = 9;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 单实例判断放在最前面。第二个实例必须立刻结束——
            // App.xaml 里配了 StartupUri，等到 OnStartup 返回后 WPF 还会去建主窗口。
            bool createdNew;
            _instanceMutex = new Mutex(true, @"Local\MiLauncher.SingleInstance.7f3d", out createdNew);
            if (!createdNew)
            {
                // 已经有一个实例在跑：把它的窗口拉到前面，自己直接退出
                ActivateExistingInstance();
                Environment.Exit(0);
                return;
            }

            base.OnStartup(e);

            // 外部游戏清单要在主窗口（StartupUri）创建之前读进来
            MiLauncher.Services.GameCatalog.LoadUserCatalog();

            // 界面上的异常如果不接住，程序会直接消失，什么都看不到。
            // 这里统一弹窗并记录到 %APPDATA%\MiLauncher\error.log，方便排查。
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_instanceMutex != null)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch
                {
                    // 不是持有者（比如被系统回收）就忽略
                }

                _instanceMutex.Dispose();
                _instanceMutex = null;
            }

            base.OnExit(e);
        }

        private static void ActivateExistingInstance()
        {
            try
            {
                Process current = Process.GetCurrentProcess();
                foreach (Process process in Process.GetProcessesByName(current.ProcessName))
                {
                    if (process.Id == current.Id)
                    {
                        continue;
                    }

                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(process.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(process.MainWindowHandle);
                    }

                    process.Dispose();
                }
            }
            catch
            {
                // 拉不起来就算了，反正新实例已经决定退出
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteLog(e.Exception);
            MessageBox.Show(
                "程序遇到了未处理的错误：\n\n" + e.Exception.Message +
                "\n\n详细信息已写入：\n" + LogPath,
                "米哈游游戏中心", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            if (exception != null)
            {
                WriteLog(exception);
            }
        }

        private static string LogPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MiLauncher",
                    "error.log");
            }
        }

        private static void WriteLog(Exception exception)
        {
            if (exception == null)
            {
                return;
            }

            try
            {
                string folder = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.AppendAllText(LogPath,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " +
                    exception + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
                // 日志写不进去就算了，不能在错误处理里再抛错
            }
        }
    }
}
