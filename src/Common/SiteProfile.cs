using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 单个站点的**连接参数**：并发、间隔、重试、超时、UA。
    ///
    /// 这个类只存"怎么连接"，**不存"怎么提取内容"** —— 后者（选择器 / 正则 / XPath）
    /// 一旦内置或分发，性质就变成"分发书源"了，这是本项目明确不碰的红线。
    /// <see cref="SiteProfileStore.Validate"/> 会检查用户手改的文件里有没有这类参数。
    /// </summary>
    public class SiteProfile
    {
        /// <summary>站点 key（和设置界面里的站点名对应：fanqie / biquga-m / biquga）</summary>
        public string Name = "";
        /// <summary>给用户看的中文名</summary>
        public string DisplayName = "";

        public int Workers = 8;              // 并发线程数
        public int MinDelayMs = 60;          // 请求间隔（随机区间）
        public int MaxDelayMs = 180;
        public int TimeoutSeconds = 25;      // 单次请求超时
        public int MaxRetries = 3;           // 单次请求重试次数
        public string UserAgent = "";        // 空 = 用内置默认

        /// <summary>
        /// 输出文件编码。只允许这两种：
        ///  utf-8（默认，带 BOM，手机阅读器/记事本都认）
        ///  gbk（老设备/老阅读器需要）
        /// </summary>
        public string OutputEncoding = "utf-8";

        public void Clamp()
        {
            Workers = Clamp(Workers, 1, 32);
            MinDelayMs = Clamp(MinDelayMs, 0, 10000);
            MaxDelayMs = Clamp(MaxDelayMs, MinDelayMs, 10000);
            TimeoutSeconds = Clamp(TimeoutSeconds, 5, 300);
            MaxRetries = Clamp(MaxRetries, 0, 10);
            if (string.IsNullOrEmpty(UserAgent)) UserAgent = "";
            if (!IsUtf8(OutputEncoding) && !IsGbk(OutputEncoding)) OutputEncoding = "utf-8";
        }

        public bool IsUtf8(string s) { return !string.IsNullOrEmpty(s) && s.Replace("-", "").ToLowerInvariant() == "utf8"; }
        public bool IsGbk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            var v = s.Replace("-", "").ToLowerInvariant();
            return v == "gbk" || v == "gb2312" || v == "gb18030";
        }

        /// <summary>输出文件的编码对象（utf-8 带 BOM / gbk 不带）</summary>
        public Encoding FileEncoding()
        {
            if (IsGbk(OutputEncoding))
            {
                try { return Encoding.GetEncoding("GBK"); } catch { return new UTF8Encoding(true); }
            }
            return new UTF8Encoding(true);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        public override string ToString()
        {
            return string.Format("{0}: 并发 {1}，间隔 {2}~{3}ms，超时 {4}s，重试 {5}，编码 {6}",
                DisplayName.Length > 0 ? DisplayName : Name, Workers, MinDelayMs, MaxDelayMs,
                TimeoutSeconds, MaxRetries, OutputEncoding);
        }
    }

    /// <summary>
    /// 站点参数的读写：一个 `站点配置.ini`（键=值，可手改）。
    ///
    /// 为什么还是 ini 而不是 json：和 settings.ini 保持一致（这个项目里所有配置都是
    /// "能用记事本改"的形式），而且 ini 不会被误当成"书源规则文件"。
    /// </summary>
    public static class SiteProfileStore
    {
        public const string FileName = "站点配置.ini";

        /// <summary>被明确拒绝的键（这些属于"内容提取"，不属于"连接参数"）</summary>
        private static readonly string[] ForbiddenKeys = new[]
        {
            "selector", "selectors", "xpath", "regex", "regexp", "pattern", "patterns",
            "rule", "rules", "extract", "extractor", "contentselector", "titleselector",
            "listselector", "chapterrule", "bookrule", "parser", "script", "js", "eval",
            "选择器", "正则", "规则", "提取", "解析规则",
        };

        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        /// <summary>内置默认值：三个站点各一份，和代码里的默认参数一致</summary>
        public static List<SiteProfile> Defaults()
        {
            var list = new List<SiteProfile>();
            list.Add(new SiteProfile
            {
                Name = "fanqie", DisplayName = "番茄小说",
                Workers = 4, MinDelayMs = 120, MaxDelayMs = 400, TimeoutSeconds = 25, MaxRetries = 3,
            });
            list.Add(new SiteProfile
            {
                Name = "biquga-m", DisplayName = "笔趣阁（移动版·快）",
                Workers = 8, MinDelayMs = 60, MaxDelayMs = 180, TimeoutSeconds = 25, MaxRetries = 3,
            });
            list.Add(new SiteProfile
            {
                Name = "biquga", DisplayName = "笔趣阁（PC版·慢）",
                Workers = 6, MinDelayMs = 120, MaxDelayMs = 400, TimeoutSeconds = 25, MaxRetries = 3,
            });
            return list;
        }

        /// <summary>按站点 key 取参数（文件里没有这一节就用默认值）</summary>
        public static SiteProfile Get(string siteKey)
        {
            var list = Load(DefaultPath);
            foreach (var p in list)
                if (string.Equals(p.Name, siteKey, StringComparison.OrdinalIgnoreCase)) return p;
            foreach (var p in Defaults())
                if (string.Equals(p.Name, siteKey, StringComparison.OrdinalIgnoreCase)) return p;
            return new SiteProfile { Name = siteKey, DisplayName = siteKey };
        }

        /// <summary>
        /// 加载配置。文件不存在 → 返回默认值（并写一份带注释的样例，方便用户手改）。
        /// 文件损坏 → 记下 <see cref="LastError"/> 并回退默认值，绝不让程序起不来。
        /// </summary>
        public static List<SiteProfile> Load(string path)
        {
            LastError = null;
            var result = Defaults();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;

                var byName = new Dictionary<string, SiteProfile>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in result) byName[p.Name] = p;

                string section = null;
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        if (!byName.ContainsKey(section))
                        {
                            var np = new SiteProfile { Name = section, DisplayName = section };
                            byName[section] = np;
                            result.Add(np);
                        }
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();

                    // ★ 合规闸门：拒绝任何"内容提取"类参数，并在日志/提示里说明原因
                    foreach (var bad in ForbiddenKeys)
                    {
                        if (key.ToLowerInvariant().Replace(" ", "").Contains(bad))
                        {
                            LastError = string.Format(
                                "配置里有「{0}」这类参数 —— 那是「内容提取规则」，本工具不支持也不分发。" +
                                "这个文件只允许放连接参数（并发/间隔/超时/重试/UA/编码）。该行已忽略。", key);
                            continue;
                        }
                    }

                    if (section == null) continue;
                    var prof = byName[section];
                    int n;
                    var k = key.ToLowerInvariant();
                    switch (k)
                    {
                        case "workers": if (int.TryParse(val, out n)) prof.Workers = n; break;
                        case "mindelayms": if (int.TryParse(val, out n)) prof.MinDelayMs = n; break;
                        case "maxdelayms": if (int.TryParse(val, out n)) prof.MaxDelayMs = n; break;
                        case "timeoutseconds": if (int.TryParse(val, out n)) prof.TimeoutSeconds = n; break;
                        case "maxretries": if (int.TryParse(val, out n)) prof.MaxRetries = n; break;
                        case "useragent": if (val.Length > 0) prof.UserAgent = val; break;
                        case "outputencoding": prof.OutputEncoding = val; break;
                        case "displayname": if (val.Length > 0) prof.DisplayName = val; break;
                        // 未知键忽略：以后加参数时老文件也不会报错
                    }
                }
                foreach (var p in result) p.Clamp();
                return result;
            }
            catch (Exception ex)
            {
                LastError = "读取站点配置失败（已回退默认值）：" + ex.Message;
                return Defaults();
            }
        }

        /// <summary>最近一次加载的错误/警告（给界面显示用）</summary>
        public static string LastError;

        /// <summary>写一份带注释的样例配置（首次运行时生成，让用户有东西可改）</summary>
        public static void SaveSample(string path, List<SiteProfile> profiles)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# 站点连接参数（这个文件可以直接用记事本改，改完重启程序生效）");
            sb.AppendLine("#");
            sb.AppendLine("# 只放「怎么连接」的参数：并发 / 请求间隔 / 超时 / 重试 / User-Agent / 输出编码。");
            sb.AppendLine("# 本文件**不接受**选择器、正则、XPath 这类「内容怎么提取」的规则 ——");
            sb.AppendLine("# 那是书源规则，本工具不内置、不分发（放进来会被忽略）。");
            sb.AppendLine("#");
            sb.AppendLine("# 调节建议：被站点限速（正文变成「访问太频繁」）就把 workers 调小、间隔调大；");
            sb.AppendLine("#           网络好又想快，可以适当调大 workers，但别超过 16。");
            sb.AppendLine("# ============================================================");
            sb.AppendLine();
            foreach (var p in profiles)
            {
                sb.AppendLine("[" + p.Name + "]");
                sb.AppendLine("displayname=" + p.DisplayName);
                sb.AppendLine("workers=" + p.Workers);
                sb.AppendLine("mindelayms=" + p.MinDelayMs);
                sb.AppendLine("maxdelayms=" + p.MaxDelayMs);
                sb.AppendLine("timeoutseconds=" + p.TimeoutSeconds);
                sb.AppendLine("maxretries=" + p.MaxRetries);
                sb.AppendLine("outputencoding=" + p.OutputEncoding);
                sb.AppendLine("useragent=" + p.UserAgent);
                sb.AppendLine();
            }
            try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); }
            catch { /* 写不出样例不影响使用 */ }
        }

        /// <summary>把站点参数真正作用到取数层（和 AppSettings 一起生效）</summary>
        public static void Apply(SiteProfile p)
        {
            if (p == null) return;
            Http.Configure(p.MinDelayMs, p.MaxDelayMs);
            Http.TimeoutSeconds = p.TimeoutSeconds;
            Http.MaxRetries = p.MaxRetries;
            if (!string.IsNullOrEmpty(p.UserAgent)) Http.UserAgent = p.UserAgent;
        }

        public static string CurrentEncodingName(SiteProfile p)
        {
            return p == null ? "utf-8" : p.OutputEncoding;
        }
    }
}
