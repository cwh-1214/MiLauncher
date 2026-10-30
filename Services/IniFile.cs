using System;
using System.Collections.Generic;
using System.IO;

namespace MiLauncher.Services
{
    /// <summary>
    /// 极简 INI 读取器。只服务于游戏包体里的 config.ini，所以刻意做了两个简化：
    /// 1. 段落名忽略（我们的键都在 [General] / [general] 里，且不同游戏大小写不统一）；
    /// 2. 键名大小写不敏感。
    /// </summary>
    internal sealed class IniFile
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static IniFile Load(string path)
        {
            var ini = new IniFile();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return ini;
                }

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    if (line[0] == ';' || line[0] == '#')
                    {
                        continue;
                    }
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                    {
                        value = value.Substring(1, value.Length - 2);
                    }
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    ini._values[key] = value;
                }
            }
            catch
            {
                // 读不了就当空配置，区服会退化成“未知”，不影响启动
            }

            return ini;
        }

        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            string value;
            return _values.TryGetValue(key, out value) ? value : null;
        }

        public int GetInt(string key, int fallback)
        {
            string value = Get(key);
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }

            int parsed;
            return int.TryParse(value.Trim(), out parsed) ? parsed : fallback;
        }
    }
}
