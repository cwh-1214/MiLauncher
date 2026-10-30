using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MiLauncher.Services
{
    /// <summary>
    /// 开机自动启动。写注册表 Run 项实现，不依赖快捷方式——
    /// 快捷方式要放到开始菜单的启动目录里，卸载/挪 exe 后容易留下残片。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MiLauncher";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    object value = key == null ? null : key.GetValue(ValueName);
                    return value is string path && !string.IsNullOrEmpty(path);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null)
                    {
                        return;
                    }

                    if (enabled)
                    {
                        string exe = ExePath();
                        if (!string.IsNullOrEmpty(exe))
                        {
                            key.SetValue(ValueName, "\"" + exe + "\"");
                        }
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                OfficialAssets.Note("开机自启设置失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 启动时校正注册表里的路径。用户把 exe 挪到别的文件夹之后，Run 项还指着旧路径，
        /// 开机启动会静默失败而且没人知道；这里发现对不上就改成当前路径。
        /// </summary>
        public static void RepairPathIfMoved()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string recorded = key == null ? null : key.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(recorded))
                    {
                        return;
                    }

                    string current = ExePath();
                    if (string.IsNullOrEmpty(current) ||
                        string.Equals(recorded.Trim('"'), current, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    using (RegistryKey write = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (write != null)
                        {
                            write.SetValue(ValueName, "\"" + current + "\"");
                        }
                    }

                    OfficialAssets.Note("开机自启路径已修正：" + current);
                }
            }
            catch
            {
                // 改不了就算了，不影响本次运行
            }
        }

        /// <summary>当前运行的程序路径（发布后用 exe 路径，开发时用 exe 同样有效）。</summary>
        public static string ExePath()        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    string path = process.MainModule == null ? null : process.MainModule.FileName;
                    return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
