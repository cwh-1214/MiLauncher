using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MiLauncher.Services
{
    /// <summary>配置文件里保存的一条游戏记录。只存主程序路径和上次启动时间，其余每次启动时重新识别。</summary>
    internal sealed class StoredGame
    {
        public string Exe { get; set; }

        public DateTimeOffset? LastPlayed { get; set; }

        /// <summary>是否用 DX12 模式启动（绝区零专用，其它游戏恒为 false）。</summary>
        public bool Dx12 { get; set; }
    }

    internal sealed class LauncherConfig
    {
        public int Version { get; set; }

        /// <summary>上次选择目录的位置，下次打开对话框时直接定位过去。</summary>
        public string LastRoot { get; set; }

        /// <summary>上次选中的游戏（主程序路径），下次打开直接停在这个游戏上。</summary>
        public string LastGame { get; set; }

        /// <summary>是否已经自动扫描过常见安装位置（首次运行引导只做一次）。</summary>
        public bool AutoScanDone { get; set; }

        /// <summary>上次的窗口位置（DIP）。窗口尺寸是固定的，不存。为空表示用默认居中。</summary>
        public double? WindowLeft { get; set; }

        public double? WindowTop { get; set; }

        /// <summary>素材缓存目录。为空表示用默认的 %APPDATA%\MiLauncher\assets。</summary>
        public string AssetCacheDir { get; set; }

        /// <summary>「添加游戏」默认落到哪个目录；为空时用第一个固定盘的根。</summary>
        public string DefaultRoot { get; set; }

        /// <summary>背景轮播关了就一直停在第一张（反向记：老配置缺这个字段默认是开着的）。</summary>
        public bool CarouselDisabled { get; set; }

        /// <summary>轮播间隔秒数，小于等于 0 时用默认 10 秒。</summary>
        public int CarouselSeconds { get; set; }

        /// <summary>是否隐藏详情页「开始游戏」下方那行安装路径（默认显示）。</summary>
        public bool HideInstallPath { get; set; }

        /// <summary>背景缓慢推近。默认关：实测开着内存从 190MB 涨到 500MB+。</summary>
        public bool KenBurnsEnabled { get; set; }

        /// <summary>开机自动启动（注册表 Run 项）。</summary>
        public bool AutoStart { get; set; }

        /// <summary>关掉后就只在新扫描/手动刷新时查版本，启动时不再自动联网。</summary>
        public bool CheckUpdateDisabled { get; set; }

        public List<StoredGame> Games { get; set; }
    }

    /// <summary>
    /// 配置读写。放在 %APPDATA%\MiLauncher\games.json，
    /// 和程序目录解耦——以后把 exe 挪到别处、或者换 Debug/Release，列表都还在。
    /// </summary>
    internal static class GameStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static string ConfigPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MiLauncher",
                    "games.json");
            }
        }

        /// <summary>上一版配置。原子替换时会留下这份备份，主文件读不出来时靠它救场。</summary>
        private static string BackupPath
        {
            get { return ConfigPath + ".bak"; }
        }

        public static LauncherConfig Load()
        {
            // 先读主文件，读不出来（不存在 / 内容坏了 / 写了一半）再试备份
            LauncherConfig loaded = TryLoadFrom(ConfigPath);
            if (loaded != null)
            {
                return loaded;
            }

            LauncherConfig backup = TryLoadFrom(BackupPath);
            if (backup != null)
            {
                OfficialAssets.Note("配置文件读取失败，已从 games.json.bak 恢复");
                return backup;
            }

            return new LauncherConfig
            {
                Version = 1,
                Games = new List<StoredGame>()
            };
        }

        private static LauncherConfig TryLoadFrom(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                LauncherConfig loaded = JsonSerializer.Deserialize<LauncherConfig>(json, Options);
                if (loaded == null)
                {
                    return null;
                }

                if (loaded.Games == null)
                {
                    loaded.Games = new List<StoredGame>();
                }

                return loaded;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 保存配置。先写 .tmp，再原子替换正式文件——写到一半断电 / 被强杀时，
        /// 正式文件仍然是完整的上一版，不会变成半截 JSON 把游戏列表清空。
        /// </summary>
        public static bool TrySave(LauncherConfig config, out string error)
        {
            error = null;
            string tempPath = ConfigPath + ".tmp";

            try
            {
                string folder = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string json = JsonSerializer.Serialize(config, Options);
                File.WriteAllText(tempPath, json);

                if (File.Exists(ConfigPath))
                {
                    // File.Replace 是原子的，顺带把旧文件留成 .bak
                    File.Replace(tempPath, ConfigPath, BackupPath, true);
                }
                else
                {
                    File.Move(tempPath, ConfigPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;

                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // 临时文件删不掉就算了
                }

                return false;
            }
        }
    }
}
