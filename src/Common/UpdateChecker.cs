using System;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace TomatoBiquga
{
    /// <summary>
    /// 版本自检：问一下 GitHub 有没有新版本。
    ///
    /// 设计上的几个克制之处（都是同类工具的 issue 区教出来的）：
    ///
    ///   1. **不自动下载、不自动替换自己**。so-novel 的 `auto-update` 是默认关的，
    ///      因为"程序把自己换掉"这件事一旦出错，用户连回滚的界面都没有。
    ///      这里只做一件事：告诉你有没有新版本，并把下载页地址显示出来。
    ///   2. **不主动联网**。只有用户点了「检查更新」才发请求。
    ///      工具的主业是下载小说，不该在启动时偷偷连 GitHub（国内还经常连不上，
    ///      会让启动变慢甚至卡住）。
    ///   3. **明确区分"没更新"和"检查失败"**。网络不通时说"检查失败"，
    ///      绝不说"已是最新" —— 后者是在撒谎，用户会以为真的没新版。
    ///
    /// 走 api.github.com 而不是 github.com：README 里实测过国内 github.com 会断，
    /// 但 api.github.com 通常还通（项目自己的下载说明就是这么教的）。
    /// </summary>
    public static class UpdateChecker
    {
        public const string Owner = "George01230123";
        public const string Repo = "biquga-downloader";

        public static string ReleasesApi
        {
            get { return "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest"; }
        }

        public static string ReleasesPage
        {
            get { return "https://github.com/" + Owner + "/" + Repo + "/releases/latest"; }
        }

        /// <summary>当前程序版本（读 AssemblyInfo 里的 AssemblyVersion）</summary>
        public static Version CurrentVersion
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    return asm.GetName().Version;
                }
                catch { return new Version(0, 0, 0, 0); }
            }
        }

        public static string CurrentVersionText
        {
            get
            {
                var v = CurrentVersion;
                return string.Format("{0}.{1}.{2}", v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>检查结果</summary>
        public class Result
        {
            /// <summary>是否检查成功（网络/解析是否 OK）。false 时不要下"已是最新"的结论。</summary>
            public bool Ok;
            /// <summary>有没有更新的版本</summary>
            public bool HasUpdate;
            public string Current = "";
            public string Latest = "";
            /// <summary>Release 标题/说明（截断过）</summary>
            public string Notes = "";
            public string DownloadUrl = "";
            /// <summary>失败原因 / 提示语</summary>
            public string Message = "";

            public override string ToString() { return Message; }
        }

        /// <summary>同步检查（调用方负责放到后台线程）</summary>
        public static Result Check(Action<string> log)
        {
            var r = new Result();
            r.Current = CurrentVersionText;

            string json;
            try
            {
                // 用 curl/.NET 取数层：这里要显式带 GitHub 要求的 UA 和 Accept，
                // 否则 api.github.com 会返回 403（无 UA 的请求会被直接拒）
                json = Http.GetForApi(ReleasesApi);
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Message = "检查更新失败（网络不通或被墙）：" + ex.Message +
                            "\n\n可以手动打开下载页看看：\n" + ReleasesPage;
                if (log != null) log("检查更新失败：" + ex.Message);
                return r;
            }

            if (string.IsNullOrEmpty(json))
            {
                r.Ok = false;
                r.Message = "检查更新失败：GitHub 返回了空内容。\n\n下载页：\n" + ReleasesPage;
                return r;
            }

            // 没有 release 的仓库会返回 {"message":"Not Found"}
            if (json.IndexOf("\"message\"", StringComparison.Ordinal) >= 0 &&
                json.IndexOf("\"tag_name\"", StringComparison.Ordinal) < 0)
            {
                r.Ok = false;
                r.Message = "这个仓库还没有发布 Release。\n\n下载页：\n" + ReleasesPage;
                if (log != null) log("检查更新：仓库暂无 Release。");
                return r;
            }

            var tag = JsonStr(json, "tag_name");
            r.Latest = Normalize(tag);
            r.Notes = Truncate(JsonStr(json, "body"), 800);
            r.DownloadUrl = JsonStr(json, "html_url");
            if (string.IsNullOrEmpty(r.DownloadUrl)) r.DownloadUrl = ReleasesPage;

            if (string.IsNullOrEmpty(r.Latest))
            {
                r.Ok = false;
                r.Message = "检查更新失败：没能从 GitHub 的返回里解析出版本号。\n\n下载页：\n" + ReleasesPage;
                return r;
            }

            r.Ok = true;
            r.HasUpdate = CompareVersions(r.Latest, r.Current) > 0;
            r.Message = r.HasUpdate
                ? string.Format("发现新版本：{0} → {1}\n\n{2}", r.Current, r.Latest,
                    string.IsNullOrEmpty(r.Notes) ? "" : r.Notes)
                : string.Format("当前已是最新版本（{0}）。", r.Current);

            if (log != null)
                log(r.HasUpdate ? string.Format("发现新版本 {0}（当前 {1}）", r.Latest, r.Current)
                                : string.Format("已是最新版本（{0}）", r.Current));
            return r;
        }

        /// <summary>
        /// 把 "v1.0.5" / "1.0.5" / "release-1.0.5" 里的版本号抠出来。
        /// 抠不出来返回空串（调用方按失败处理）。
        /// </summary>
        public static string Normalize(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            var m = Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return "";
            var s = m.Groups[1].Value + "." + m.Groups[2].Value + "." +
                    (m.Groups[3].Success ? m.Groups[3].Value : "0");
            return s;
        }

        /// <summary>
        /// 比较版本，a &gt; b 返回正数。
        /// 用 System.Version 而不是字符串比较 —— "1.0.10" 按字符串比会小于 "1.0.9"。
        /// 解析不了的一律当 0，绝不因为版本号格式怪就报"有更新"。
        /// </summary>
        public static int CompareVersions(string a, string b)
        {
            Version va, vb;
            if (!Version.TryParse(Pad(a), out va)) va = new Version(0, 0, 0, 0);
            if (!Version.TryParse(Pad(b), out vb)) vb = new Version(0, 0, 0, 0);
            return va.CompareTo(vb);
        }

        private static string Pad(string v)
        {
            var s = Normalize(v);
            return string.IsNullOrEmpty(s) ? "0.0.0" : s;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", "\n").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>从 JSON 里取一个字符串字段（够用就行，不引解析器）</summary>
        internal static string JsonStr(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            var raw = m.Groups[1].Value;
            return raw.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t")
                      .Replace("\\\"", "\"").Replace("\\/", "/").Replace("\\\\", "\\")
                      .Replace("\\u003c", "<").Replace("\\u003e", ">").Replace("\\u0026", "&");
        }
    }
}
