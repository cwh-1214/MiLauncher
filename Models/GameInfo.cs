using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace MiLauncher.Models
{
    /// <summary>
    /// 一个游戏条目：官服和 B 服的同一个游戏算两条，因为它们的安装目录、区服、版本都可能不同。
    /// </summary>
    public class GameInfo : INotifyPropertyChanged
    {
        private bool _isRunning;
        private ImageSource _icon;
        private ImageSource _backgroundBlur;
        private string _latestVersion;
        private bool _hasUpdate;
        private bool _updateChecked;

        public string Name { get; set; }

        /// <summary>图标缺失时显示的占位字。</summary>
        public string ShortName { get; set; }

        /// <summary>官服 / B服 / 国际服。</summary>
        public string Server { get; set; }

        public string Version { get; set; }

        /// <summary>游戏主程序完整路径，启动时直接用它。</summary>
        public string ExePath { get; set; }

        /// <summary>游戏主程序所在目录，作为工作目录。</summary>
        public string ExeDir { get; set; }

        /// <summary>该游戏所属渠道启动器路径（用于更新和修复），可能为空。</summary>
        public string LauncherPath { get; set; }

        /// <summary>进程名（不含 .exe），用于判断是否正在运行。</summary>
        public string ProcessName { get; set; }

        /// <summary>去重用的唯一键，等于小写的 exe 全路径。</summary>
        public string GameKey { get; set; }

        /// <summary>米哈游内部标识（hk4e_cn 等），查线上版本用。国际服为 null。</summary>
        public string Biz { get; set; }

        /// <summary>这款游戏是否支持 DX12 模式启动（目前只有绝区零）。不支持就不显示勾选项。</summary>
        public bool SupportsDx12 { get; set; }

        /// <summary>用户是否勾选了「使用 DX12 模式启动」。按游戏分别记忆，存进配置。</summary>
        public bool UseDx12 { get; set; }

        /// <summary>服务器当前发布的版本号，还没查到时为 null。</summary>
        public string LatestVersion
        {
            get { return _latestVersion; }
            set
            {
                if (_latestVersion != value)
                {
                    _latestVersion = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>线上版本是否比本地新。只有查过才有效。</summary>
        public bool HasUpdate
        {
            get { return _hasUpdate; }
            set
            {
                if (_hasUpdate != value)
                {
                    _hasUpdate = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>是否已经完成过一次版本查询（成功或失败都算查过，避免反复请求）。</summary>
        public bool UpdateChecked
        {
            get { return _updateChecked; }
            set
            {
                if (_updateChecked != value)
                {
                    _updateChecked = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>主程序当前是否存在。目录被移走或盘符没挂载时为 false。</summary>
        public bool IsInstalled { get; set; }

        public DateTimeOffset? LastPlayed { get; set; }

        /// <summary>游戏主色，用于详情页柔光和按钮。</summary>
        public Color Accent { get; set; }

        public ImageSource Icon
        {
            get { return _icon; }
            set
            {
                if (!ReferenceEquals(_icon, value))
                {
                    _icon = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>是否正在运行。左侧图标的绿点靠它显示，所以必须走通知。</summary>
        public bool IsRunning
        {
            get { return _isRunning; }
            set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 官方详情页的背景图，可能有多张（原神现在一次给 4 张），按接口顺序轮换。
        ///
        /// 每张 1280×720 的位图在 WPF 里要占几十 MB（解码位图 + 缩放副本 + 渲染纹理），
        /// 所以这里只保留"当前 + 下一张"两帧真正解码，其余位置放 null 占位，
        /// 轮播时再按需解码。
        /// </summary>
        private List<ImageSource> _backgrounds;
        private List<string> _backgroundPaths;
        private int _backgroundIndex;

        public IList<ImageSource> Backgrounds
        {
            get { return _backgrounds; }
        }

        public int BackgroundCount
        {
            get { return _backgrounds == null ? 0 : _backgrounds.Count; }
        }

        public bool HasBackground
        {
            get { return BackgroundCount > 0; }
        }

        public int BackgroundIndex
        {
            get { return BackgroundCount == 0 ? 0 : _backgroundIndex % BackgroundCount; }
        }

        /// <summary>当前这一张；还没解码时为 null（调用方应保留上一张画面，别清空）。</summary>
        public ImageSource BackgroundImage
        {
            get { return FrameAt(BackgroundIndex); }
        }

        /// <summary>同一张背景图的低分辨率副本，切图时先垫一下，避免空一帧。</summary>
        public ImageSource BackgroundBlur
        {
            get { return _backgroundBlur; }
        }

        public ImageSource FrameAt(int index)
        {
            if (_backgrounds == null || index < 0 || index >= _backgrounds.Count)
            {
                return null;
            }

            return _backgrounds[index];
        }

        public string PathAt(int index)
        {
            if (_backgroundPaths == null || index < 0 || index >= _backgroundPaths.Count)
            {
                return null;
            }

            return _backgroundPaths[index];
        }

        /// <summary>登记有哪些图（路径用于按需解码），帧数组先全部置空。</summary>
        public void SetBackgroundPaths(List<string> paths, ImageSource blur)
        {
            if (paths == null || paths.Count == 0)
            {
                _backgroundPaths = null;
                _backgrounds = null;
            }
            else
            {
                _backgroundPaths = new List<string>(paths);
                _backgrounds = new List<ImageSource>(new ImageSource[paths.Count]);
            }

            _backgroundBlur = blur;
            _backgroundIndex = 0;
            OnPropertyChanged("Backgrounds");
            OnPropertyChanged("BackgroundImage");
        }

        public void SetFrame(int index, ImageSource frame)
        {
            if (_backgrounds == null || index < 0 || index >= _backgrounds.Count)
            {
                return;
            }

            _backgrounds[index] = frame;
        }

        /// <summary>只留下这两帧，其余释放（null），把内存让出来。</summary>
        public void ReleaseFramesExcept(int keepA, int keepB)
        {
            if (_backgrounds == null)
            {
                return;
            }

            for (int index = 0; index < _backgrounds.Count; index++)
            {
                if (index != keepA && index != keepB)
                {
                    _backgrounds[index] = null;
                }
            }
        }

        /// <summary>轮播到下一张（只前进下标，解码交给调用方）。</summary>
        public void NextBackground()
        {
            if (BackgroundCount > 1)
            {
                _backgroundIndex = (_backgroundIndex + 1) % BackgroundCount;
            }
        }

        /// <summary>回到第一张（换游戏时用）。</summary>
        public void ResetBackground()
        {
            _backgroundIndex = 0;
        }

        public void ClearBackgrounds()
        {
            _backgroundPaths = null;
            _backgrounds = null;
            _backgroundBlur = null;
            _backgroundIndex = 0;
        }

        public Brush AccentBrush
        {
            get
            {
                var brush = new SolidColorBrush(Accent);
                brush.Freeze();
                return brush;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
