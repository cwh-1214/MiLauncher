using System;
using System.Collections.Generic;
using System.IO;
using MiLauncher.Models;

namespace MiLauncher.Services
{
    /// <summary>
    /// 目录扫描与游戏识别。给定一个用户选的文件夹，向下广度优先找已知游戏主程序，
    /// 然后读同目录 config.ini 判定区服、取版本号，并向上回溯找到所属渠道启动器。
    /// </summary>
    internal static class GameScanner
    {
        private const int MaxDepth = 5;
        private const int MaxDirectories = 6000;

        /// <summary>这些目录里不可能有游戏主程序，直接跳过，避免扫到更新缓存。</summary>
        private static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cache", "caches", "logs", "temp", "tmp", "$recycle.bin",
            "system volume information", "node_modules", ".git", ".vs",
            "crashreport", "crashreports", "webcache", "screenshots",
            "bin", "obj"
        };

        /// <summary>
        /// 各盘根下常见的安装目录名。用户自选路径没法猜，这里只覆盖
        /// 「渠道启动器默认目录」和「很多人习惯建的 Games / 游戏 目录」，
        /// 扫不到就让用户自己选。
        /// </summary>
        private static readonly string[] CommonFolderNames =
        {
            "Game", "Games", "游戏",
            "miHoYo", "MiHoYo", "mihoyo",
            "HoYoPlay", "miHoYo Launcher",
            @"Program Files\miHoYo Launcher", @"Program Files (x86)\miHoYo Launcher",
            @"Program Files\HoYoPlay", @"Program Files (x86)\HoYoPlay",
            "Genshin Impact", "Genshin Impact bilibili",
            "Star Rail", "Star Rail bilibili",
            "ZenlessZoneZero", "ZenlessZoneZero bilibili",
            "Honkai Impact 3rd"
        };

        /// <summary>
        /// 列出本机真实存在的「常见安装位置」，按盘符顺序。
        /// 全新电脑上不保证命中——命中更好，没命中就让用户选目录。
        /// </summary>
        public static List<string> CommonRoots()
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string drive in FixedDrives())
            {
                foreach (string name in CommonFolderNames)
                {
                    string path;
                    try
                    {
                        path = Path.Combine(drive, name);
                        if (!Directory.Exists(path) || !seen.Add(path))
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    roots.Add(path);
                }
            }

            return roots;
        }

        /// <summary>枚举本机固定磁盘的根（跳过光驱、U 盘、网络盘，扫它们又慢又没意义）。</summary>
        private static IEnumerable<string> FixedDrives()
        {
            const string letters = "CDEFGHIJKLMNOPQRSTUVWXYZ";

            foreach (char letter in letters)
            {
                string root = letter + ":\\";
                bool usable = false;
                try
                {
                    if (Directory.Exists(root))
                    {
                        usable = new DriveInfo(root).DriveType == DriveType.Fixed;
                    }
                }
                catch
                {
                    usable = false;
                }

                if (usable)
                {
                    yield return root;
                }
            }
        }

        public static List<GameInfo> Scan(string root)
        {
            var results = new List<GameInfo>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return results;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<KeyValuePair<DirectoryInfo, int>>();
            queue.Enqueue(new KeyValuePair<DirectoryInfo, int>(new DirectoryInfo(root), 0));

            int visited = 0;

            while (queue.Count > 0 && visited < MaxDirectories)
            {
                KeyValuePair<DirectoryInfo, int> node = queue.Dequeue();
                DirectoryInfo dir = node.Key;
                int depth = node.Value;
                visited++;

                FileInfo hit = null;
                try
                {
                    foreach (FileInfo file in dir.GetFiles())
                    {
                        if (GameCatalog.FindByExe(file.Name) != null)
                        {
                            hit = file;
                            break;
                        }
                    }
                }
                catch
                {
                    continue;
                }

                if (hit != null)
                {
                    if (seen.Add(hit.FullName))
                    {
                        GameInfo game = CreateFromExe(hit.FullName, null);
                        if (game != null)
                        {
                            results.Add(game);
                        }
                    }

                    // 已经是游戏根目录，不用再往里挖
                    continue;
                }

                if (depth >= MaxDepth)
                {
                    continue;
                }

                DirectoryInfo[] subDirs;
                try
                {
                    subDirs = dir.GetDirectories();
                }
                catch
                {
                    continue;
                }

                foreach (DirectoryInfo sub in subDirs)
                {
                    if (SkipNames.Contains(sub.Name))
                    {
                        continue;
                    }

                    try
                    {
                        // 跳过符号链接/junction，防目录环
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    queue.Enqueue(new KeyValuePair<DirectoryInfo, int>(sub, depth + 1));
                }
            }

            SortByGameOrder(results);
            return results;
        }

        /// <summary>按固定顺序排列扫描结果，避免每次刷新顺序都不一样。</summary>
        public static void SortByGameOrder(List<GameInfo> games)
        {
            if (games == null)
            {
                return;
            }

            games.Sort(delegate (GameInfo a, GameInfo b)
            {
                return GameCatalog.OrderOf(Path.GetFileName(a.ExePath))
                    .CompareTo(GameCatalog.OrderOf(Path.GetFileName(b.ExePath)));
            });
        }

        /// <summary>由主程序路径构造一个游戏条目。认不出的 exe 返回 null。</summary>
        public static GameInfo CreateFromExe(string exePath, DateTimeOffset? lastPlayed)
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return null;
            }

            string fileName = Path.GetFileName(exePath);
            GameDefinition definition = GameCatalog.FindByExe(fileName);
            if (definition == null)
            {
                return null;
            }

            string dir = Path.GetDirectoryName(exePath);
            string configPath = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "config.ini");
            IniFile ini = IniFile.Load(configPath);

            string version = ini.Get("game_version");
            string cps = ini.Get("cps");
            int channel = ini.GetInt("channel", 0);

            return new GameInfo
            {
                Name = definition.Name,
                ShortName = definition.ShortName,
                Server = DetectServer(cps, channel, fileName),
                Version = string.IsNullOrEmpty(version) ? "—" : version.Trim(),
                ExePath = exePath,
                ExeDir = dir,
                LauncherPath = FindLauncher(dir),
                ProcessName = definition.ProcessName,
                Biz = definition.Biz,
                SupportsDx12 = definition.SupportsDx12,
                GameKey = exePath.ToLowerInvariant(),
                IsInstalled = File.Exists(exePath),
                LastPlayed = lastPlayed,
                Accent = definition.Accent,
                Icon = IconLoader.TryLoad(exePath)
            };
        }

        /// <summary>
        /// 区服判定：以包体 config.ini 的 cps / channel 为准。
        /// B 服三个游戏的 cps 写法各不相同（bilibili、bilibili_PC、zzz_bilibili_pc），所以用包含匹配。
        /// </summary>
        private static string DetectServer(string cps, int channel, string exeName)
        {
            string value = (cps ?? string.Empty).ToLowerInvariant();

            if (value.Contains("bilibili") || channel == 14)
            {
                return "B服";
            }

            // 国际服原神的主程序叫 GenshinImpact.exe，国服叫 YuanShen.exe，这是最稳的区分
            if (string.Equals(exeName, "GenshinImpact.exe", StringComparison.OrdinalIgnoreCase))
            {
                return "国际服";
            }

            if (value.Contains("hoyoverse") || value.Contains("global"))
            {
                return "国际服";
            }

            if (value.Contains("mihoyo") || channel == 1)
            {
                return "官服";
            }

            return "未知";
        }

        /// <summary>向上回溯找渠道启动器：包体目录 → games → 渠道根目录（有 launcher.exe 的那层）。</summary>
        private static string FindLauncher(string gameDir)
        {
            if (string.IsNullOrEmpty(gameDir))
            {
                return null;
            }

            try
            {
                DirectoryInfo dir = new DirectoryInfo(gameDir);
                for (int i = 0; i < 5 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir.FullName, "launcher.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    dir = dir.Parent;
                }
            }
            catch
            {
                // 路径权限问题就算了，启动器入口缺失不影响启动游戏
            }

            return null;
        }
    }
}
