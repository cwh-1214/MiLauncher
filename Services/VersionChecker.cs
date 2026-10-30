using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiLauncher.Services
{
    /// <summary>
    /// 线上版本查询。
    ///
    /// 用的是启动器自己的公开接口（hyp-connect / getGameBranches），不需要登录、不需要频道参数，
    /// 返回的是「服务器当前发布的版本号」，也就是官方启动器点开时会去比对的那个值。
    ///
    /// 这里只做查询，不下载、不写任何文件，所以不会有损坏游戏包体的风险。
    /// </summary>
    internal static class VersionChecker
    {
        private const string Endpoint =
            "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches?launcher_id=jGHBHlcOq1";

        /// <summary>一次查询的结果：版本表 + 失败原因（成功时为 null）。</summary>
        public sealed class FetchResult
        {
            public Dictionary<string, string> Versions =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string Error;
        }

        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            // 5 秒足够正常网络走完全程；再久界面就像卡死了（点按钮 10 秒没动静）。
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MiLauncher/1.0");
            return client;
        }

        /// <summary>
        /// 批量查询线上版本，返回 biz → 版本号 + 失败原因。网络失败或返回格式异常时
        /// Versions 为空表并把原因放进 Error，不抛异常（版本检查失败不该打扰使用者）。
        /// </summary>
        public static async Task<FetchResult> FetchLatestAsync(IEnumerable<string> bizes)
        {
            var result = new FetchResult();
            if (bizes == null)
            {
                return result;
            }

            var query = new List<string>();
            foreach (string biz in bizes)
            {
                // game_id 从游戏清单里取：内置清单 + 可选的 games.catalog.json
                // （官方上新游戏 / 改 id 时，不用重新编译程序）
                GameDefinition definition = GameCatalog.FindByBiz(biz);
                if (definition == null || string.IsNullOrEmpty(definition.GameId))
                {
                    continue;
                }

                string pair = "game_ids[]=" + definition.GameId;
                if (query.Contains(pair))
                {
                    continue;
                }

                query.Add(pair);
            }

            if (query.Count == 0)
            {
                return result;
            }

            string url = Endpoint + "&" + string.Join("&", query);

            try
            {
                string json = await Client.GetStringAsync(url).ConfigureAwait(false);
                result.Versions = ParseBranches(json);
            }
            catch (Exception ex)
            {
                // 断网、超时、接口改版都在这里落地，把原因带回去给界面提示
                result.Error = ex.Message;
                OfficialAssets.Note("版本接口请求失败：" + ex.Message + "  " + url);
            }

            return result;
        }

        /// <summary>把异常消息缩成几个字的短语，界面提示用。认不出来的截断。</summary>
        public static string DescribeError(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return "未知原因";
            }

            // 常见失败直接归类成短话，免得把一长串系统原文怼到状态栏里被截断
            if (error.IndexOf("超时", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                error.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                error.IndexOf("取消", StringComparison.Ordinal) >= 0)
            {
                return "连接超时";
            }

            if (error.IndexOf("resolve", StringComparison.OrdinalIgnoreCase) >= 0 ||
                error.IndexOf("解析", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "域名解析失败";
            }

            if (error.IndexOf("拒绝", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0 ||
                error.IndexOf("unreachable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                error.IndexOf("不可达", StringComparison.Ordinal) >= 0)
            {
                return "无法连接网络";
            }

            string text = error.Trim();
            if (text.Length > 20)
            {
                text = text.Substring(0, 20) + "…";
            }

            return text;
        }

        private static Dictionary<string, string> ParseBranches(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement data;
                if (!document.RootElement.TryGetProperty("data", out data))
                {
                    return result;
                }

                JsonElement branches;
                if (!data.TryGetProperty("game_branches", out branches) ||
                    branches.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                foreach (JsonElement branch in branches.EnumerateArray())
                {
                    string biz = TryReadString(branch, "game", "biz");
                    string tag = TryReadString(branch, "main", "tag");

                    if (!string.IsNullOrEmpty(biz) && !string.IsNullOrEmpty(tag))
                    {
                        result[biz] = tag.Trim();
                    }
                }
            }

            return result;
        }

        private static string TryReadString(JsonElement element, string parentName, string childName)
        {
            JsonElement parent;
            if (!element.TryGetProperty(parentName, out parent) || parent.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement child;
            if (!parent.TryGetProperty(childName, out child) || child.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return child.GetString();
        }

        /// <summary>
        /// 线上版本是否比本地新。按数字逐段比较（7.10.0 &gt; 7.9.0），
        /// 解析不出来时保守返回 false，宁可漏报也不误报"有更新"。
        /// </summary>
        public static bool IsNewer(string latest, string local)
        {
            if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(local))
            {
                return false;
            }

            Version latestVersion;
            Version localVersion;

            if (!Version.TryParse(Normalize(latest), out latestVersion) ||
                !Version.TryParse(Normalize(local), out localVersion))
            {
                return false;
            }

            return latestVersion > localVersion;
        }

        /// <summary>去掉可能存在的渠道后缀（如 "7.1.0_bilibili"），只留数字和点。</summary>
        private static string Normalize(string text)
        {
            string trimmed = text.Trim();
            int cut = trimmed.IndexOfAny(new[] { ' ', '_', '-' });
            if (cut > 0)
            {
                trimmed = trimmed.Substring(0, cut);
            }

            // 补齐成 x.y.z 形式，Version 解析要求至少两段
            if (trimmed.IndexOf('.') == trimmed.LastIndexOf('.'))
            {
                trimmed = trimmed + ".0";
            }

            return trimmed;
        }
    }
}
