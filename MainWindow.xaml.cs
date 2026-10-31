using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using MiLauncher.Models;
using MiLauncher.Services;

namespace MiLauncher
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<GameInfo> _games = new ObservableCollection<GameInfo>();
        private readonly DispatcherTimer _processTimer;
        private readonly DispatcherTimer _carouselTimer;
        private readonly LauncherConfig _config;

        /// <summary>正在查询线上版本的游戏（用 GameKey 标识），避免重复请求。</summary>
        private readonly HashSet<string> _checking = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>最近一次版本检查是否失败过（用于详情页留一句"版本检查失败"的灰字）。</summary>
        private bool _versionCheckFailed;

        /// <summary>本地有背景图文件但解不开（最常见原因是系统没装 WebP 图像扩展）。</summary>
        private bool _backdropDecodeFailed;

        /// <summary>连不上素材接口，而且本地还没有任何背景图（真·首次启动 + 断网）。</summary>
        private bool _assetsUnavailable;

        // 状态刷新是每 3 秒一次的，画刷用冻结的静态实例，不要每次重建
        private static readonly Brush MutedBrush = Frozen(0xA2, 0xA2, 0xB6);
        private static readonly Brush WarnBrush = Frozen(0xF0, 0xC4, 0x6B);
        private static readonly Brush OkBrush = Frozen(0x8A, 0xD2, 0xA8);

        // 状态胶囊里那个小圆点 / 可更新时的金色按钮
        private static readonly Brush DotIdleBrush = Frozen(0xC9, 0xC9, 0xDA);
        private static readonly Brush DotOkBrush = Frozen(0x8F, 0xE3, 0xA6);
        private static readonly Brush DotWarnBrush = Frozen(0xF0, 0xC4, 0x6B);
        private static readonly Brush DotErrorBrush = Frozen(0xEF, 0x8F, 0x8F);
        private static readonly Brush UpdateTextBrush = Frozen(0xFB, 0xE2, 0x7A);
        private static readonly Brush UpdateEdgeBrush = FrozenA(0x59, 0xFB, 0xDC, 0x29);
        private static readonly Brush UpdateFillBrush = FrozenA(0x1F, 0xFB, 0xDC, 0x29);
        private static readonly Brush GhostTextBrush = Frozen(0xE2, 0xE2, 0xEE);
        private static readonly Brush GhostEdgeBrush = FrozenA(0x3D, 0xFF, 0xFF, 0xFF);
        private static readonly Brush GhostFillBrush = FrozenA(0x2E, 0x00, 0x00, 0x00);

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush FrozenA(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>状态胶囊的语气：只影响小圆点颜色，文字一律白色。</summary>
        private enum StatusTone
        {
            Idle,
            Ok,
            Warn,
            Error
        }

        /// <summary>
        /// 右上角状态胶囊。传空字符串就整块收起来——"就绪"这种没有信息量的字不该常驻，
        /// 详情页安静下来，运行时/出错时它才跳出来。
        /// </summary>
        private void SetStatus(string text, StatusTone tone)
        {
            if (string.IsNullOrEmpty(text))
            {
                TxtStatus.Text = string.Empty;
                StatusPill.Visibility = Visibility.Collapsed;
                return;
            }

            TxtStatus.Text = text;
            StatusPill.Visibility = Visibility.Visible;

            switch (tone)
            {
                case StatusTone.Ok:
                    StatusDot.Fill = DotOkBrush;
                    break;
                case StatusTone.Warn:
                    StatusDot.Fill = DotWarnBrush;
                    break;
                case StatusTone.Error:
                    StatusDot.Fill = DotErrorBrush;
                    break;
                default:
                    StatusDot.Fill = DotIdleBrush;
                    break;
            }
        }

        private bool _busy;
        private bool _initialCheckDone;

        /// <summary>配置有改动，等 600ms 没有新改动再落盘，避免切游戏时频繁写磁盘。</summary>
        private readonly DispatcherTimer _saveTimer;
        private bool _configDirty;

        public MainWindow()
        {
            InitializeComponent();

            _config = GameStore.Load();
            OfficialAssets.CacheDir = _config.AssetCacheDir;

            // 解码宽度必须在这里定下来：下面第 175 行选中游戏就会触发第一次背景解码，
            // 那时若还是默认的 2560，会白解一张 14.7MB 的图、白写一份旁车。
            ApplyDecodeWidthForWindow();

            // exe 挪过位置或被安全软件清过注册表时，按配置把开机自启补回来
            if (_config.AutoStart)
            {
                if (!AutoStart.IsEnabled())
                {
                    AutoStart.SetEnabled(true);
                }
                else
                {
                    // 已经设过自启：顺手看看注册表里的路径还是不是当前 exe（挪过文件夹就修一下）
                    AutoStart.RepairPathIfMoved();
                }
            }

            RestoreWindowPlacement();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += delegate
            {
                _saveTimer.Stop();
                FlushConfig();
            };

            // 轮播定时器要在选中游戏之前建好：选中会触发 ShowGame → RestartCarousel
            _carouselTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(CarouselSecondsSetting) };
            _carouselTimer.Tick += CarouselTick;

            GameList.ItemsSource = _games;
            GameList.SelectionChanged += GameList_SelectionChanged;

            // 设置面板只能通过右上角关闭按钮关（点外部区域 / Esc 都不关），这里不再挂全局快捷键

            LoadSavedGames();
            RefreshRunningStateAsync();
            RefreshLayout();

            // 这台机器上连一个常见安装目录都没有（比如极简系统），
            // 「自动扫描」按钮点了也只会弹目录框，不如直接不显示
            if (GameScanner.CommonRoots().Count == 0)
            {
                BtnAutoScan.Visibility = Visibility.Collapsed;
            }

            if (_games.Count > 0)
            {
                // 停在上次选中的游戏上；找不到就回到第一个
                GameInfo last = _games.FirstOrDefault(
                    g => string.Equals(g.ExePath, _config.LastGame, StringComparison.OrdinalIgnoreCase));
                GameList.SelectedItem = last ?? _games[0];
            }

            _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _processTimer.Tick += ProcessTimer_Tick;
            _processTimer.Start();

            // 启动后在后台查一次线上版本，界面先出来，版本状态随后补上
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialCheckDone)
            {
                return;
            }
            _initialCheckDone = true;

            // 按窗口实际物理像素决定背景解码宽度：1920 宽的屏就解 1920，
            // 4K/200% 就解到 2560（原图上限）。既不会放大发虚，也不白占内存。
            ApplyDecodeWidthForWindow();


            // 1) 先用本地缓存把背景图铺上。这一步完全不联网，正常几十毫秒，
            //    窗口一出来就是完整画面；断网、接口挂了也不影响。
            await LoadCachedBackdropsAsync();

            // 2) 全新电脑第一次打开、列表还是空的：自动去找一遍常见安装位置，
            //    找到就直接进详情页，找不到就留在空状态并给一句提示（只试一次）。
            bool addedByScan = false;
            if (_games.Count == 0 && !_config.AutoScanDone)
            {
                _config.AutoScanDone = true;
                SaveConfig();
                addedByScan = await AutoScanAsync(false);
            }

            // 3) 版本检查和素材刷新互不依赖，并行去做，别一个等一个。
            //    这两个慢或者失败都不该拖住画面。上一步刚加进来的游戏已经查过了，不重复查。
            //    设置里关掉「启动时检查更新」就不发这次联网请求；手动刷新按钮不受影响。
            if (!addedByScan && _games.Count > 0)
            {
                Task versionTask = _config.CheckUpdateDisabled
                    ? Task.CompletedTask
                    : CheckAllUpdatesAsync();
                Task assetTask = EnsureAssetsAsync();
                await Task.WhenAll(versionTask, assetTask);
            }

            UpdateBackdropNotice();
        }

        /// <summary>
        /// 按窗口物理宽度决定背景解码宽度。官方原图 2560×1440：
        /// 窗口比 2560 还宽也只能用到 2560（源就这么多），比 1280 窄就没必要解那么大。
        /// 以前这里还有个「固定清晰度」的手动选项，已经去掉了——窗口尺寸是固定的，
        /// 跟着窗口算永远是正确值，手动选只会要么发虚要么白占内存。
        /// </summary>
        private void ApplyDecodeWidthForWindow()
        {
            // 刚构造、还没挂到 HwndSource 时 VisualTreeHelper.GetDpi 只会给 96，
            // 这时候问系统要 DPI，否则按 1280 解出来的图在 150% 屏上会发虚
            double dpi = 0;
            if (PresentationSource.FromVisual(this) != null)
            {
                dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            }

            if (dpi <= 0)
            {
                try
                {
                    dpi = GetDpiForSystem() / 96.0;
                }
                catch
                {
                    dpi = 1.0;
                }
            }

            double width = ActualWidth > 0 ? ActualWidth : Width;
            int pixels = (int)Math.Ceiling(width * dpi);

            OfficialAssets.BackgroundDecodeWidth = Math.Max(1280, Math.Min(2560, pixels));

            // 换个分辨率（比如把窗口拖到另一块屏）时，旧宽度的旁车就没用了。
            // 放到后台删，别在 UI 线程摸磁盘。
            int keep = OfficialAssets.BackgroundDecodeWidth;
            Task.Run(() => OfficialAssets.PruneSidecars(keep));
        }

        /// <summary>
        /// 启动时只读当前选中游戏已经缓存好的背景（原神一次 4 张，全读进来太占内存，
        /// 别的游戏切过去时再读）。第一张解出来就先上屏，剩下的在后台补齐。
        /// </summary>
        private async Task LoadCachedBackdropsAsync()
        {
            GameInfo selected = GameList.SelectedItem as GameInfo;
            if (selected == null)
            {
                return;
            }

            await LoadCachedFramesAsync(selected, true);
            RestartCarousel();

            // 顺手在后台给其它游戏的缓存图生成「预解码 BMP」旁车：
            // 之后第一次切过去就是读文件（约 30ms），而不是现场解 webp（100–250ms）。
            _ = Task.Run(() =>
            {
                foreach (GameInfo game in _games)
                {
                    if (ReferenceEquals(game, selected) || string.IsNullOrEmpty(game.Biz))
                    {
                        continue;
                    }

                    foreach (string path in CachedFrames(game.Biz))
                    {
                        OfficialAssets.WarmSidecar(path, OfficialAssets.BackgroundDecodeWidth);
                    }
                }
            });
        }

        /// <summary>缓存目录里这个游戏的背景帧路径（最多 MaxBackgrounds 张）。</summary>
        private static List<string> CachedFrames(string biz)
        {
            var paths = new List<string>();
            if (string.IsNullOrEmpty(biz))
            {
                return paths;
            }

            for (int index = 1; index <= OfficialAssets.MaxBackgrounds; index++)
            {
                string stem = index == 1 ? biz + "-bg" : biz + "-bg" + index;
                string path = OfficialAssets.FindCached(stem);
                if (path != null)
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        /// <summary>把某一帧解码出来（后台线程），成功后写回 game。</summary>
        private async Task<bool> DecodeFrameAsync(GameInfo game, int index)
        {
            if (game.FrameAt(index) != null)
            {
                return true;
            }

            string path = game.PathAt(index);
            if (path == null)
            {
                return false;
            }

            string file = path;
            ImageSource frame = await Task.Run(
                () => OfficialAssets.TryLoadImage(file, OfficialAssets.BackgroundDecodeWidth));

            if (frame == null)
            {
                // 文件在、解不开：几乎都是系统缺 WebP 解码器，界面上要说清楚
                _backdropDecodeFailed = true;
                return false;
            }

            game.SetFrame(index, frame);
            return true;
        }

        /// <summary>
        /// 只保留"当前 + 下一张"两帧解码，其余释放。
        /// 当前帧一解好就通过 onFirstReady 通知调用方上屏，不要等下一张也解完
        /// （那会白白多等一次解码，切游戏就能感觉到慢）。
        /// </summary>
        private async Task PrepareFramesAsync(GameInfo game, Action onFirstReady)
        {
            int count = game.BackgroundCount;
            if (count == 0)
            {
                return;
            }

            int current = game.BackgroundIndex;
            if (await DecodeFrameAsync(game, current) && onFirstReady != null)
            {
                onFirstReady();
            }

            int next = count > 1 ? (current + 1) % count : current;
            if (count > 1)
            {
                await DecodeFrameAsync(game, next);
            }

            game.ReleaseFramesExcept(current, next);
        }

        private Task PrepareFramesAsync(GameInfo game)
        {
            return PrepareFramesAsync(game, null);
        }

        /// <summary>把本地缓存的多张背景读进来（后台线程解码，不卡界面）。</summary>
        private async Task LoadCachedFramesAsync(GameInfo game, bool applyToUi)
        {
            if (game == null || game.HasBackground || string.IsNullOrEmpty(game.Biz))
            {
                return;
            }

            List<string> paths = CachedFrames(game.Biz);
            if (paths.Count == 0)
            {
                return;
            }

            game.SetBackgroundPaths(paths, null);

            if (applyToUi)
            {
                // 第一帧解好立刻上屏，剩下的（下一张）在后台慢慢补
                await PrepareFramesAsync(game, delegate
                {
                    ApplyBackdropOnly(game);
                    RestartCarousel();
                });
            }
            else
            {
                await PrepareFramesAsync(game);
            }
        }

        /// <summary>
        /// 只保留当前这个游戏的背景帧，其它游戏的全部释放。
        /// 每帧解码后要几十 MB，留着三个游戏就是白白多背两三百兆；
        /// 切回去时从磁盘重解一次也就两百毫秒，而画面会一直停在上一张上，不会闪。
        /// </summary>
        private void ReleaseOtherBackdrops(GameInfo keep)
        {
            foreach (GameInfo game in _games)
            {
                if (!ReferenceEquals(game, keep) && game.HasBackground)
                {
                    game.ClearBackgrounds();
                }
            }
        }

        /// <summary>轮播间隔。没配过就用 10 秒。</summary>
        private int CarouselSecondsSetting
        {
            get { return _config != null && _config.CarouselSeconds > 0 ? _config.CarouselSeconds : 10; }
        }

        /// <summary>背景是否做了轮播白名单之外的其它设置判断（配置未提供时默认开启）。</summary>
        private bool CarouselEnabled
        {
            get { return _config == null || !_config.CarouselDisabled; }
        }

        /// <summary>
        /// 缓慢推近效果（Ken Burns）。默认关：实测开着的时候 WPF 每帧都要重采样整张 2560×1440 位图，
        /// 内存从 190MB 涨到 500MB+，对一个启动器不值得。设置里可以打开。
        /// </summary>
        private bool EnableKenBurns
        {
            // 这个效果已经从设置里下掉了：每帧重采样整张背景图，内存从 190MB 涨到 500MB+，
            // 而且开着也必须等下一次换图才看得到。保留代码但恒关闭，旧配置里写过 true 的也不再生效。
            get { return false; }
        }

        private void RestartCarousel()
        {
            if (_carouselTimer == null)
            {
                return;
            }

            _carouselTimer.Stop();

            GameInfo game = GameList.SelectedItem as GameInfo;
            if (game != null && game.BackgroundCount > 1 && CarouselEnabled)
            {
                _carouselTimer.Start();
            }
        }

        private async void CarouselTick(object sender, EventArgs e)
        {
            // 最小化时没必要继续换图和跑动画
            if (WindowState == WindowState.Minimized)
            {
                return;
            }

            GameInfo game = GameList.SelectedItem as GameInfo;
            if (game == null || game.BackgroundCount < 2)
            {
                _carouselTimer.Stop();
                return;
            }

            game.NextBackground();
            await PrepareFramesAsync(game);

            ImageSource frame = game.BackgroundImage;
            if (frame == null)
            {
                // 下一张解不出来就别继续转了（多半是缺 WebP 解码器）
                _carouselTimer.Stop();
                return;
            }

            ShowBackdrop(frame, 620);
        }

        /// <summary>
        /// 详情页底部的那行小字：只在"该有图却没有"的时候出现，
        /// 说清楚是缺解码器还是没联网，别让用户对着纯色背景猜。
        /// </summary>
        private void UpdateBackdropNotice()
        {
            GameInfo selected = GameList.SelectedItem as GameInfo;
            bool noImage = selected != null && !selected.HasBackground;

            if (_backdropDecodeFailed)
            {
                TxtNotice.Text = "背景图无法解码，已退回纯色背景。常见原因是系统缺少「WebP 图像扩展」：" +
                                 "设置 → 应用 → 可选功能 → 添加功能，装好后重开本程序即可。";
                TxtNotice.Visibility = Visibility.Visible;
                return;
            }

            if (_assetsUnavailable && noImage)
            {
                TxtNotice.Text = "还没能下载官方背景图：现在连不上米哈游的素材服务器。" +
                                 "联网后重开本程序就会自动补上（不影响开始游戏）。";
                TxtNotice.Visibility = Visibility.Visible;
                return;
            }

            TxtNotice.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 切到还没加载过背景图的游戏时补一次。缓存里有就立刻铺上，没有就等联网那一步。
        /// </summary>
        private async void LoadCachedBackdropForCurrentAsync()
        {
            GameInfo game = GameList.SelectedItem as GameInfo;
            if (game == null || game.HasBackground)
            {
                return;
            }

            await LoadCachedFramesAsync(game, true);

            if (ReferenceEquals(GameList.SelectedItem, game))
            {
                RestartCarousel();
            }
        }

        /// <summary>
        /// 只重画跟背景相关的部分（背景图 + 游戏名的字号）。
        /// 图是后补上来的，不应该把整页入场动画再播一遍。
        /// </summary>
        private void ApplyBackdropOnly(GameInfo game)
        {
            bool hasBackground = ExpectsBackground(game);
            TxtName.FontSize = hasBackground ? 28 : 40;
            TxtName.FontWeight = hasBackground ? FontWeights.SemiBold : FontWeights.Bold;
            TxtName.Opacity = hasBackground ? 0.94 : 1.0;

            // 切游戏用短淡入：大图解码只要十几毫秒，画面上来得很快，
            // 0.6 秒那种慢淡入反而会让人觉得"点了没反应"。
            ImageSource full = game.BackgroundImage;
            if (full != null)
            {
                ApplyBackdropImage(full, game.BackgroundBlur, 240);
                return;
            }

            // 还在解码：先用这个游戏的糊版顶一下，连糊版都没有就保持上一张（几毫秒后就到）
            if (game.BackgroundBlur != null)
            {
                ApplyBackdropImage(game.BackgroundBlur, null, 120);
            }
        }

        /// <summary>
        /// 这个游戏"应该"有官方背景图吗——不只看已解码的帧，还要看磁盘上有没有缓存。
        /// 否则切到还没解码的游戏时字号会先按"没图"放大、等图到了再缩小，
        /// 整块内容会上下跳一下。
        /// </summary>
        private static bool ExpectsBackground(GameInfo game)
        {
            if (game.HasBackground)
            {
                return true;
            }

            return !string.IsNullOrEmpty(game.Biz) &&
                   OfficialAssets.FindCached(game.Biz + "-bg") != null;
        }

        // ==================== 加载 / 保存 ====================

        /// <summary>从配置里把上次添加的游戏恢复出来，每条的版本、区服都会重新识别。</summary>
        private void LoadSavedGames()
        {
            foreach (StoredGame stored in _config.Games)
            {
                if (string.IsNullOrWhiteSpace(stored.Exe))
                {
                    continue;
                }
                if (_games.Any(g => string.Equals(g.GameKey, stored.Exe, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                GameInfo game = GameScanner.CreateFromExe(stored.Exe, stored.LastPlayed);
                if (game != null)
                {
                    game.UseDx12 = stored.Dx12;
                    _games.Add(game);
                }
            }
        }

        /// <summary>标记配置有改动，稍后统一落盘（切来切去时不必每次都写磁盘）。</summary>
        private void SaveConfig()
        {
            _configDirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>立刻把配置写下去（关窗口、以及离上次改动超过 600ms 时）。</summary>
        private void FlushConfig()
        {
            _saveTimer.Stop();

            if (!_configDirty)
            {
                return;
            }
            _configDirty = false;

            _config.Games = _games
                .Select(g => new StoredGame { Exe = g.ExePath, LastPlayed = g.LastPlayed, Dx12 = g.UseDx12 })
                .ToList();

            string error;
            if (!GameStore.TrySave(_config, out error))
            {
                AppDialog.Tell(this, "保存失败",
                    "无法保存配置：\n" + error + "\n\n本次改动只在这次运行期间有效。",
                    DialogTone.Error, null);
            }
        }

        // ==================== 窗口位置 ====================

        /// <summary>
        /// 恢复上次的窗口位置。尺寸是固定的（1280×720，和官方一致），只记位置。
        /// 位置要夹回虚拟屏幕范围里——换屏、拔掉外接显示器之后，
        /// 旧坐标可能落在没有屏幕的地方，窗口就找不到了。
        /// </summary>
        private void RestoreWindowPlacement()
        {
            if (!_config.WindowLeft.HasValue || !_config.WindowTop.HasValue)
            {
                return;
            }

            double left = _config.WindowLeft.Value;
            double top = _config.WindowTop.Value;

            // 至少留 200×80 在屏幕里，保证还能抓住标题栏拖回来
            double minLeft = SystemParameters.VirtualScreenLeft - Width + 200;
            double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 200;
            double minTop = SystemParameters.VirtualScreenTop - 40;
            double maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;

            left = Math.Max(minLeft, Math.Min(left, maxLeft));
            top = Math.Max(minTop, Math.Min(top, maxTop));

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        private void CaptureWindowPlacement()
        {
            if (WindowState != WindowState.Normal)
            {
                return;
            }

            try
            {
                // 直接量真实窗口矩形：WPF 的 Left/Top 属性不一定跟得上系统移动
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                RECT rect;
                if (!GetWindowRect(hwnd, out rect))
                {
                    return;
                }

                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                _config.WindowLeft = rect.Left / dpi.DpiScaleX;
                _config.WindowTop = rect.Top / dpi.DpiScaleY;
                _configDirty = true;
            }
            catch
            {
                // 记不住位置不算问题，下次还是居中
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            CaptureWindowPlacement();
            FlushConfig();
            base.OnClosing(e);
        }

        // ==================== 添加游戏 ====================

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "选择游戏安装目录",
                Multiselect = false
            };

            // 「添加游戏」的起始目录：设置里的安装位置优先，其次上次选过的目录，
            // 都没有就从常见位置里挑一个存在的，全新电脑上也能落到个像样的地方。
            string start = EffectiveGameRoot();
            if (!string.IsNullOrEmpty(start))
            {
                dialog.InitialDirectory = start;
            }

            if (dialog.ShowDialog(this) == true)
            {
                ImportRoot(dialog.FolderName);
            }
        }

        private async void BtnAutoScan_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            await AutoScanAsync(true);
        }

        /// <summary>
        /// 扫描本机所有常见安装位置。interactive=false 时用于首次启动静默探测，
        /// 找不到就静静留在空状态，不弹框打扰。
        /// </summary>
        private async Task<bool> AutoScanAsync(bool interactive)
        {
            if (_busy)
            {
                return false;
            }

            List<string> roots = GameScanner.CommonRoots();
            if (roots.Count == 0)
            {
                // 这台机器上没有任何常见目录，直接退化成手动选目录
                if (interactive)
                {
                    BtnAdd_Click(this, null);
                }
                return false;
            }

            SetBusy(true, "正在扫描 " + roots.Count + " 个常见位置…");

            List<GameInfo> found = null;
            string error = null;
            try
            {
                found = await Task.Run(delegate
                {
                    var all = new List<GameInfo>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (string root in roots)
                    {
                        foreach (GameInfo game in GameScanner.Scan(root))
                        {
                            if (seen.Add(game.GameKey))
                            {
                                all.Add(game);
                            }
                        }
                    }

                    GameScanner.SortByGameOrder(all);
                    return all;
                });
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            SetBusy(false, null);

            if (error != null)
            {
                if (interactive)
                {
                    AppDialog.Tell(this, "扫描失败", error, DialogTone.Error, null);
                }
                return false;
            }

            bool nothing = found == null || found.Count == 0;

            if (nothing && interactive)
            {
                // 常见位置没有，直接弹目录选择，省得用户再点一次
                TxtBusy.Text = "常见位置没有找到游戏，请手动选择目录";
                BtnAdd_Click(this, null);
                return false;
            }

            if (nothing)
            {
                TxtBusy.Text = "没有自动找到游戏，点「添加游戏目录」选一下安装位置";
                return false;
            }

            return await ApplyFoundAsync(found, interactive);
        }

        /// <summary>扫描一个目录，把认出来的游戏加进列表。扫描放后台线程，避免大目录把界面卡住。</summary>
        private async void ImportRoot(string root)
        {
            if (_busy)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                AppDialog.Tell(this, "无法扫描", "目录不存在：\n" + root,
                    DialogTone.Warning, null);
                return;
            }

            SetBusy(true, "正在扫描…  " + root);

            List<GameInfo> found = null;
            string error = null;
            try
            {
                found = await Task.Run(() => GameScanner.Scan(root));
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            SetBusy(false, null);

            if (error != null)
            {
                AppDialog.Tell(this, "扫描失败", error, DialogTone.Error, null);
                return;
            }

            _config.LastRoot = root;

            if (found == null || found.Count == 0)
            {
                SaveConfig();
                AppDialog.Tell(this, "未找到游戏",
                    "这个目录里没有找到米哈游的游戏主程序。\n\n" +
                    "请选择下面任一文件所在的那一层，或者直接选它们的上层文件夹：\n\n" +
                    "· YuanShen.exe（原神）\n" +
                    "· StarRail.exe（崩坏：星穹铁道）\n" +
                    "· ZenlessZoneZero.exe（绝区零）\n" +
                    "· BH3.exe（崩坏3）",
                    DialogTone.Info, null);
                return;
            }

            await ApplyFoundAsync(found, true);
        }

        /// <summary>把扫描结果并入列表，然后补版本和素材。返回是否真的加了新游戏。</summary>
        private async Task<bool> ApplyFoundAsync(List<GameInfo> found, bool interactive)
        {
            var added = new List<GameInfo>();
            foreach (GameInfo game in found)
            {
                if (_games.Any(g => string.Equals(g.GameKey, game.GameKey, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _games.Add(game);
                added.Add(game);
            }

            SaveConfig();
            RefreshLayout();

            if (added.Count == 0)
            {
                if (interactive)
                {
                    AppDialog.Tell(this, "提示", "这些游戏已经在列表里了。", DialogTone.Info, null);
                }
                return false;
            }

            GameList.SelectedItem = added[0];
            SetStatus(added.Count > 1
                ? "已添加 " + added.Count + " 个游戏"
                : "已添加 " + added[0].Name, StatusTone.Ok);

            await CheckAllUpdatesAsync();
            await EnsureAssetsAsync();
            return true;
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            BtnAdd.IsEnabled = !busy;
            BtnAutoScan.IsEnabled = !busy;
            TxtBusy.Text = busy ? (string.IsNullOrEmpty(message) ? "正在扫描…" : message) : string.Empty;
            Cursor = busy ? Cursors.Wait : null;
        }

        // ==================== 列表与详情 ====================

        private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            GameInfo game = GameList.SelectedItem as GameInfo;
            if (game == null)
            {
                RefreshLayout();
                return;
            }

            if (!string.Equals(_config.LastGame, game.ExePath, StringComparison.OrdinalIgnoreCase))
            {
                _config.LastGame = game.ExePath;
                SaveConfig();
            }

            ShowGame(game);

            // 切到一张还没读进来的图，就顺手从本地缓存补一次
            if (!game.HasBackground)
            {
                LoadCachedBackdropForCurrentAsync();
            }
        }

        /// <summary>根据有没有选中项，在「详情」和「空状态」之间切换。</summary>
        private void RefreshLayout()
        {
            bool showDetail = GameList.SelectedItem is GameInfo;
            DetailContent.Visibility = showDetail ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = showDetail ? Visibility.Collapsed : Visibility.Visible;

            if (!showDetail)
            {
                Backdrop.Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x1A));
                ClearBackdrop();
            }
        }

        private void ShowGame(GameInfo game)
        {
            DetailContent.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;

            // 别的游戏的背景帧立刻放掉，只留当前这个
            ReleaseOtherBackdrops(game);

            TxtName.Text = game.Name;
            TxtServer.Text = game.Server;
            TxtVersion.Text = string.IsNullOrEmpty(game.Version) || game.Version == "—"
                ? "—"
                : "v" + game.Version;
            TxtPath.Text = game.ExePath;
            TxtPath.ToolTip = game.ExePath;

            // DX12 勾选项只对支持的游戏出现（目前只有绝区零），状态按游戏分别记忆
            CbDx12.Visibility = game.SupportsDx12 ? Visibility.Visible : Visibility.Collapsed;
            CbDx12.IsChecked = game.SupportsDx12 && game.UseDx12;

            // 区服 pill 现在固定用主题金（颜色写在 XAML 里），不再跟着各游戏主色变；
            // 游戏主色只留给背景兜底光晕，保证同一屏上的强调色只有一种。
            Backdrop.Background = BuildBackdrop(game.Accent);
            ApplyBackdropOnly(game);
            RestartCarousel();

            UpdateStatusUi(game);
            UpdateBackdropNotice();

            // 入场动画只播一次（第一次进详情页）。之后切游戏只做背景交叉淡入，
            // 再来一次 16px 的上浮会让人以为界面在抖。
            if (!_detailIntroPlayed)
            {
                _detailIntroPlayed = true;
                PlayDetailIn();
            }

            TrimMemorySoon();
        }

        /// <summary>
        /// 切走游戏的背景帧托管引用已经断开了，但 WPF/WIC 的原生位图要等 GC 才还回去——
        /// 不主动收的话，任务管理器里每切一次就往上爬一截，最后稳在 600MB 以上。
        /// 放到空闲优先级执行，并做防抖：连续切换只收一次。
        /// </summary>
        private void TrimMemorySoon()
        {
            if (_trimScheduled)
            {
                return;
            }

            _trimScheduled = true;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                _trimScheduled = false;
                GC.Collect(2, GCCollectionMode.Optimized, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Optimized, true);
            }), DispatcherPriority.ApplicationIdle);
        }

        private bool _trimScheduled;

        /// <summary>详情页的入场动画是否已经播过。</summary>
        private bool _detailIntroPlayed;

        // ==================== 背景层 ====================

        /// <summary>当前显示在屏幕上的那张，用来避免同一张图重复淡入。</summary>
        private ImageSource _shownBackdrop;

        /// <summary>下一次往哪一层画。两张 Image 轮流当"前台"。</summary>
        private bool _frontIsA = true;

        /// <summary>
        /// 换背景图：新图在背层画好、淡入，旧图同时淡出，形成交叉淡化。
        /// 传 null 时保留当前画面 —— 切到还没读好图的游戏时不该先变成一片纯色。
        /// </summary>
        private void ApplyBackdropImage(ImageSource image, ImageSource blur, int fadeMs)
        {
            if (blur != null)
            {
                // 糊版只当垫底，清晰图盖在上面，切图时不会露出底色
                BackdropBlur.Source = blur;
                BackdropBlur.Opacity = 0.9;
            }

            if (image == null || ReferenceEquals(_shownBackdrop, image))
            {
                return;
            }

            ShowBackdrop(image, fadeMs);
        }

        private void ApplyBackdropImage(ImageSource image, ImageSource blur)
        {
            ApplyBackdropImage(image, blur, 620);
        }

        /// <summary>把一张图放到"背层"，按 fadeMs 淡入，旧图同时淡出。</summary>
        private void ShowBackdrop(ImageSource image, int fadeMs)
        {
            Image target = _frontIsA ? BackdropB : BackdropA;
            Image current = _frontIsA ? BackdropA : BackdropB;
            ScaleTransform zoom = _frontIsA ? BackdropZoomB : BackdropZoomA;

            target.Source = image;

            // 复位缩放（Ken Burns 目前关闭，留着开关）
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            zoom.ScaleX = 1.0;
            zoom.ScaleY = 1.0;

            if (EnableKenBurns)
            {
                var grow = new DoubleAnimation(1.0, 1.055, TimeSpan.FromSeconds(CarouselSecondsSetting + 5))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
                };
                zoom.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                zoom.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }

            if (fadeMs <= 0)
            {
                target.BeginAnimation(OpacityProperty, null);
                target.Opacity = 1;
                current.BeginAnimation(OpacityProperty, null);
                current.Opacity = 0;
            }
            else
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var duration = TimeSpan.FromMilliseconds(fadeMs);

                target.BeginAnimation(OpacityProperty, null);
                target.Opacity = 0;
                target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
                {
                    EasingFunction = ease
                });

                current.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration)
                {
                    EasingFunction = ease
                });
            }

            _frontIsA = !_frontIsA;
            _shownBackdrop = image;
        }

        private void ClearBackdrop()
        {
            BackdropBlur.BeginAnimation(OpacityProperty, null);
            BackdropBlur.Source = null;
            BackdropBlur.Opacity = 0;

            BackdropA.BeginAnimation(OpacityProperty, null);
            BackdropA.Source = null;
            BackdropA.Opacity = 0;

            BackdropB.BeginAnimation(OpacityProperty, null);
            BackdropB.Source = null;
            BackdropB.Opacity = 0;

            _shownBackdrop = null;
            _frontIsA = true;
        }

        /// <summary>刷新与运行状态相关的部分：状态胶囊、主按钮文字与可用性、副信息。</summary>
        private void UpdateStatusUi(GameInfo game)
        {
            if (game.IsRunning)
            {
                SetStatus("运行中", StatusTone.Ok);
                BtnPlay.Content = "运行中";
                BtnPlay.Tag = "Running";
                BtnPlay.IsEnabled = false;
            }
            else if (!game.IsInstalled)
            {
                SetStatus("主程序缺失", StatusTone.Error);
                BtnPlay.Content = "开始游戏";
                BtnPlay.Tag = "Play";
                BtnPlay.IsEnabled = false;
            }
            else
            {
                // 一切正常时不占位：右上角留白，界面安静
                SetStatus(null, StatusTone.Idle);
                BtnPlay.Content = "开始游戏";
                BtnPlay.Tag = "Play";
                BtnPlay.IsEnabled = true;
            }

            UpdateVersionUi(game);
            TxtMeta.Text = BuildMeta(game);
        }

        /// <summary>版本状态那一块：检查中 / 有新版本 / 已是最新，同时决定副按钮的文字、颜色和可用性。</summary>
        private void UpdateVersionUi(GameInfo game)
        {
            bool canCheck = game.IsInstalled
                            && !string.IsNullOrEmpty(game.Biz)
                            && game.Version != null
                            && game.Version != "—";

            bool checking = _checking.Contains(game.GameKey);

            if (!canCheck)
            {
                TxtUpdateInfo.Text = string.Empty;
                StyleLauncherButton(false, "检查更新");
                BtnLauncher.IsEnabled = false;
                BtnLauncher.ToolTip = game.IsInstalled
                    ? "国际服版本走另一套接口，暂不支持检查"
                    : "主程序不存在，无法检查版本";
                return;
            }

            BtnLauncher.IsEnabled = !checking;

            if (checking)
            {
                TxtUpdateInfo.Text = "检查中…";
                TxtUpdateInfo.Foreground = MutedBrush;
                StyleLauncherButton(false, "检查中…");
                BtnLauncher.ToolTip = "正在向米哈游查询当前发布版本";
                return;
            }

            if (game.HasUpdate)
            {
                TxtUpdateInfo.Text = "可更新至 v" + game.LatestVersion;
                TxtUpdateInfo.Foreground = WarnBrush;
                StyleLauncherButton(true, "更新至 v" + game.LatestVersion);
                BtnLauncher.ToolTip = "点击打开官方启动器完成更新（下载与校验由官方完成）";
                return;
            }

            if (game.UpdateChecked)
            {
                TxtUpdateInfo.Text = "已是最新版本";
                TxtUpdateInfo.Foreground = OkBrush;
            }
            else if (_versionCheckFailed)
            {
                // 查过了但没成功：留个灰字，不然按钮回到原样，看起来像什么都没发生
                TxtUpdateInfo.Text = "版本检查失败";
                TxtUpdateInfo.Foreground = MutedBrush;
            }
            else
            {
                TxtUpdateInfo.Text = string.Empty;
            }

            StyleLauncherButton(false, "检查更新");
            BtnLauncher.ToolTip = "重新向米哈游查询当前发布版本";
        }

        /// <summary>
        /// 副按钮的两种形态：平时是薄玻璃的「检查更新」，检测到新版本时换成金色描边 + 下载图标，
        /// 让"该更新了"成为整屏第二显眼的东西（第一是开始游戏）。
        /// </summary>
        private void StyleLauncherButton(bool update, string text)
        {
            BtnLauncher.Tag = update ? "Update" : "Check";
            BtnLauncher.Content = text;
            BtnLauncher.Foreground = update ? UpdateTextBrush : GhostTextBrush;
            BtnLauncher.BorderBrush = update ? UpdateEdgeBrush : GhostEdgeBrush;
            BtnLauncher.Background = update ? UpdateFillBrush : GhostFillBrush;
        }

        private static string BuildMeta(GameInfo game)
        {
            if (!game.IsInstalled)
            {
                return "主程序不存在，目录可能被移走了";
            }
            if (!game.LastPlayed.HasValue)
            {
                return "尚未通过这里启动过";
            }

            DateTimeOffset time = game.LastPlayed.Value.ToLocalTime();
            string text = time.Date == DateTime.Today
                ? "今天 " + time.ToString("HH:mm")
                : time.ToString("yyyy-MM-dd HH:mm");

            return "上次启动 " + text;
        }

        private void PlayDetailIn()
        {
            var storyboard = (Storyboard)FindResource("DetailIn");
            storyboard.Begin(this, true);
        }

        /// <summary>右上角一团柔光，颜色随游戏主色变。</summary>
        private static Brush BuildBackdrop(Color accent)
        {
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.78, 0.16),
                GradientOrigin = new Point(0.78, 0.16),
                RadiusX = 0.85,
                RadiusY = 1.05
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(96, accent.R, accent.G, accent.B), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(28, accent.R, accent.G, accent.B), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0E, 0x0E, 0x14), 1.0));
            brush.Freeze();
            return brush;
        }

        // ==================== 版本检查 ====================

        /// <summary>把列表里所有支持检查的游戏一次性查完（接口支持批量，一次请求就够）。</summary>
        private async Task CheckAllUpdatesAsync()
        {            List<GameInfo> targets = _games
                .Where(g => g.IsInstalled && !string.IsNullOrEmpty(g.Biz))
                .ToList();

            if (targets.Count == 0)
            {
                return;
            }

            foreach (GameInfo game in targets)
            {
                _checking.Add(game.GameKey);
            }

            GameInfo selected = GameList.SelectedItem as GameInfo;
            if (selected != null)
            {
                UpdateVersionUi(selected);
            }

            Dictionary<string, string> latest;
            VersionChecker.FetchResult fetched;
            try
            {
                fetched = await VersionChecker.FetchLatestAsync(targets.Select(g => g.Biz).Distinct());
            }
            finally
            {
                foreach (GameInfo game in targets)
                {
                    _checking.Remove(game.GameKey);
                }
            }

            latest = fetched.Versions;
            _versionCheckFailed = latest.Count == 0 && fetched.Error != null;

            foreach (GameInfo game in targets)
            {
                string tag;
                if (game.Biz != null && latest.TryGetValue(game.Biz, out tag))
                {
                    game.LatestVersion = tag;
                    game.UpdateChecked = true;
                    game.HasUpdate = VersionChecker.IsNewer(tag, game.Version);
                }
            }

            if (selected != null)
            {
                UpdateVersionUi(selected);
            }
        }

        /// <summary>查询单个游戏的版本，结果（含失败原因）交给调用方去提示。</summary>
        private async Task<VersionChecker.FetchResult> CheckOneAsync(GameInfo game)
        {
            if (game == null || string.IsNullOrEmpty(game.Biz))
            {
                return null;
            }

            _checking.Add(game.GameKey);
            UpdateVersionUi(game);

            VersionChecker.FetchResult fetched;
            try
            {
                fetched = await VersionChecker.FetchLatestAsync(new[] { game.Biz });
            }
            finally
            {
                _checking.Remove(game.GameKey);
            }

            string tag;
            if (!fetched.Versions.TryGetValue(game.Biz, out tag))
            {
                _versionCheckFailed = fetched.Error != null;
                UpdateVersionUi(game);
                return fetched;
            }

            _versionCheckFailed = false;
            game.LatestVersion = tag;
            game.UpdateChecked = true;
            game.HasUpdate = VersionChecker.IsNewer(tag, game.Version);
            UpdateVersionUi(game);
            return fetched;
        }

        private async void BtnLauncher_Click(object sender, RoutedEventArgs e)
        {
            GameInfo game = GameList.SelectedItem as GameInfo;
            if (game == null)
            {
                return;
            }

            // 已经知道有新版本：这一步是"去更新"，打开官方启动器，下载与校验交给它
            if (game.HasUpdate)
            {
                if (OpenLauncher(game))
                {
                    SetStatus("已打开官方启动器，更新由它完成", StatusTone.Ok);
                }
                return;
            }

            if (_checking.Contains(game.GameKey))
            {
                SetStatus("正在检查版本…", StatusTone.Idle);
                return;
            }

            VersionChecker.FetchResult result = await CheckOneAsync(game);

            string tag;
            if (result != null && result.Versions.TryGetValue(game.Biz, out tag))
            {
                if (game.HasUpdate)
                {
                    SetStatus("发现新版本 v" + game.LatestVersion, StatusTone.Warn);
                }
                else
                {
                    SetStatus("已是最新版本", StatusTone.Ok);
                }
            }
            else
            {
                // 失败也要说清楚原因，不能让按钮看起来卡死没反应
                SetStatus("查询版本失败（" +
                    VersionChecker.DescribeError(result == null ? null : result.Error) +
                    "），可稍后重试", StatusTone.Error);
            }
        }

        // ==================== 官方素材（背景图 / logo） ====================

        /// <summary>
        /// 下载并加载官方的详情页背景图和 logo 字标。
        /// 首次约需几秒（背景图 2–3MB），之后走本地缓存；失败就保持纯色柔光，不影响使用。
        /// </summary>
        private async Task EnsureAssetsAsync()
        {
            List<GameInfo> targets = _games
                .Where(g => g.IsInstalled && !string.IsNullOrEmpty(g.Biz))
                .ToList();

            if (targets.Count == 0)
            {
                return;
            }

            Dictionary<string, OfficialAsset> assets = await OfficialAssets.FetchAsync();
            if (assets.Count == 0)
            {
                // 接口不通不影响已经铺好的本地缓存图，直接收工
                if (OfficialAssets.LastFetchError != null)
                {
                    _assetsUnavailable = true;
                    UpdateBackdropNotice();
                }
                return;
            }

            _assetsUnavailable = false;

            bool reloaded = false;

            foreach (GameInfo game in targets)
            {
                OfficialAsset asset;
                if (string.IsNullOrEmpty(game.Biz) || !assets.TryGetValue(game.Biz, out asset))
                {
                    continue;
                }

                List<string> urls = asset.BackgroundUrls;
                if (urls.Count == 0)
                {
                    continue;
                }

                string firstStem = game.Biz + "-bg";

                // 先记下缓存里存的地址，再去下载。地址变了说明官方换了主视觉，要重读一次。
                string cachedUrl = OfficialAssets.ReadCachedUrl(firstStem);

                // 把该游戏的所有背景都确保落到本地（原神 4 张，用来轮播）
                var paths = new List<string>();
                for (int index = 0; index < urls.Count && index < OfficialAssets.MaxBackgrounds; index++)
                {
                    string stem = index == 0 ? firstStem : game.Biz + "-bg" + (index + 1);
                    string path = await OfficialAssets.EnsureAsync(urls[index], stem);
                    if (path != null)
                    {
                        paths.Add(path);
                    }
                }

                if (paths.Count == 0)
                {
                    continue;
                }

                bool urlChanged = !string.Equals(cachedUrl, urls[0], StringComparison.Ordinal);
                if (!urlChanged && game.HasBackground)
                {
                    continue;
                }

                // 只给"当前选中的游戏"解码上屏。其它游戏把图落到磁盘就够了，
                // 切过去时再解——每张位图在 WPF 里要占几十 MB。
                if (!ReferenceEquals(game, GameList.SelectedItem) && !game.HasBackground)
                {
                    continue;
                }

                string firstFile = paths[0];
                ImageSource firstBlur = await Task.Run(
                    () => OfficialAssets.TryLoadImage(firstFile, OfficialAssets.BackdropBlurDecodeWidth));

                game.SetBackgroundPaths(paths, firstBlur);
                await PrepareFramesAsync(game, delegate
                {
                    if (ReferenceEquals(game, GameList.SelectedItem))
                    {
                        ApplyBackdropOnly(game);
                        RestartCarousel();
                    }
                });
                reloaded = true;
            }

            if (!reloaded)
            {
                UpdateBackdropNotice();
                return;
            }

            // 素材换过了才重画背景，不重播整页入场动画
            GameInfo selected = GameList.SelectedItem as GameInfo;
            if (selected != null)
            {
                ApplyBackdropOnly(selected);
                RestartCarousel();
            }

            UpdateBackdropNotice();
        }

        // ==================== 启动 ====================

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            GameInfo game = GameList.SelectedItem as GameInfo;
            if (game == null || game.IsRunning)
            {
                return;
            }
            if (!game.IsInstalled || !File.Exists(game.ExePath))
            {
                AppDialog.Tell(this, "无法启动", "主程序不存在：\n" + game.ExePath,
                    DialogTone.Warning, null);
                return;
            }

            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = game.ExePath,
                    WorkingDirectory = string.IsNullOrEmpty(game.ExeDir)
                        ? Path.GetDirectoryName(game.ExePath)
                        : game.ExeDir,
                    UseShellExecute = true
                };

                // 绝区零的 DX12 模式：官方启动器是勾选项，底层就是加一个启动参数。
                // -use-d3d12 是 2.3 之后的写法（早期教程里的 -force-d3d12 是 Unity 通用参数）。
                if (game.SupportsDx12 && game.UseDx12)
                {
                    info.Arguments = "-use-d3d12";
                }

                Process.Start(info);

                game.LastPlayed = DateTimeOffset.Now;
                SaveConfig();
                UpdateStatusUi(game);
                SetStatus("启动中…", StatusTone.Idle);
            }
            catch (Exception ex)
            {
                AppDialog.Tell(this, "启动失败", ex.Message, DialogTone.Error, null);
            }
        }

        /// <summary>
        /// 切换「使用 DX12 模式启动」。只记住当前这款游戏的选择，下次启动直接带参数。
        /// 挂在 Checked/Unchecked 上而不是 Click：回填状态、键盘空格、辅助功能接口
        /// 改变勾选时都能落盘，只有值真的变了才写配置。
        /// </summary>
        private void CbDx12_Changed(object sender, RoutedEventArgs e)
        {
            GameInfo game = GameList.SelectedItem as GameInfo;
            if (game == null || !game.SupportsDx12)
            {
                return;
            }

            bool wanted = CbDx12.IsChecked == true;
            if (game.UseDx12 == wanted)
            {
                return;
            }

            game.UseDx12 = wanted;
            SaveConfig();
        }

        /// <summary>
        /// 拉起该游戏所属渠道的官方启动器（独立窗口，不嵌入本程序）。
        /// 官方启动器自己带 requireAdministrator，系统会单独为它处理提权。
        /// </summary>
        private bool OpenLauncher(GameInfo game)
        {
            if (game == null || string.IsNullOrEmpty(game.LauncherPath) || !File.Exists(game.LauncherPath))
            {
                AppDialog.Tell(this, "无法打开更新器",
                    "没有找到这个游戏所属渠道的启动器。\n\n可以右键左侧图标 → 打开所在文件夹，确认目录结构。",
                    DialogTone.Info, null);
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = game.LauncherPath,
                    WorkingDirectory = Path.GetDirectoryName(game.LauncherPath),
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                AppDialog.Tell(this, "打开失败", ex.Message, DialogTone.Error, null);
                return false;
            }
        }

        // ==================== 进程监测 ====================

        private void ProcessTimer_Tick(object sender, EventArgs e)
        {
            RefreshRunningStateAsync();
        }

        /// <summary>防止上一次还没查完又开一次（慢机器上进程枚举可能超过 3 秒）。</summary>
        private bool _refreshingStates;

        /// <summary>
        /// 刷新"运行中"状态。进程枚举（GetProcessesByName）要扫全系统进程表，
        /// 在 UI 线程上做会周期性卡一下界面——每 3 秒一次的卡顿就是它。
        /// 放到后台线程去查，查完回 UI 线程改状态。
        /// </summary>
        private async void RefreshRunningStateAsync()
        {
            if (_refreshingStates)
            {
                return;
            }
            _refreshingStates = true;

            try
            {
                var snapshot = _games
                    .Where(g => !string.IsNullOrEmpty(g.ProcessName))
                    .Select(g => new { Key = g.GameKey, Name = g.ProcessName })
                    .ToList();

                Dictionary<string, bool> running = await Task.Run(delegate
                {
                    var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in snapshot)
                    {
                        try
                        {
                            Process[] processes = Process.GetProcessesByName(item.Name);
                            bool isRunning = processes.Length > 0;
                            foreach (Process process in processes)
                            {
                                process.Dispose();
                            }
                            map[item.Key] = isRunning;
                        }
                        catch
                        {
                            map[item.Key] = false;
                        }
                    }
                    return map;
                });

                foreach (GameInfo game in _games)
                {
                    bool isRunning;
                    game.IsRunning = running.TryGetValue(game.GameKey, out isRunning) && isRunning;
                }

                GameInfo selected = GameList.SelectedItem as GameInfo;
                if (selected != null)
                {
                    UpdateStatusUi(selected);
                }
            }
            finally
            {
                _refreshingStates = false;
            }
        }

        // ==================== 右键菜单 ====================

        private void Tile_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = sender as ListBoxItem;
            var game = item == null ? null : item.DataContext as GameInfo;
            if (game == null)
            {
                return;
            }

            var menu = new ContextMenu();

            // 右键不会自动选中条目，先选中，这样菜单里的操作和详情页看到的是同一个游戏
            GameList.SelectedItem = game;

            var openItem = new MenuItem { Header = "打开所在文件夹" };
            openItem.Click += delegate { OpenInExplorer(game.ExeDir); };
            menu.Items.Add(openItem);

            // 换盘、重装到别的目录后，主程序路径会失效，这里手动指一下新位置
            var relocateItem = new MenuItem
            {
                Header = game.IsInstalled ? "重新定位主程序…" : "找不到主程序 — 重新定位…"
            };
            relocateItem.Click += delegate { RelocateGame(game); };
            menu.Items.Add(relocateItem);

            if (!string.IsNullOrEmpty(game.LauncherPath))
            {
                var launcherItem = new MenuItem { Header = "打开官方启动器（更新 / 修复）" };
                launcherItem.Click += delegate { OpenLauncher(game); };
                menu.Items.Add(launcherItem);
            }

            // 这里原本有一条 Separator 分隔「危险操作」，去掉了：菜单本来就不长，
            // 红色字本身已经是足够的警示，多一条线反而显得空。
            var removeItem = new MenuItem { Header = "从列表移除" };
            removeItem.Style = (Style)FindResource("DangerMenuItem");
            removeItem.Click += delegate { RemoveGame(game); };
            menu.Items.Add(removeItem);

            menu.PlacementTarget = item;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void OpenInExplorer(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                AppDialog.Tell(this, "无法打开", "目录不存在：\n" + folder,
                    DialogTone.Warning, null);
                return;
            }

            try
            {
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch
            {
                // 资源管理器拉不起来不算致命问题，静默忽略
            }
        }

        /// <summary>
        /// 游戏被挪走（换盘、重装到别处）后，手动指一下新的主程序。
        /// 换掉这一条本身，保留它在列表里的位置和上次启动时间。
        /// </summary>
        private async void RelocateGame(GameInfo game)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择「" + game.Name + "」的主程序",
                Filter = "游戏主程序|*.exe|所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (!string.IsNullOrEmpty(game.ExeDir) && Directory.Exists(game.ExeDir))
            {
                dialog.InitialDirectory = game.ExeDir;
            }

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string path = dialog.FileName;
            if (GameCatalog.FindByExe(Path.GetFileName(path)) == null)
            {
                AppDialog.Tell(this, "无法识别",
                    "这个文件不是能识别的米哈游游戏主程序。\n\n" +
                    "可用的主程序：YuanShen.exe / GenshinImpact.exe / StarRail.exe /\n" +
                    "ZenlessZoneZero.exe / BH3.exe",
                    DialogTone.Warning, null);
                return;
            }

            GameInfo fresh = GameScanner.CreateFromExe(path, game.LastPlayed);
            if (fresh == null)
            {
                return;
            }

            GameInfo duplicate = _games.FirstOrDefault(
                g => !ReferenceEquals(g, game) &&
                     string.Equals(g.GameKey, fresh.GameKey, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                AppDialog.Tell(this, "提示", "这个主程序已经在列表里了。", DialogTone.Info, null);
                return;
            }

            int index = _games.IndexOf(game);
            if (index < 0)
            {
                return;
            }

            // 整条替换：位置不变，路径 / 区服 / 版本 / 图标 / 启动器入口全部重新识别
            _games[index] = fresh;
            SaveConfig();
            GameList.SelectedItem = fresh;
            SetStatus("已重新定位到 " + fresh.ExeDir, StatusTone.Ok);

            await CheckAllUpdatesAsync();
            await EnsureAssetsAsync();
        }

        private void RemoveGame(GameInfo game)
        {
            bool confirmed = AppDialog.Ask(this,
                "从列表移除",
                "把「" + game.Name + "（" + game.Server + "）」从启动器列表里去掉。\n" +
                "不会删除任何游戏文件，之后还能重新添加。",
                "移除", true, game.Icon);

            if (!confirmed)
            {
                return;
            }

            _games.Remove(game);
            SaveConfig();

            if (_games.Count > 0)
            {
                GameList.SelectedIndex = 0;
            }
            else
            {
                RefreshLayout();
            }
        }

        // ==================== 窗口 ====================

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_ROUND = 2;

        /// <summary>DWMWA_COLOR_NONE：让 DWM 不要画那圈 1px 的窗口描边。</summary>
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Win11 的圆角 + 去掉系统描边：窗口是无边框样式，不显式设置的话四角是直角，
            // 而且 DWM 会沿着边缘画一圈浅浅的亮线（就是顶部那条白边）
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

                int dark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                int noBorder = DWMWA_COLOR_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // Win10 或旧版本不认这些属性，忽略即可
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnMin_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ==================== 设置面板 ====================

        private bool _settingsOpen;

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsOpen)
            {
                CloseSettings();
                return;
            }

            LoadSettingsUi();
            NavGeneral.IsChecked = true; // 每次打开都回到「通用」
            SettingsOverlay.Visibility = Visibility.Visible;
            _settingsOpen = true;

            // 弹窗式入场：整体淡入 + 略微放大。from 0.94 只放一点点，够有"浮起来"的感觉，
            // 又不会像从很远的地方飞过来那样晃眼。
            AnimateScale(0.94, 1, 220, new CubicEase { EasingMode = EasingMode.EaseOut });
            BeginOverlayFade(0, 1, 180);
        }

        private void BtnSettingsClose_Click(object sender, RoutedEventArgs e)
        {
            CloseSettings();
        }

        private void CloseSettings()
        {
            if (!_settingsOpen)
            {
                return;
            }

            _settingsOpen = false;

            AnimateScale(1, 0.96, 140, new CubicEase { EasingMode = EasingMode.EaseIn });
            BeginOverlayFade(1, 0, 150, delegate { SettingsOverlay.Visibility = Visibility.Collapsed; });
        }

        /// <summary>弹窗的缩放动画（X/Y 一起动，保持比例）。</summary>
        private void AnimateScale(double from, double to, double ms, IEasingFunction ease)
        {
            SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            SettingsScale.ScaleX = from;
            SettingsScale.ScaleY = from;

            var animX = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = ease
            };
            var animY = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = ease
            };

            SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }

        /// <summary>整块遮罩（含面板背景）淡入淡出。面板自身位移交给另一个动画。</summary>
        private void BeginOverlayFade(double from, double to, double ms, Action done = null)
        {
            SettingsOverlay.BeginAnimation(OpacityProperty, null);
            SettingsOverlay.Opacity = from;

            var fade = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms));
            if (done != null)
            {
                fade.Completed += delegate { done(); };
            }

            SettingsOverlay.BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>把当前配置回填到面板控件上。每次打开都重读，避免别处改了没同步。</summary>
        private void LoadSettingsUi()
        {
            SwCarousel.IsChecked = !_config.CarouselDisabled;
            SwAutoStart.IsChecked = AutoStart.IsEnabled();
            SwCheckUpdate.IsChecked = !_config.CheckUpdateDisabled;

            CmbInterval.SelectedIndex = IntervalIndex(_config.CarouselSeconds);
            CmbInterval.IsEnabled = !_config.CarouselDisabled;

            TxtCacheDir.Text = OfficialAssets.CacheDir;
            TxtCacheDir.ToolTip = TxtCacheDir.Text;

            TxtAppVersion.Text = "版本 " + AppVersion();
            TxtConfigPath.Text = "配置：" + GameStore.ConfigPath;
            TxtConfigPath.ToolTip = GameStore.ConfigPath;

            UpdateCacheSize();
        }

        /// <summary>程序版本号（取程序集版本，编到哪版显示哪版）。</summary>
        private static string AppVersion()
        {
            Version version = typeof(MainWindow).Assembly.GetName().Version;
            return version == null ? "—" : version.ToString(3);
        }

        /// <summary>左侧导航切换：只显示对应 tab 的内容（打开时默认回「通用」）。</summary>
        private void SettingsNav_Changed(object sender, RoutedEventArgs e)
        {
            if (TabGeneral == null)
            {
                return; // XAML 初始化期间触发
            }

            RadioButton nav = sender as RadioButton;
            TabGeneral.Visibility = ReferenceEquals(nav, NavGeneral) ? Visibility.Visible : Visibility.Collapsed;
            TabUi.Visibility = ReferenceEquals(nav, NavUi) ? Visibility.Visible : Visibility.Collapsed;
            TabStorage.Visibility = ReferenceEquals(nav, NavStorage) ? Visibility.Visible : Visibility.Collapsed;
            TabAbout.Visibility = ReferenceEquals(nav, NavAbout) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateCacheSize()
        {
            long bytes = OfficialAssets.CacheSize();
            TxtCacheSize.Text = bytes > 0
                ? Math.Round(bytes / 1024.0 / 1024.0, 1) + " MB"
                : "空";
        }

        private static int IntervalIndex(int seconds)
        {
            switch (seconds)
            {
                case 6: return 0;
                case 15: return 2;
                case 30: return 3;
                default: return 1; // 10 秒
            }
        }

        private static int[] IntervalValues = { 6, 10, 15, 30 };

        /// <summary>「添加游戏」默认落到哪：先看设置里选的目录，再看上次用过的，最后退到常见位置。</summary>
        private string EffectiveGameRoot()
        {
            if (!string.IsNullOrEmpty(_config.DefaultRoot) && Directory.Exists(_config.DefaultRoot))
            {
                return _config.DefaultRoot;
            }

            if (!string.IsNullOrEmpty(_config.LastRoot) && Directory.Exists(_config.LastRoot))
            {
                return _config.LastRoot;
            }

            return GameScanner.CommonRoots().FirstOrDefault() ?? Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
        }

        private string PickFolder(string start)
        {
            var dialog = new OpenFolderDialog { Title = "选择目录", Multiselect = false };
            if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
            {
                dialog.InitialDirectory = start;
            }

            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }

        private async void BtnPickCacheDir_Click(object sender, RoutedEventArgs e)
        {
            string oldDir = OfficialAssets.CacheDir;
            string picked = PickFolder(oldDir);
            if (string.IsNullOrEmpty(picked))
            {
                return;
            }

            if (string.Equals(picked, oldDir, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 老目录里有东西就顺手搬过去，省得下次联网又重下一遍背景图
            if (Directory.Exists(oldDir) && Directory.GetFileSystemEntries(oldDir).Length > 0 &&
                !string.Equals(picked, OfficialAssets.DefaultCacheDir, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(delegate
                {
                    try
                    {
                        Directory.CreateDirectory(picked);
                        foreach (string file in Directory.GetFiles(oldDir))
                        {
                            string target = Path.Combine(picked, Path.GetFileName(file));
                            if (!File.Exists(target))
                            {
                                File.Move(file, target);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OfficialAssets.Note("迁移素材缓存失败：" + ex.Message);
                    }
                });
            }

            OfficialAssets.CacheDir = picked;
            _config.AssetCacheDir = picked;
            SaveConfig();

            TxtCacheDir.Text = picked;
            TxtCacheDir.ToolTip = picked;
            UpdateCacheSize();

            // 路径换了，当前这张背景按新位置重新读一次
            await ReloadBackdropsAsync();
        }

        private void BtnOpenCache_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder(OfficialAssets.CacheDir);
        }

        private void BtnOpenConfig_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder(Path.GetDirectoryName(GameStore.ConfigPath));
        }

        private const string GitHubUrl = "https://github.com/cwh-1214/MiLauncher";

        private void BtnOpenGitHub_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(GitHubUrl);
        }

        private void TxtGitHub_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            OpenUrl(GitHubUrl);
        }

        /// <summary>用系统默认浏览器打开链接。打不开就静默——不该因为一个网址把界面搞崩。</summary>
        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // 没有默认浏览器或被组策略拦掉，忽略
            }
        }

        private async void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            await Task.Run(() => OfficialAssets.ClearCache());
            UpdateCacheSize();

            // 缓存清了，当前这张图的源文件也没了，回到纯色兜底色
            await ReloadBackdropsAsync();
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
            catch
            {
                // 打不开就算了，不该让界面崩掉
            }
        }

        /// <summary>
        /// 背景相关的设置变了（清晰度 / 缓存位置 / 清了缓存）之后重新走一遍读取流程：
        /// 原来那几张已经解码的帧不能再用了，但不用重 downloading——本地文件还在就直接用。
        /// </summary>
        private async Task ReloadBackdropsAsync()
        {
            foreach (GameInfo game in _games)
            {
                game.ClearBackgrounds();
            }

            GameInfo selected = GameList.SelectedItem as GameInfo;
            if (selected == null)
            {
                return;
            }

            _backdropDecodeFailed = false;
            ClearBackdrop();
            await LoadCachedFramesAsync(selected, true);

            if (ReferenceEquals(GameList.SelectedItem, selected))
            {
                RestartCarousel();
            }
        }

        private void SwCarousel_Click(object sender, RoutedEventArgs e)
        {
            _config.CarouselDisabled = SwCarousel.IsChecked == false;
            CmbInterval.IsEnabled = !_config.CarouselDisabled;
            SaveConfig();
            RestartCarousel();
        }

        private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 打开面板时回填也会触发，那时 _settingsOpen 还是 false，别写回去
            if (!_settingsOpen || CmbInterval.SelectedIndex < 0)
            {
                return;
            }

            int index = Math.Min(CmbInterval.SelectedIndex, IntervalValues.Length - 1);
            _config.CarouselSeconds = IntervalValues[index];
            SaveConfig();

            _carouselTimer.Interval = TimeSpan.FromSeconds(_config.CarouselSeconds);
            RestartCarousel();
        }

        private void SwAutoStart_Click(object sender, RoutedEventArgs e)
        {
            AutoStart.SetEnabled(SwAutoStart.IsChecked == true);

            // 注册表可能没写进去（权限/运行环境），写完以实际状态为准回填
            _config.AutoStart = AutoStart.IsEnabled();
            SaveConfig();
            SwAutoStart.IsChecked = _config.AutoStart;
        }

        private void SwCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            _config.CheckUpdateDisabled = SwCheckUpdate.IsChecked == false;
            SaveConfig();
        }
    }
}
