using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 目录遍历的**断点续爬**。
    ///
    /// 为什么需要：PC 版遍历是顺着「上一页」链串行走的，实测一本 774 章的书要 **~2 小时**。
    /// 而原来的实现在内存里攒进度，中途关掉程序（或崩一次）就**整本重来** ——
    /// 对两小时的任务来说这是最难接受的一种失败。
    ///
    /// 为什么可以安全续：遍历链是**确定的** —— 从最后一章出发，每页的「上一页」
    /// 链接（var uiiekp0do）都指向唯一的前一页，直到回到目录页。
    /// 所以只要记下"上次走到哪一个页面 key"，从那里接着走就行，不会错位、不会漏章。
    ///
    /// 保存什么、不保存什么：
    ///   · 保存：当前页 key（续爬的锚点）、起始页 key、目录、**已收录的章节（id+标题）** 和发现顺序。
    ///   · **不保存正文**：正文体积大，而它已经由遍历过程自然拿在手里了；
    ///     续爬后重新走一遍那些页只是为了拿到"更靠前的章节"，正文会在下载阶段按需再抓
    ///     （或者用户开离线模式时由预抓统一处理）。
    ///     把正文也存进去会让断点文件涨到几十 MB，得不偿失。
    ///
    /// 失效处理：起始页 key 对不上（换了书/换了源）就丢弃这份断点，从零开始 ——
    /// 宁可多走一遍，也绝不把别的书的章节拼进来。
    /// </summary>
    public static class CrawlResume
    {
        /// <summary>断点文件放在 cache 目录下，和目录缓存并列</summary>
        private static string Dir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache"); }
        }

        /// <summary>一份断点</summary>
        public class State
        {
            /// <summary>站点 key（biquga / biquga-m）</summary>
            public string Site = "";
            /// <summary>书目录，如 /10_10333</summary>
            public string BookDir = "";
            /// <summary>起始页 key（最后一章）—— 用来校验这份断点是不是这本书的</summary>
            public string StartKey = "";
            /// <summary>下次续爬要走的页 key</summary>
            public string NextKey = "";
            /// <summary>已收录章节（id 顺序即发现顺序，最终还会按 id 排序，所以顺序只影响日志）</summary>
            public List<string> Ids = new List<string>();
            /// <summary>与 Ids 一一对应的标题</summary>
            public List<string> Titles = new List<string>();
            /// <summary>已走过的页数（给日志用）</summary>
            public int Pages;
            public DateTime SavedAt = DateTime.MinValue;

            public bool IsEmpty { get { return Ids.Count == 0; } }

            public override string ToString()
            {
                return string.Format("{0} 章 / {1} 页（{2}）", Ids.Count, Pages,
                    SavedAt == DateTime.MinValue ? "unknown" : SavedAt.ToString("MM-dd HH:mm"));
            }
        }

        /// <summary>断点文件路径</summary>
        public static string PathFor(string site, string bookDir)
        {
            return Path.Combine(Dir,
                "crawl_" + Http.SafeFileName(site + "_" + (bookDir ?? "").Replace("/", "")) + ".json");
        }

        /// <summary>这本书有没有可用的断点（不校验内容，只看文件在不在）</summary>
        public static bool Exists(string site, string bookDir)
        {
            try { return File.Exists(PathFor(site, bookDir)); }
            catch { return false; }
        }

        /// <summary>删掉断点（遍历成功结束后调用，免得下次又"续"到一份旧的）</summary>
        public static void Clear(string site, string bookDir)
        {
            try
            {
                var p = PathFor(site, bookDir);
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        // ---------------------------------------------------------- 读写

        public static bool Save(State s)
        {
            if (s == null || string.IsNullOrEmpty(s.BookDir) || string.IsNullOrEmpty(s.NextKey)) return false;
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                s.SavedAt = DateTime.Now;

                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"note\": \"目录遍历断点。删掉它就等于放弃续爬、下次从头遍历。\",\n");
                sb.Append("  \"site\": ").Append(Bookshelf.J(s.Site)).Append(",\n");
                sb.Append("  \"bookDir\": ").Append(Bookshelf.J(s.BookDir)).Append(",\n");
                sb.Append("  \"startKey\": ").Append(Bookshelf.J(s.StartKey)).Append(",\n");
                sb.Append("  \"nextKey\": ").Append(Bookshelf.J(s.NextKey)).Append(",\n");
                sb.Append("  \"pages\": ").Append(s.Pages).Append(",\n");
                sb.Append("  \"savedAt\": ").Append(Bookshelf.J(s.SavedAt.ToString("yyyy-MM-dd HH:mm:ss"))).Append(",\n");
                sb.Append("  \"chapters\": [\n");
                for (int i = 0; i < s.Ids.Count; i++)
                {
                    sb.Append("    {")
                      .Append("\"id\": ").Append(Bookshelf.J(s.Ids[i])).Append(", ")
                      .Append("\"title\": ").Append(Bookshelf.J(i < s.Titles.Count ? s.Titles[i] : ""))
                      .Append("}");
                    if (i < s.Ids.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append("  ]\n}\n");

                var path = PathFor(s.Site, s.BookDir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch { return false; }
        }

        /// <summary>读断点。文件不在/损坏/不属于这本书 → null。</summary>
        public static State Load(string site, string bookDir, string expectStartKey)
        {
            try
            {
                var path = PathFor(site, bookDir);
                if (!File.Exists(path)) return null;
                var text = DirCache.ReadAllTextShared(path);
                if (string.IsNullOrEmpty(text)) return null;

                var s = new State
                {
                    Site = Str(text, "site"),
                    BookDir = Str(text, "bookDir"),
                    StartKey = Str(text, "startKey"),
                    NextKey = Str(text, "nextKey"),
                    Pages = Num(text, "pages"),
                };

                // 关键校验：这份断点必须是**同一本书**的。
                // 换书/换源后起始页 key 会变，续错会把别的书的章节拼进来。
                if (!string.IsNullOrEmpty(expectStartKey) &&
                    !string.Equals(s.StartKey, expectStartKey, StringComparison.Ordinal))
                    return null;
                if (string.IsNullOrEmpty(s.NextKey)) return null;

                var ds = Str(text, "savedAt");
                DateTime dt;
                if (DateTime.TryParse(ds, out dt)) s.SavedAt = dt;

                // 解析 chapters 数组里的 {id,title} 对
                int at = text.IndexOf("\"chapters\"", StringComparison.Ordinal);
                if (at >= 0)
                {
                    int arr = text.IndexOf('[', at);
                    int end = arr < 0 ? -1 : text.IndexOf(']', arr);
                    if (arr >= 0 && end > arr)
                    {
                        var body = text.Substring(arr, end - arr);
                        foreach (System.Text.RegularExpressions.Match m in
                            System.Text.RegularExpressions.Regex.Matches(body,
                                "\\{\\s*\"id\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*,\\s*\"title\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\}"))
                        {
                            s.Ids.Add(Bookshelf.Unescape(m.Groups[1].Value));
                            s.Titles.Add(Bookshelf.Unescape(m.Groups[2].Value));
                        }
                    }
                }
                return s;
            }
            catch { return null; }
        }

        private static string Str(string obj, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(obj,
                "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Bookshelf.Unescape(m.Groups[1].Value) : "";
        }

        private static int Num(string obj, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(obj,
                "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            int n;
            return m.Success && int.TryParse(m.Groups[1].Value, out n) ? n : 0;
        }
    }
}
