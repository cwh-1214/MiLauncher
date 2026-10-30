using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace MiLauncher.Services
{
    /// <summary>一款米哈游游戏在本启动器里的静态定义。</summary>
    internal sealed class GameDefinition
    {
        /// <summary>主程序文件名，识别游戏靠它。</summary>
        public string ExeName;

        public string Name;

        /// <summary>图标取不到时的占位字。</summary>
        public string ShortName;

        /// <summary>进程名（不含扩展名），判断运行中靠它。</summary>
        public string ProcessName;

        /// <summary>
        /// 米哈游内部的游戏标识（hyp-connect 接口里的 game.biz），查线上版本用。
        /// 国际服不在国服接口里，留空即跳过版本检查。
        /// </summary>
        public string Biz;

        /// <summary>hyp-connect 接口用的 game_id（版本检查要拼在 URL 里）。</summary>
        public string GameId;

        /// <summary>
        /// 是否支持以 DX12 模式启动（目前只有绝区零，3.0 起）。
        /// 支持的会在详情页「开始游戏」下方显示勾选项。
        /// </summary>
        public bool SupportsDx12;

        public Color Accent;
    }

    internal static class GameCatalog
    {
        /// <summary>内置清单。官方加新游戏时，除了这里，也可以用外部 catalog 文件免编译补上。</summary>
        private static readonly GameDefinition[] BuiltIn =
        {
            // 国服原神是 YuanShen.exe，国际服是 GenshinImpact.exe，两者要分开认
            new GameDefinition
            {
                ExeName = "YuanShen.exe",
                Name = "原神",
                ShortName = "原",
                ProcessName = "YuanShen",
                Biz = "hk4e_cn",
                GameId = "1Z8W5NHUQb",
                Accent = Color.FromRgb(0xEA, 0xCB, 0x84)
            },
            new GameDefinition
            {
                ExeName = "GenshinImpact.exe",
                Name = "原神",
                ShortName = "原",
                ProcessName = "GenshinImpact",
                Biz = null,   // 国际服版本走另一套接口，暂不检查
                GameId = null,
                Accent = Color.FromRgb(0xEA, 0xCB, 0x84)
            },
            new GameDefinition
            {
                ExeName = "StarRail.exe",
                Name = "崩坏：星穹铁道",
                ShortName = "星",
                ProcessName = "StarRail",
                Biz = "hkrpg_cn",
                GameId = "64kMb5iAWu",
                Accent = Color.FromRgb(0x9B, 0x8C, 0xE8)
            },
            new GameDefinition
            {
                ExeName = "ZenlessZoneZero.exe",
                Name = "绝区零",
                ShortName = "绝",
                ProcessName = "ZenlessZoneZero",
                Biz = "nap_cn",
                GameId = "x6znKlJ0xK",
                SupportsDx12 = true,
                Accent = Color.FromRgb(0xFF, 0xD3, 0x4E)
            },
            new GameDefinition
            {
                ExeName = "BH3.exe",
                Name = "崩坏3",
                ShortName = "崩",
                ProcessName = "BH3",
                Biz = "bh3_cn",
                GameId = "osvnlOc0S8",
                Accent = Color.FromRgb(0x5A, 0xC8, 0xFA)
            }
        };

        private static GameDefinition[] _all = BuiltIn;

        /// <summary>当前生效的清单（内置 + 用户外部文件）。</summary>
        public static GameDefinition[] All
        {
            get { return _all; }
        }

        /// <summary>
        /// 外部清单路径。官方上了新游戏、或者内置的 game_id 变了，
        /// 不用重新编译：往这里放一份 JSON 就能补上 / 覆盖。
        /// </summary>
        public static string UserCatalogPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MiLauncher",
                    "games.catalog.json");
            }
        }

        private sealed class CatalogEntry
        {
            public string Exe { get; set; }
            public string Name { get; set; }
            public string ShortName { get; set; }
            public string Process { get; set; }
            public string Biz { get; set; }
            public string GameId { get; set; }
            public string Accent { get; set; }
        }

        private sealed class CatalogFile
        {
            public List<CatalogEntry> Games { get; set; }
        }

        /// <summary>
        /// 读取外部清单并合并进内置清单（同名的 exe 覆盖，新的追加）。
        /// 文件不存在、格式不对都不影响启动——出问题就只用内置的。
        /// 必须在主窗口创建之前调用一次。
        /// </summary>
        public static void LoadUserCatalog()
        {
            _all = BuiltIn;

            try
            {
                string path = UserCatalogPath;
                if (!File.Exists(path))
                {
                    return;
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                CatalogFile file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(path), options);
                if (file == null || file.Games == null)
                {
                    OfficialAssets.Note("外部清单为空或格式不对：" + path);
                    return;
                }

                var merged = new List<GameDefinition>(BuiltIn);

                foreach (CatalogEntry entry in file.Games)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Exe))
                    {
                        continue;
                    }

                    GameDefinition definition = ToDefinition(entry);
                    if (definition == null)
                    {
                        continue;
                    }

                    int index = merged.FindIndex(
                        d => string.Equals(d.ExeName, definition.ExeName, StringComparison.OrdinalIgnoreCase));

                    if (index >= 0)
                    {
                        merged[index] = definition;
                    }
                    else
                    {
                        merged.Add(definition);
                    }
                }

                _all = merged.ToArray();
            }
            catch (Exception ex)
            {
                _all = BuiltIn;
                OfficialAssets.Note("外部清单读取失败：" + ex.Message);
            }
        }

        private static GameDefinition ToDefinition(CatalogEntry entry)
        {
            string exe = entry.Exe.Trim();
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                exe += ".exe";
            }

            Color accent = Color.FromRgb(0xD3, 0xBC, 0x8E);
            if (!string.IsNullOrWhiteSpace(entry.Accent))
            {
                try
                {
                    object converted = ColorConverter.ConvertFromString(entry.Accent.Trim());
                    if (converted is Color)
                    {
                        accent = (Color)converted;
                    }
                }
                catch
                {
                    // 颜色写错就用默认金色
                }
            }

            string name = string.IsNullOrWhiteSpace(entry.Name) ? exe : entry.Name.Trim();

            return new GameDefinition
            {
                ExeName = exe,
                Name = name,
                ShortName = string.IsNullOrWhiteSpace(entry.ShortName)
                    ? name.Substring(0, 1)
                    : entry.ShortName.Trim(),
                ProcessName = string.IsNullOrWhiteSpace(entry.Process)
                    ? Path.GetFileNameWithoutExtension(exe)
                    : entry.Process.Trim(),
                Biz = string.IsNullOrWhiteSpace(entry.Biz) ? null : entry.Biz.Trim(),
                GameId = string.IsNullOrWhiteSpace(entry.GameId) ? null : entry.GameId.Trim(),
                Accent = accent
            };
        }

        /// <summary>按主程序文件名找定义，认不出返回 null。</summary>
        public static GameDefinition FindByExe(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            foreach (GameDefinition definition in _all)
            {
                if (string.Equals(definition.ExeName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return definition;
                }
            }

            return null;
        }

        /// <summary>按 biz 找定义，版本检查拼 game_id 用。</summary>
        public static GameDefinition FindByBiz(string biz)
        {
            if (string.IsNullOrEmpty(biz))
            {
                return null;
            }

            foreach (GameDefinition definition in _all)
            {
                if (string.Equals(definition.Biz, biz, StringComparison.OrdinalIgnoreCase))
                {
                    return definition;
                }
            }

            return null;
        }

        /// <summary>用于给扫描结果排序，让列表顺序稳定（按清单顺序）。</summary>
        public static int OrderOf(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return int.MaxValue;
            }

            for (int i = 0; i < _all.Length; i++)
            {
                if (string.Equals(_all[i].ExeName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return int.MaxValue;
        }
    }
}
