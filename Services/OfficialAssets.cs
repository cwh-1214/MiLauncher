using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MiLauncher.Services
{
    /// <summary>
    /// 官方素材地址。只取详情页大背景：
    /// 接口里每张背景还带一个 icon，那是横幅的功能标签（比如"版本热点"），不是游戏 logo，不能用。
    /// </summary>
    internal sealed class OfficialAsset
    {
        public string Biz;

        /// <summary>
        /// 该游戏的全部背景图地址，按接口顺序（官方把当前在用的那条排在最前）。
        /// 原神现在给 4 张，星铁 2 张，绝区零 1 张；带视频的那条取的是视频封面。
        /// </summary>
        public List<string> BackgroundUrls = new List<string>();

        public string BackgroundUrl
        {
            get { return BackgroundUrls.Count > 0 ? BackgroundUrls[0] : null; }
        }
    }

    /// <summary>
    /// 官方素材的获取与缓存。
    ///
    /// 米哈游启动器的背景图、logo 都放在 launcher-webstatic.mihoyo.com 上，
    /// 接口（hyp-connect / getAllGameBasicInfo）会一并给出当前使用的地址。
    /// 拿到地址后下载到本地缓存，之后每次启动直接用缓存，只有地址变了才重新下载。
    ///
    /// 注意：背景图是 WebP（2560×1440）。WPF 通过 WIC 解码，需要系统装有
    /// 「WebP 图像扩展」（Win10/11 一般自带 Microsoft.WebpImageExtension）。
    /// </summary>
    internal static class OfficialAssets
    {
        private const string Endpoint =
            "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=jGHBHlcOq1&language=zh-cn";

        /// <summary>
        /// 背景图解码宽度。默认按原图 2560；主窗口启动时会按「窗口物理像素宽」调低
        /// （最小 1280、最大 2560），既保证显示时是 1:1 不放大，又不至于白占内存。
        /// </summary>
        public static int BackgroundDecodeWidth = 2560;

        /// <summary>兜底糊版的解码宽度。反正要被放大糊掉，小一点既省内存又自带柔化。</summary>
        public const int BackdropBlurDecodeWidth = 220;

        /// <summary>一个游戏最多轮换几张背景（接口给多少用多少，最多 4 张，多了内存吃不消）。</summary>
        public const int MaxBackgrounds = 4;

        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            // 12 秒足够慢网把图片下完；再久也没意义，界面不能等接口。
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MiLauncher/1.0");
            return client;
        }

        /// <summary>
        /// 素材缓存目录。默认是 %APPDATA%\MiLauncher\assets，
        /// 设置里可以改到别的地方（比如空间更大的盘），改完立即对新图生效。
        /// </summary>
        public static string CacheDir
        {
            get
            {
                string custom = _cacheDirOverride;
                if (!string.IsNullOrEmpty(custom))
                {
                    return custom;
                }

                return DefaultCacheDir;
            }
            set
            {
                _cacheDirOverride = string.IsNullOrEmpty(value) ||
                                    string.Equals(value, DefaultCacheDir, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : value;
            }
        }

        private static string _cacheDirOverride;

        public static string DefaultCacheDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MiLauncher",
                    "assets");
            }
        }

        /// <summary>缓存目录占用多少字节（目录不存在为 0）。</summary>
        public static long CacheSize()
        {
            long total = 0;

            try
            {
                string dir = CacheDir;
                if (!Directory.Exists(dir))
                {
                    return 0;
                }

                foreach (string file in Directory.GetFiles(dir))
                {
                    total += new FileInfo(file).Length;
                }
            }
            catch
            {
                // 统计失败就当 0，不影响使用
            }

            return total;
        }

        /// <summary>清空缓存目录里的素材文件（不清 Accounts 之类外面的东西），重新联网时会再下。</summary>
        public static void ClearCache()
        {
            try
            {
                string dir = CacheDir;
                if (!Directory.Exists(dir))
                {
                    return;
                }

                foreach (string file in Directory.GetFiles(dir))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 有文件被占用就跳过，下次再试
            }
        }

        /// <summary>最近一次拉取素材地址失败的原因（成功时为 null）。界面用它决定要不要提示"连不上"。</summary>
        public static string LastFetchError { get; private set; }

        /// <summary>
        /// 拉取所有游戏的官方素材地址。失败返回空表，界面退回纯色背景，不影响使用。
        /// </summary>
        public static async Task<Dictionary<string, OfficialAsset>> FetchAsync()
        {
            var result = new Dictionary<string, OfficialAsset>(StringComparer.OrdinalIgnoreCase);
            LastFetchError = null;

            try
            {
                string json = await Client.GetStringAsync(Endpoint).ConfigureAwait(false);

                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement data;
                    if (!document.RootElement.TryGetProperty("data", out data))
                    {
                        return result;
                    }

                    JsonElement list;
                    if (!data.TryGetProperty("game_info_list", out list) ||
                        list.ValueKind != JsonValueKind.Array)
                    {
                        return result;
                    }

                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        OfficialAsset asset = ParseGame(item);
                        if (asset != null && !string.IsNullOrEmpty(asset.BackgroundUrl))
                        {
                            result[asset.Biz] = asset;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 断网、接口改版都在这里落地
                LastFetchError = ex.Message;
                Note("素材接口请求失败：" + ex.Message);
            }

            return result;
        }

        private static OfficialAsset ParseGame(JsonElement item)
        {
            JsonElement game;
            if (!item.TryGetProperty("game", out game) || game.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string biz = ReadString(game, "biz");
            if (string.IsNullOrEmpty(biz))
            {
                return null;
            }

            var asset = new OfficialAsset { Biz = biz };

            JsonElement backgrounds;
            if (item.TryGetProperty("backgrounds", out backgrounds) &&
                backgrounds.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement bg in backgrounds.EnumerateArray())
                {
                    string url = ReadString(bg, "background", "url");
                    if (string.IsNullOrEmpty(url) || asset.BackgroundUrls.Contains(url))
                    {
                        continue;
                    }

                    asset.BackgroundUrls.Add(url);

                    if (asset.BackgroundUrls.Count >= MaxBackgrounds)
                    {
                        break;
                    }
                }
            }

            return asset;
        }

        private static string ReadString(JsonElement element, params string[] path)
        {
            JsonElement current = element;
            foreach (string name in path)
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(name, out current))
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }

        /// <summary>
        /// 不看网络，直接在缓存目录里找已经下好的图。找到就返回本地路径，没有返回 null。
        ///
        /// 启动时先用这个把图铺上，再去联网。这样接口挂了、断网了，
        /// 已经缓存过背景图的机器照样能立刻看到完整画面。
        /// </summary>
        public static string FindCached(string fileStem)
        {
            if (string.IsNullOrEmpty(fileStem))
            {
                return null;
            }

            try
            {
                string dir = CacheDir;
                if (!Directory.Exists(dir))
                {
                    return null;
                }

                foreach (string file in Directory.GetFiles(dir, fileStem + ".*"))
                {
                    string extension = Path.GetExtension(file);
                    if (string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (new FileInfo(file).Length > 1024)
                    {
                        return file;
                    }
                }
            }
            catch
            {
                // 读不到就当没有缓存
            }

            return null;
        }

        /// <summary>缓存里记录的来源地址；没有缓存过返回 null。</summary>
        public static string ReadCachedUrl(string fileStem)
        {
            try
            {
                string path = Path.Combine(CacheDir, fileStem + ".url");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static readonly object NoteLock = new object();

        /// <summary>
        /// 排查用的小日志：素材这边出问题时记一行到 %APPDATA%\MiLauncher\assets.log。
        /// 故意写得很轻，写不进去就拉倒。
        /// </summary>
        public static void Note(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MiLauncher");
                Directory.CreateDirectory(dir);

                lock (NoteLock)
                {
                    string logPath = Path.Combine(dir, "assets.log");

                    // 排查日志不该无限长大：超过 256KB 就清空重来（保留最近的记录就够定位问题）
                    try
                    {
                        var info = new FileInfo(logPath);
                        if (info.Exists && info.Length > 256 * 1024)
                        {
                            File.Delete(logPath);
                        }
                    }
                    catch
                    {
                        // 删不掉就继续追加，别因为日志坏了影响主流程
                    }

                    File.AppendAllText(
                        logPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message +
                        Environment.NewLine);
                }
            }
            catch
            {
                // 日志写不进去不影响功能
            }
        }

        /// <summary>
        /// 确保素材已经下载到本地，返回本地路径；失败返回 null。
        /// 同一个 URL 不会重复下载：本地有个 .url 文件记录来源地址，地址变了才重下。
        /// </summary>
        public static async Task<string> EnsureAsync(string url, string fileStem)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            string dir = CacheDir;
            string extension = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(extension) || extension.Length > 5)
            {
                extension = ".bin";
            }

            string localPath = Path.Combine(dir, fileStem + extension);
            string urlPath = Path.Combine(dir, fileStem + ".url");

            try
            {
                if (File.Exists(localPath) && File.Exists(urlPath) &&
                    string.Equals(File.ReadAllText(urlPath).Trim(), url, StringComparison.Ordinal) &&
                    new FileInfo(localPath).Length > 1024)
                {
                    return localPath;
                }

                Directory.CreateDirectory(dir);

                byte[] bytes = await Client.GetByteArrayAsync(url).ConfigureAwait(false);

                // CDN 出错时会返回一个 HTML 错误页（"<!DOCTYPE"），当图片用会解码失败，
                // 这里直接判掉，避免把坏文件写进缓存
                if (bytes.Length < 1024 || LooksLikeHtml(bytes))
                {
                    Note("素材内容异常（" + bytes.Length + " 字节）：" + url);
                    return null;
                }

                string tempPath = localPath + ".tmp";
                File.WriteAllBytes(tempPath, bytes);
                File.Copy(tempPath, localPath, true);
                File.Delete(tempPath);
                File.WriteAllText(urlPath, url);

                return localPath;
            }
            catch (Exception ex)
            {
                Note("下载失败：" + ex.Message + "  " + url);
                return null;
            }
        }

        private static bool LooksLikeHtml(byte[] bytes)
        {
            // '<' 开头基本就是 HTML 错误页
            return bytes[0] == (byte)'<';
        }

        /// <summary>
        /// 把本地图片解码成可复用的冻结位图。decodeWidth 为 0 时按原尺寸解码。
        /// 调用方应在后台线程调用，避免切换游戏时界面卡顿。
        ///
        /// 两层优化：
        /// 1. 走 UriSource 而不是把 FileStream 塞给 StreamSource：
        ///    实测同一张 2560×1440 的 webp，UriSource 约 4–16ms，
        ///    StreamSource 要 76–98ms（WIC 拿不到文件级优化），差 20 倍。
        /// 2. 解码一次后把结果写成无压缩 BMP 旁车文件（xxx.d1920.bmp）。
        ///    webp 在应用内实测要 90–250ms（冷缓存 + 解码器冷启动），
        ///    而 BMP 是裸像素，读进来就是解码完的，只要几毫秒。
        ///    源文件更新过（旁车过期）会自动重新生成。
        /// </summary>
        public static ImageSource TryLoadImage(string path, int decodeWidth)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            if (decodeWidth > 0)
            {
                string sidecar = SidecarPath(path, decodeWidth);
                if (sidecar != null && File.Exists(sidecar) &&
                    File.GetLastWriteTimeUtc(sidecar) >= File.GetLastWriteTimeUtc(path))
                {
                    ImageSource cached = LoadDirect(sidecar, 0);
                    if (cached != null)
                    {
                        return cached;
                    }
                }
            }

            ImageSource image = LoadDirect(path, decodeWidth);
            if (image != null && decodeWidth > 0)
            {
                WriteSidecar(path, decodeWidth, image);
            }

            return image;
        }

        /// <summary>
        /// 确保某张图的预解码旁车存在（没有就解一次源图并写盘）。
        /// 启动时后台调用，让之后每次切游戏都走"读文件"的快路径。
        /// </summary>
        public static void WarmSidecar(string path, int decodeWidth)
        {
            if (string.IsNullOrEmpty(path) || decodeWidth <= 0 || !File.Exists(path))
            {
                return;
            }

            try
            {
                string sidecar = SidecarPath(path, decodeWidth);
                if (sidecar == null || (File.Exists(sidecar) &&
                    File.GetLastWriteTimeUtc(sidecar) >= File.GetLastWriteTimeUtc(path)))
                {
                    return;
                }

                ImageSource image = LoadDirect(path, decodeWidth);
                if (image != null)
                {
                    WriteSidecar(path, decodeWidth, image);
                }
            }
            catch
            {
                // 预热失败无所谓，切过去时会再试
            }
        }

        /// <summary>旁车文件路径：原名 + ".d{宽度}.bmp"，和源图放一起。</summary>
        private static string SidecarPath(string path, int decodeWidth)
        {
            try
            {
                return path + ".d" + decodeWidth + ".bmp";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 删掉"宽度不是当前解码宽度"的旁车。旁车是 3.7–14.7MB 一张的裸位图，
        /// 一旦窗口搬到别的分辨率（或以前用过固定清晰度），旧宽度的文件就永远不会再被读到，
        /// 留着纯占磁盘——实测能堆到 150MB 以上。当前宽度和兜底糊版（220）那两份留着。
        /// </summary>
        public static void PruneSidecars(int keepWidth)
        {
            if (keepWidth <= 0)
            {
                return;
            }

            try
            {
                string dir = CacheDir;
                if (!Directory.Exists(dir))
                {
                    return;
                }

                foreach (string file in Directory.GetFiles(dir, "*.bmp"))
                {
                    string name = Path.GetFileName(file);
                    if (name == null || name.Length < 6)
                    {
                        continue;
                    }

                    int end = name.Length - 4;      // ".bmp" 之前
                    int i = end - 1;
                    while (i >= 0 && char.IsDigit(name[i]))
                    {
                        i--;
                    }

                    // 必须长成 name.d<数字>.bmp，别的 bmp 一律不碰
                    if (i == end - 1 || i < 1 || name[i] != 'd' || name[i - 1] != '.')
                    {
                        continue;
                    }

                    int width;
                    if (!int.TryParse(name.Substring(i + 1, end - i - 1), out width))
                    {
                        continue;
                    }

                    if (width == keepWidth || width == BackdropBlurDecodeWidth)
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 被占用就下次再删
                    }
                }
            }
            catch
            {
                // 清理失败不影响显示
            }
        }

        /// <summary>把解码好的位图落一份无压缩 BMP，下次切换直接读，不用再解 webp。</summary>
        private static void WriteSidecar(string path, int decodeWidth, ImageSource image)
        {
            try
            {
                string sidecar = SidecarPath(path, decodeWidth);
                if (sidecar == null || File.Exists(sidecar) ||
                    File.GetLastWriteTimeUtc(sidecar) >= File.GetLastWriteTimeUtc(path))
                {
                    return;
                }

                var bitmapFrame = image as BitmapFrame ?? BitmapFrame.Create((BitmapSource)image);
                var encoder = new BmpBitmapEncoder();
                encoder.Frames.Add(bitmapFrame);

                string temp = sidecar + ".tmp";
                using (FileStream stream = new FileStream(
                    temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(stream);
                }

                File.Move(temp, sidecar);
            }
            catch
            {
                // 旁车写不进去就退回每次现解，只是慢一点，不影响功能
            }
        }

        /// <summary>直接从文件解码（无旁车逻辑）。</summary>
        private static ImageSource LoadDirect(string path, int decodeWidth)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                if (decodeWidth > 0)
                {
                    image.DecodePixelWidth = decodeWidth;
                }
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                // 解码失败（比如系统缺 WebP 扩展）就退回纯色背景
                Note("图片解码失败（decodeWidth=" + decodeWidth + "）：" + path + "  " + ex.Message);
                return null;
            }
        }
    }
}
