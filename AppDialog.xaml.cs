using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MiLauncher
{
    /// <summary>提示框的语气，决定左上角那个圆标和标题的气质。</summary>
    public enum DialogTone
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// 和主界面同一套视觉的对话框（确认 / 提示）。
    /// 系统 MessageBox 是白底方角，压在深色启动器上很割裂，所以自己画。
    /// </summary>
    public partial class AppDialog : Window
    {
        public AppDialog()
        {
            InitializeComponent();

            // 没有标题栏，按住卡片任意位置都能拖
            MouseLeftButtonDown += delegate
            {
                if (Mouse.LeftButton != MouseButtonState.Pressed)
                {
                    return;
                }

                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // 鼠标已经抬起，忽略
                }
            };
        }

        /// <summary>
        /// 确认框：返回用户是否点了确认。
        /// danger=true 时确认按钮是红色（用于删除、移除这类不可逆操作）。
        /// </summary>
        public static bool Ask(Window owner, string title, string message, string confirmText,
            bool danger, ImageSource icon)
        {
            AppDialog dialog = Create(owner, title, message);

            dialog.ConfirmButton.Content = confirmText;
            dialog.ConfirmButton.Style = (Style)dialog.FindResource(danger ? "DangerButton" : "PrimaryButton");

            if (icon != null)
            {
                dialog.SetIcon(icon);
            }
            else
            {
                dialog.SetGlyph(danger ? DialogTone.Error : DialogTone.Info);
            }

            // 默认焦点给「取消」，误按回车不会触发危险操作
            dialog.Loaded += delegate { dialog.CancelButton.Focus(); };

            return dialog.ShowDialog() == true;
        }

        /// <summary>提示框：只有一个「知道了」，用来报错、说明情况。</summary>
        public static void Tell(Window owner, string title, string message, DialogTone tone, ImageSource icon)
        {
            AppDialog dialog = Create(owner, title, message);

            dialog.CancelButton.Visibility = Visibility.Collapsed;
            dialog.ConfirmButton.Content = "知道了";
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("PrimaryButton");
            dialog.ConfirmButton.IsDefault = true;
            dialog.ConfirmButton.IsCancel = true;   // Esc 也能关

            if (icon != null)
            {
                dialog.SetIcon(icon);
            }
            else
            {
                dialog.SetGlyph(tone);
            }

            dialog.Loaded += delegate { dialog.ConfirmButton.Focus(); };

            dialog.ShowDialog();
        }

        private static AppDialog Create(Window owner, string title, string message)
        {
            var dialog = new AppDialog();
            if (owner != null && owner.IsVisible)
            {
                dialog.Owner = owner;
            }

            dialog.TitleText.Text = title;
            dialog.MessageText.Text = message;
            dialog.IconFrame.Visibility = Visibility.Collapsed;
            dialog.GlyphFrame.Visibility = Visibility.Collapsed;

            return dialog;
        }

        private void SetIcon(ImageSource icon)
        {
            IconImage.ImageSource = icon;
            IconFrame.Visibility = Visibility.Visible;
            GlyphFrame.Visibility = Visibility.Collapsed;
        }

        private void SetGlyph(DialogTone tone)
        {
            IconFrame.Visibility = Visibility.Collapsed;
            GlyphFrame.Visibility = Visibility.Visible;

            switch (tone)
            {
                case DialogTone.Warning:
                    GlyphText.Text = "!";
                    GlyphFrame.Background = Frozen(0x33, 0xE8, 0xB5, 0x59);
                    GlyphText.Foreground = Frozen(0xFF, 0xE8, 0xB5, 0x59);
                    break;

                case DialogTone.Error:
                    GlyphText.Text = "×";
                    GlyphFrame.Background = Frozen(0x33, 0xF0, 0x71, 0x6B);
                    GlyphText.Foreground = Frozen(0xFF, 0xF0, 0x71, 0x6B);
                    break;

                default:
                    GlyphText.Text = "i";
                    GlyphFrame.Background = Frozen(0x33, 0xD3, 0xBC, 0x8E);
                    GlyphText.Foreground = Frozen(0xFF, 0xD3, 0xBC, 0x8E);
                    break;
            }
        }

        private static Brush Frozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
