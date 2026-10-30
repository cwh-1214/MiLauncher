param([string]$Out = "docs\screenshot.png")

# 必须先声明 DPI 感知：否则 GetWindowRect 返回的是缩放后的虚拟坐标（1280x720），
# 而 CopyFromScreen 按物理像素抓，结果只截到窗口左上角一块。
$dpi = @'
using System;
using System.Runtime.InteropServices;
public class DpiFix
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    public static void Apply()
    {
        // -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
    }
}
'@
if (-not ("DpiFix" -as [type])) { Add-Type -TypeDefinition $dpi }
[DpiFix]::Apply()

Add-Type -AssemblyName System.Drawing

$src = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public class Shot
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_SHOWWINDOW = 0x0040;

    public static RECT Place(IntPtr hwnd)
    {
        ShowWindow(hwnd, 9);              // SW_RESTORE
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_SHOWWINDOW);
        SetForegroundWindow(hwnd);
        RECT r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    public static Bitmap Snap(RECT r)
    {
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }
}
'@

if (-not ("Shot" -as [type])) { Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing }

$p = Get-Process MiLauncher -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { "找不到窗口"; exit 1 }

$rect = [Shot]::Place($p.MainWindowHandle)
Start-Sleep -Milliseconds 1800
$bmp = [Shot]::Snap($rect)
"抓取区域: $($bmp.Width)x$($bmp.Height) @ $($rect.Left),$($rect.Top)"

$w = 1280
$h = [int]([Math]::Round($bmp.Height * $w / $bmp.Width))
$dst = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($dst)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.DrawImage($bmp, 0, 0, $w, $h)
$g.Dispose()

$dir = Split-Path -Parent (Join-Path (Get-Location) $Out)
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$dst.Save((Join-Path (Get-Location) $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose(); $dst.Dispose()

$f = Get-Item (Join-Path (Get-Location) $Out)
"已保存: $($f.FullName)  $($f.Length) 字节  ${w}x${h}"
