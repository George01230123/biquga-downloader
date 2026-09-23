using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TomatoBiquga
{
    /// <summary>
    /// 笔趣阁（biquga.com）站点实现。
    /// 站点自己的目录页 index_N/dindex_N 是坏的（会串到别的书），
    /// 所以这里顺着每章页面里的“下一章”链接（var kkehvov）走完整本。
    /// 正文是 base64 存在 document.writeln(qsbs.bb('...')) 里。
    /// </summary>
    public class BiqugaSite : ISite, ITextCacheProvider
    {
        public const string Origin = "https://www.biquga.com";
        public string Name { get { return "笔趣阁"; } }

        /// <summary>目录遍历时顺路抓到的正文：key -> 文本</summary>
        private readonly Dictionary<string, string> _parsedText = new Dictionary<string, string>();
        /// <summary>章节正文缓存：cid -> 文本</summary>
        private readonly Dictionary<string, string> _textCache = new Dictionary<string, string>();
        private readonly object _lock = new object();

        public int CrawlWorkers = 6;

        /// <summary>为 true 时忽略本地目录缓存，强制重新遍历站点</summary>
        public bool ForceRefresh = false;

        /// <summary>
        /// 离线模式：目录遍历时把整本正文留在内存，下载阶段完全不联网。
        /// 代价是内存（一本 300 万字约 6MB）和“目录缓存不再生效”（每次都要真遍历）。
        /// </summary>
        public bool Offline = false;

        // ---------------------------------------------------------- 搜索

        public List<BookInfo> Search(string keyword, Action<string> log)
        {
            var result = new List<BookInfo>();
            var html = Http.Post(Origin + "/search.html", "s=" + Uri.EscapeDataString(keyword), Origin + "/");
            foreach (Match m in Regex.Matches(html, @"<li>([\s\S]*?)</li>"))
            {
                var chunk = m.Groups[1].Value;
                var link = Regex.Match(chunk, "<span class=\"s2\"><a href=\"([^\"]+)\"[^>]*>([\\s\\S]*?)</a>");
                if (!link.Success) continue;
                var dir = link.Groups[1].Value.TrimEnd('/');
                if (!Regex.IsMatch(dir, @"^/\d+_\d+$")) continue;
                var author = Regex.Match(chunk, "<span class=\"s3\"><a[^>]*>([\\s\\S]*?)</a>");
                var cat = Regex.Match(chunk, "<span class=\"s1\">\\[([\\s\\S]*?)\\]</span>");
                var latest = Regex.Match(chunk, "<span class=\"s4\"><a[^>]*>([\\s\\S]*?)</a>");
                result.Add(new BookInfo
                {
                    Site = "biquga",
                    Title = Http.StripTags(link.Groups[2].Value).Trim(),
                    Author = author.Success ? Http.StripTags(author.Groups[1].Value).Trim() : "",
                    Category = cat.Success ? Http.StripTags(cat.Groups[1].Value).Trim() : "",
                    Desc = latest.Success ? "最新：" + Http.StripTags(latest.Groups[1].Value).Trim() : "",
                    Dir = dir,
                    Url = Origin + dir + "/",
                });
            }
            if (log != null) log(string.Format("搜索“{0}”得到 {1} 条结果", keyword, result.Count));
            return result;
        }

        // ---------------------------------------------------------- 目录

        public BookInfo LoadBook(BookInfo item, Action<string> log)
        {
            // 0) 目录缓存：命中就免去几分钟的遍历（离线模式下必须真遍历，否则拿不到正文）
            var cacheKey = (item.Dir ?? "").Replace("/", "");
            if (!ForceRefresh && !Offline)
            {
                var cached = DirCache.Load("biquga", cacheKey);
                if (cached != null)
                {
                    var at = DirCache.CachedAt("biquga", cacheKey);
                    var age = at.HasValue ? (DateTime.Now - at.Value).TotalHours : 999;
                    if (age <= DirCache.MaxAgeHours)
                    {
                        if (log != null) log(string.Format("使用本地目录缓存（缓存于 {0}，共 {1} 章，跳过站点遍历）",
                            at.HasValue ? at.Value.ToString("MM-dd HH:mm") : "未知", cached.Chapters.Count));
                        return cached;
                    }
                    if (log != null) log("本地目录缓存已过期（超过 " + DirCache.MaxAgeHours + " 小时），重新遍历站点…");
                }
            }

            var book = new BookInfo
            {
                Site = "biquga",
                Dir = item.Dir,
                Url = Origin + item.Dir + "/",
                Title = item.Title,
                Author = item.Author,
                Category = item.Category,
            };

            if (log != null) log("读取书籍信息…");
            var html = Http.Get(book.Url, Origin + "/");
            book.Title = FirstGroup(html, "og:novel:book_name\" content=\"([^\"]*)\"", book.Title);
            book.Author = FirstGroup(html, "og:novel:author\" content=\"([^\"]*)\"", book.Author);
            book.Category = FirstGroup(html, "og:novel:category\" content=\"([^\"]*)\"", book.Category);
            book.Status = FirstGroup(html, "og:novel:status\" content=\"([^\"]*)\"", "");
            book.Desc = CleanDesc(Http.StripTags(FirstGroup(html, "og:description\" content=\"([^\"]*)\"", "")));
            if (string.IsNullOrEmpty(book.Desc))
                book.Desc = CleanDesc(Http.StripTags(FirstGroup(html, "<div class=\"desc[^\"]*\">([\\s\\S]*?)</div>", "")));

            // 起始点 = 最后一章：详情页的章节列表里，最新的一章排在最前面。
            // 站点详情页只列最新/最早各 100 章，这里取“最后一章”，然后顺着 prev 往回走。
            var seq = new List<string>();
            foreach (Match m in Regex.Matches(html, "href=\"(" + Regex.Escape(item.Dir) + "/(\\d+)\\.html)\""))
            {
                var cid = m.Groups[2].Value;
                if (!seq.Contains(cid)) seq.Add(cid);
            }
            if (seq.Count == 0) throw new Exception("详情页里没找到任何章节链接，站点结构可能变了");

            // 详情页顺序是“最新章节在前、然后章节列表”，取 id 最大的那个作为最后一章
            string lastCid = null;
            long maxId = -1;
            foreach (var cid in seq)
            {
                long v;
                if (long.TryParse(cid, out v) && v > maxId) { maxId = v; lastCid = cid; }
            }
            if (lastCid == null) lastCid = seq[seq.Count - 1];

            if (log != null) log("《" + book.Title + "》 " + book.Author + " —— 从最后一章往回遍历目录（顺“上一页”链，一章不漏，全书约 1~2 分钟）…");

            book.Chapters = CrawlChapters(book.Dir, lastCid, log);
            if (book.Chapters.Count == 0) throw new Exception("没抓到任何章节，站点结构可能变了");
            if (log != null) log(string.Format("目录完成：共 {0} 章（{1} → {2}）", book.Chapters.Count,
                book.Chapters[0].Title, book.Chapters[book.Chapters.Count - 1].Title));

            // 离线模式：把遍历时抢到的正文全部留进内存，下载阶段就完全不联网了
            if (Offline)
            {
                PromoteAllTextToCache();
                int withText = 0;
                lock (_lock) withText = _textCache.Count;
                if (log != null) log(string.Format("离线模式：已把 {0} 章的正文留在内存里，点下载会直接写文件、不再请求站点。", withText));
            }

            DirCache.Save(book);
            if (log != null) log("目录已缓存到本地，下次载入这本书会立刻打开。");
            return book;
        }

        /// <summary>
        /// 抓取完整目录。
        ///
        /// 站点的“下一章”链接(var kkehvov)是不可靠的：同一页面多次请求会给出不同的 next
        /// （实测 10032109 的第 3 页，next 有时指第 4 页、有时指第 1 页），
        /// 顺着 next 走整本会成环、漏章（1600 页只凑出 495/772 章）。
        ///
        /// 而“上一页”链接(var uiiekp0do)完全可靠：从最后一章一路 prev 走回目录页，
        /// 恰好覆盖 772 个页面（含章内分页），一章不漏。
        /// 章节 id 单调递增，所以最后按 id 升序排就是正确阅读顺序。
        /// </summary>
        private List<ChapterInfo> CrawlChapters(string dir, string firstCid, Action<string> log)
        {
            var byCid = new Dictionary<string, string>();     // cid -> 标题
            var parsed = new Dictionary<string, string>();    // key -> 正文
            var order = new List<string>();                   // 按发现顺序（倒序）
            var visited = new HashSet<string>();

            string key = firstCid;      // 起始 = 最后一章
            int pages = 0, failures = 0;

            while (!string.IsNullOrEmpty(key) && pages < 20000)
            {
                if (visited.Contains(key)) break;
                visited.Add(key);
                pages++;

                string html = null;
                for (int attempt = 0; attempt < 3 && html == null; attempt++)
                {
                    try { html = Http.Get(UrlOf(dir, key), Origin + dir + "/"); }
                    catch (Exception ex)
                    {
                        if (attempt == 2 && log != null) log("  第 " + key + " 页抓取失败：" + ex.Message);
                    }
                }
                if (html == null) { failures++; if (failures > 20) break; continue; }

                var p = ParseChapterHtml(html);
                bool isHead = key.IndexOf('_') < 0;
                if (isHead)
                {
                    if (!byCid.ContainsKey(key)) { byCid[key] = p.Title; order.Add(key); }
                }
                if (!string.IsNullOrEmpty(p.Text)) parsed[key] = p.Text;

                // 上一页：到目录页就结束
                var prevPath = Regex.Match(html, "var uiiekp0do='([^']+)'");
                if (!prevPath.Success || !Regex.IsMatch(prevPath.Groups[1].Value, @"^/\d+_\d+/\d+"))
                {
                    if (log != null) log("  已回到目录页，全书遍历结束");
                    break;
                }
                var pk = ParseNextPath(prevPath.Groups[1].Value);   // 复用解析（同样支持 _N 分页）
                if (pk == null) break;
                key = KeyOf(pk.Item1, pk.Item2);

                if (log != null && pages % 50 == 0)
                    log(string.Format("  目录遍历中：已收录 {0} 章（第 {1} 页，共约 {2} 页待走）", byCid.Count, pages, 772));
                if (pages % 3 == 0) Http.Polite();
            }

            // 章节 id 单调递增 → 按 id 升序即阅读顺序
            var ids = new List<string>(byCid.Keys);
            ids.Sort(delegate (string a, string b)
            {
                long x, y;
                bool ax = long.TryParse(a, out x), by = long.TryParse(b, out y);
                if (ax && by) return x.CompareTo(y);
                return string.CompareOrdinal(a, b);
            });

            var list = new List<ChapterInfo>();
            int idx = 0;
            foreach (var cid in ids)
            {
                list.Add(new ChapterInfo
                {
                    Id = cid,
                    Title = string.IsNullOrEmpty(byCid[cid]) ? ("第 " + (idx + 1) + " 章") : byCid[cid],
                    Order = idx++,
                });
            }

            // 遍历时顺路抓到的正文，下载阶段直接复用（脚本里的分页正文按 key 分别存，这里合并）
            _parsedText.Clear();
            foreach (var cid in ids)
            {
                var parts = new List<string>();
                string t;
                if (parsed.TryGetValue(cid, out t) && !string.IsNullOrEmpty(t)) parts.Add(t);
                for (int page = 2; page <= 30; page++)
                {
                    if (parsed.TryGetValue(cid + "_" + page, out t) && !string.IsNullOrEmpty(t)) parts.Add(t);
                    else break;
                }
                if (parts.Count > 0)
                {
                    var joined = string.Join("\n\n", parts).Trim();
                    if (joined.Length >= 15) _parsedText[cid] = joined;
                }
            }

            if (log != null && failures > 0) log("  期间有 " + failures + " 页抓取失败（已跳过）");
            return list;
        }

        // ---------------------------------------------------------- 正文

        public string LoadChapter(BookInfo book, ChapterInfo chapter, Action<string> log)
        {
            lock (_lock)
            {
                string cached;
                if (_textCache.TryGetValue(chapter.Id, out cached) && !string.IsNullOrEmpty(cached)) return cached;
                if (_parsedText.TryGetValue(chapter.Id, out cached) && !string.IsNullOrEmpty(cached))
                {
                    _textCache[chapter.Id] = cached;
                    return cached;
                }
            }
            if (Offline) return null;   // 离线模式：没有缓存就不发请求
            var text = FetchChapterText(book.Dir, chapter.Id, book.Url);
            if (!string.IsNullOrEmpty(text)) lock (_lock) _textCache[chapter.Id] = text;
            return text;
        }

        /// <summary>
        /// 取“目录遍历时顺路抓到的正文”，不发新请求。
        /// 从本地缓存载入目录时没有这份数据，返回 null，由调用方走网络。
        /// </summary>
        public string GetCachedText(string cid)
        {
            if (string.IsNullOrEmpty(cid)) return null;
            lock (_lock)
            {
                string t;
                if (_textCache.TryGetValue(cid, out t) && !string.IsNullOrEmpty(t)) return t;
                if (_parsedText.TryGetValue(cid, out t) && !string.IsNullOrEmpty(t)) return t;
            }
            return null;
        }

        /// <summary>把整本书的正文都放进内存缓存（用于“离线模式”）</summary>
        internal void PromoteAllTextToCache()
        {
            lock (_lock)
            {
                foreach (var kv in _parsedText)
                    if (!string.IsNullOrEmpty(kv.Value)) _textCache[kv.Key] = kv.Value;
            }
        }

        private string FetchChapterText(string dir, string cid, string referer)
        {
            var parts = new List<string>();
            for (int page = 1; page <= 30; page++)
            {
                var url = page == 1
                    ? string.Format("{0}{1}/{2}.html", Origin, dir, cid)
                    : string.Format("{0}{1}/{2}_{3}.html", Origin, dir, cid, page - 1);
                var html = Http.Get(url, referer);
                var p = ParseChapterHtml(html);
                if (!string.IsNullOrEmpty(p.Text)) parts.Add(p.Text);
                if (!html.Contains(cid + "_" + page + ".html")) break;
                Http.Polite();
            }
            return string.Join("\n\n", parts).Trim();
        }

        // ---------------------------------------------------------- 解析

        internal class ParsedChapter
        {
            public string Text = "";
            public string Title = "";
            public string NextPath = "";
        }

        internal static ParsedChapter ParseChapterHtml(string html)
        {
            var sb = new StringBuilder();
            foreach (Match m in Regex.Matches(html, @"document\.writeln\(\s*qsbs\.bb\('([^']*)'\)\s*\)"))
            {
                var block = DecodeBb(m.Groups[1].Value);
                foreach (var raw in Http.StripTags(block).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
                {
                    var line = Regex.Replace(raw, @"[ \t\u3000]+", " ").Trim();
                    if (line.Length == 0) continue;
                    if (line == "送你一个现金红包！") continue;
                    if (line.StartsWith("请关闭浏览器阅读模式")) continue;
                    if (line.StartsWith("本站所有内容来源于互联网")) continue;
                    sb.Append(line).Append('\n');
                }
            }

            var titleM = Regex.Match(html, "<title>([\\s\\S]*?)</title>");
            var title = titleM.Success ? Http.StripTags(titleM.Groups[1].Value).Split('_')[0].Trim() : "";
            var nextM = Regex.Match(html, "var kkehvov='([^']+)'");

            return new ParsedChapter
            {
                Text = sb.ToString().Trim(),
                Title = title,
                NextPath = nextM.Success ? nextM.Groups[1].Value : "",
            };
        }

        internal static string DecodeBb(string b64)
        {
            var clean = Regex.Replace(b64, "[^A-Za-z0-9+/=]", "");
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(clean)); }
            catch { return ""; }
        }

        internal static string UrlOf(string dir, string key)
        {
            int i = key.IndexOf('_');
            if (i < 0) return string.Format("{0}{1}/{2}.html", Origin, dir, key);
            var cid = key.Substring(0, i);
            int page = int.Parse(key.Substring(i + 1));
            return string.Format("{0}{1}/{2}_{3}.html", Origin, dir, cid, page - 1);
        }

        internal static string KeyOf(string cid, int page)
        {
            return page == 1 ? cid : cid + "_" + page;
        }

        internal static int PageOf(string key)
        {
            int i = key.IndexOf('_');
            return i < 0 ? 1 : int.Parse(key.Substring(i + 1));
        }

        /// <summary>
        /// 解析 /10_10333/10032108_2.html 这样的路径 -> (cid, 页码)
        /// 注意：第一页是 10032108.html（无后缀），第 N 页是 10032108_{N-1}.html
        /// </summary>
        internal static Tuple<string, int> ParseNextPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var m = Regex.Match(path, @"/(\d+)(?:_(\d+))?\.html$");
            if (!m.Success) return null;
            int page = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) + 1 : 1;
            return Tuple.Create(m.Groups[1].Value, page);
        }

        private static string FirstGroup(string input, string pattern, string fallback)
        {
            var m = Regex.Match(input, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : fallback;
        }

        /// <summary>
        /// 清理站点给的“简介”。
        /// 有些书的简介字段被站点塞了导航残留（例如 og:description 直接是
        /// `ahref="#begin"立即阅读/a`），这种要当作“没有简介”，不能写进 txt。
        /// </summary>
        internal static string CleanDesc(string desc)
        {
            if (string.IsNullOrEmpty(desc)) return "";
            var s = desc.Trim();
            if (s.Length < 8) return "";
            // 站点导航/按钮残留
            string[] junk = { "立即阅读", "ahref=", "#begin", "加入书架", "开始阅读", "章节目录", "javascript:" };
            foreach (var j in junk)
                if (s.Contains(j) && s.Length < 60) return "";
            // 明显的半截 HTML
            if (s.StartsWith("a href", StringComparison.OrdinalIgnoreCase)) return "";
            if (s.StartsWith("<") || s.EndsWith("/a")) return "";
            return s;
        }
    }
}
