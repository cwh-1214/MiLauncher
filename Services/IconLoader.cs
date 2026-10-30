using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MiLauncher.Services
{
    /// <summary>
    /// 从 exe 里取图标。走 shell32 的 ExtractIconEx，不需要 System.Drawing 依赖。
    /// </summary>
    internal static class IconLoader
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public static ImageSource TryLoad(string exePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    return null;
                }

                var large = new IntPtr[1];
                var small = new IntPtr[1];
                uint count = ExtractIconEx(exePath, 0, large, small, 1);
                if (count == 0)
                {
                    return null;
                }

                IntPtr handle = large[0] != IntPtr.Zero ? large[0] : small[0];
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                ImageSource source = Imaging.CreateBitmapSourceFromHIcon(
                    handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();

                if (large[0] != IntPtr.Zero)
                {
                    DestroyIcon(large[0]);
                }
                if (small[0] != IntPtr.Zero && small[0] != large[0])
                {
                    DestroyIcon(small[0]);
                }

                return source;
            }
            catch
            {
                // 图标取不到就退回占位字，不能因为装饰性内容让整个界面挂掉
                return null;
            }
        }
    }
}
