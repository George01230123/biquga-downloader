using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TomatoBiquga
{
    /// <summary>
    /// 笔趣阁移动版（m.biquga.com）—— 比 PC 版快一个数量级，推荐默认使用。
    ///
    /// 为什么快：
    ///   PC 版 www.biquga.com 的目录页是坏的（返回别的书），只能顺着每章的
    ///   “上一页”链接串行走 700+ 次请求，每次都要重新 TLS 握手 → 一本要 2 小时。
    ///   移动版的目录页 /{dir}/dindex_N.html 是**完好的**，每页 100 章，
    ///   11 页就能拿到全本章节 id + 标题 → 于是可以**并发下载**。
    ///
    /// 实测：30 章并发 2.76 秒（约 11 章/秒），全本 1067 章约 10 分钟（PC 版要 2 小时）。
    ///
    /// 正文同样是 base64 塞在 document.writeln(qsbs.bb('...')) 里，解码逻辑与 PC 版一致；
    /// 章内分页同样是 cid.html → cid_1.html → cid_2.html，并发抓取。
    /// </summary>
    public class BiqugaMobileSite : ISite, ITextCacheProvider
    {
        public const string Host = "https://m.biquga.com";
        public string Name { get { return "笔趣阁(移动版·快)"; } }

        /// <summary>并发下载线程数</summary>
        public int Workers = 8;

        /// <summary>为 true 时忽略本地目录缓存</summary>
        public bool ForceRefresh = false;
        /// <summary>为 true 时把正文在载入目录阶段就缓存下来（下载阶段不再联网）</summary>
        public bool PrefetchText = false;

        private readonly Dictionary<string, string> _textCache = new Dictionary<string, string>();
        private readonly object _lock = new object();

        // 移动版没有“搜索”接口可复用（PC 的 /search.html 在移动端不可用），
        // 搜索统一走 PC 站点，拿到 dir 之后再切到移动版下载。
        public List<BookInfo> Search(string keyword, Action<string> log)
        {
            var pc = new BiqugaSite();
            var hits = pc.Search(keyword, log);
            foreach (var h in hits) h.Site = "biquga-m";
            return hits;
        }

        // ---------------------------------------------------------- 目录

        public BookInfo LoadBook(BookInfo item, Action<string> log)
        {
            var dir = item.Dir;
            if (string.IsNullOrEmpty(dir)) throw new Exception("书籍目录为空");
            if (!dir.StartsWith("/")) dir = "/" + dir;      // 容忍传 "69_69707" 这种写法
            item.Dir = dir;

            // 缓存键必须和 DirCache.KeyFor 保持一致（否则存进去读不出来，每次都要重爬）
            var cacheKey = dir.Replace("/", "");
            if (!ForceRefresh)
            {
                var cached = DirCache.Load("biquga-m", cacheKey);
                if (cached != null)
                {
                    var at = DirCache.CachedAt("biquga-m", cacheKey);
                    var age = at.HasValue ? (DateTime.Now - at.Value).TotalHours : 999;
                    if (age <= DirCache.MaxAgeHours)
                    {
                        // 完整性校验：缓存可能是旧版本代码写坏的（比如只存了 1 页 100 章）。
                        // 这里花 1 次请求读 dindex_1 拿到真实页数，跟缓存章数对一下。
                        int realCount = ProbeChapterCount(dir);
                        if (realCount <= 0 || Math.Abs(realCount - cached.Chapters.Count) <= 5)
                        {
                            if (log != null) log(string.Format("使用本地目录缓存（缓存于 {0}，共 {1} 章）",
                                at.HasValue ? at.Value.ToString("MM-dd HH:mm") : "未知", cached.Chapters.Count));
                            return cached;
                        }
                        if (log != null) log(string.Format(
                            "本地缓存只有 {0} 章，但站点实际有约 {1} 章 —— 缓存不完整（可能是旧版本写的坏缓存），重新抓取目录…",
                            cached.Chapters.Count, realCount));
                    }
                }
            }

            var book = new BookInfo
            {
                Site = "biquga-m",
                Dir = dir,
                Url = Host + dir + "/",
                Title = item.Title,
                Author = item.Author,
                Category = item.Category,
            };

            if (log != null) log("读取移动版书籍信息…");
            var home = Http.Get(book.Url, Host + "/");
            book.Title = FirstGroup(home, "og:novel:book_name\" content=\"([^\"]*)\"", book.Title);
            book.Author = FirstGroup(home, "og:novel:author\" content=\"([^\"]*)\"", book.Author);
            book.Category = FirstGroup(home, "og:novel:category\" content=\"([^\"]*)\"", book.Category);
            book.Status = FirstGroup(home, "og:novel:status\" content=\"([^\"]*)\"", "");
            var desc = CleanDesc(Http.StripTags(FirstGroup(home, "og:description\" content=\"([^\"]*)\"", "")));
            if (string.IsNullOrEmpty(desc))
                desc = CleanDesc(Http.StripTags(FirstGroup(home, "<div class=\"desc[^\"]*\">([\\s\\S]*?)</div>", "")));
            book.Desc = desc;

            // 目录页：dindex_1 里会列出全部分页（dindex_1..N）
            if (log != null) log("正在读取移动版目录页（每页 100 章，通常十几页就够全本）…");
            var first = Http.Get(Host + dir + "/dindex_1.html", book.Url);
            var pageNums = new List<int>();
            foreach (Match m in Regex.Matches(first, Regex.Escape(dir) + "/dindex_(\\d+)\\.html"))
            {
                int p = int.Parse(m.Groups[1].Value);
                if (!pageNums.Contains(p)) pageNums.Add(p);
            }
            pageNums.Sort();
            if (pageNums.Count == 0) throw new Exception("移动版目录页里没找到分页信息，站点可能改版了");
            int maxPage = pageNums[pageNums.Count - 1];
            if (log != null) log(string.Format("目录共 {0} 页，开始并发抓取…", maxPage));

            // 目录页可以并发抓（这一点和 PC 版完全不同）
            var pageHtml = new string[maxPage + 1];
            pageHtml[1] = first;
            var tasks = new List<Task>();
            var sem = new SemaphoreSlim(Workers);
            for (int p = 2; p <= maxPage; p++)
            {
                int page = p;
                tasks.Add(Task.Run(() =>
                {
                    sem.Wait();
                    try { pageHtml[page] = Http.Get(Host + dir + "/dindex_" + page + ".html", book.Url); }
                    catch (Exception ex) { if (log != null) log("  目录第 " + page + " 页失败：" + ex.Message); }
                    finally { sem.Release(); }
                }));
            }
            Task.WaitAll(tasks.ToArray());

            // 解析出 (cid, title)，目录页是倒序的（最新的在前），按 id 排序还原阅读顺序
            var map = new Dictionary<string, string>();
            var order = new List<string>();
            for (int p = 1; p <= maxPage; p++)
            {
                var html = pageHtml[p];
                if (string.IsNullOrEmpty(html)) continue;
                foreach (Match m in Regex.Matches(html,
                    "<a href=\"" + Regex.Escape(dir) + "/(\\d+)\\.html\"[^>]*>([\\s\\S]*?)</a>"))
                {
                    var cid = m.Groups[1].Value;
                    if (map.ContainsKey(cid)) continue;
                    var title = Http.StripTags(m.Groups[2].Value).Trim();
                    title = Regex.Replace(title, @"\s+", " ");
                    map[cid] = title;
                    order.Add(cid);
                }
            }
            if (map.Count == 0) throw new Exception("移动版目录页没解析到章节");

            order.Sort(delegate (string a, string b)
            {
                long x, y;
                bool ax = long.TryParse(a, out x), by = long.TryParse(b, out y);
                if (ax && by) return x.CompareTo(y);
                return string.CompareOrdinal(a, b);
            });

            int idx = 0;
            foreach (var cid in order)
            {
                book.Chapters.Add(new ChapterInfo
                {
                    Id = cid,
                    Title = string.IsNullOrEmpty(map[cid]) ? ("第 " + (idx + 1) + " 章") : map[cid],
                    Order = idx++,
                });
            }

            if (log != null) log(string.Format("目录完成：共 {0} 章（{1} → {2}）",
                book.Chapters.Count, book.Chapters[0].Title, book.Chapters[book.Chapters.Count - 1].Title));

            DirCache.Save(book);
            if (log != null) log("目录已缓存到本地。");

            if (PrefetchText)
            {
                if (log != null) log(string.Format("正在并发预抓正文（{0} 线程）…", Workers));
                Prefetch(book, log);
            }
            return book;
        }

        /// <summary>并发把整本正文抓到内存（配合“下载”阶段不再联网）</summary>
        public void Prefetch(BookInfo book, Action<string> log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int done = 0, fail = 0;
            var sem = new SemaphoreSlim(Workers);
            var tasks = new List<Task>();
            foreach (var c in book.Chapters)
            {
                if (string.IsNullOrEmpty(c.Id)) continue;
                var chapter = c;
                tasks.Add(Task.Run(() =>
                {
                    sem.Wait();
                    try
                    {
                        var t = FetchChapterText(book.Dir, chapter.Id);
                        if (!string.IsNullOrEmpty(t))
                        {
                            lock (_lock) _textCache[chapter.Id] = t;
                            Interlocked.Increment(ref done);
                        }
                        else Interlocked.Increment(ref fail);
                    }
                    catch { Interlocked.Increment(ref fail); }
                    finally
                    {
                        sem.Release();
                        int d = done + fail;
                        if (log != null && d % 100 == 0)
                            log(string.Format("  已抓 {0}/{1} 章（失败 {2}）…", d, book.Chapters.Count, fail));
                    }
                }));
            }
            Task.WaitAll(tasks.ToArray());
            if (log != null) log(string.Format("预抓完成：成功 {0} 章，失败 {1} 章，用时 {2:F1}s",
                done, fail, sw.Elapsed.TotalSeconds));
        }

        // ---------------------------------------------------------- 正文

        public string LoadChapter(BookInfo book, ChapterInfo chapter, Action<string> log)
        {
            lock (_lock)
            {
                string t;
                if (_textCache.TryGetValue(chapter.Id, out t) && !string.IsNullOrEmpty(t)) return t;
            }
            if (PrefetchText) return null;   // 预抓模式：没抓到就不发请求（保证速度）
            var text = FetchChapterText(book.Dir, chapter.Id);
            if (!string.IsNullOrEmpty(text)) lock (_lock) _textCache[chapter.Id] = text;
            return text;
        }

        /// <summary>取已缓存的正文（供 DownloadRunner 复用，避免重复请求）</summary>
        public string GetCachedText(string cid)
        {
            if (string.IsNullOrEmpty(cid)) return null;
            lock (_lock)
            {
                string t;
                if (_textCache.TryGetValue(cid, out t) && !string.IsNullOrEmpty(t)) return t;
            }
            return null;
        }

        /// <summary>抓一章（含章内分页），与 PC 版同样的解析方式</summary>
        private string FetchChapterText(string dir, string cid)
        {
            var parts = new List<string>();
            for (int page = 1; page <= 30; page++)
            {
                var url = page == 1
                    ? string.Format("{0}{1}/{2}.html", Host, dir, cid)
                    : string.Format("{0}{1}/{2}_{3}.html", Host, dir, cid, page - 1);
                var html = Http.Get(url, Host + dir + "/");
                var p = BiqugaSite.ParseChapterHtml(html);
                if (!string.IsNullOrEmpty(p.Text)) parts.Add(p.Text);
                if (!html.Contains(cid + "_" + page + ".html")) break;
            }
            return string.Join("\n\n", parts).Trim();
        }

        // ---------------------------------------------------------- 工具

        /// <summary>
        /// 花 1 次请求探出这本书大致有多少章（读 dindex_1 里的分页选项 + 最后一页的条数）。
        /// 用于校验本地缓存是否完整。失败返回 -1。
        /// </summary>
        public static int ProbeChapterCount(string dir)
        {
            try
            {
                var first = Http.Get(Host + dir + "/dindex_1.html", Host + "/");
                var pages = new List<int>();
                foreach (Match m in Regex.Matches(first, Regex.Escape(dir) + "/dindex_(\\d+)\\.html"))
                {
                    int p = int.Parse(m.Groups[1].Value);
                    if (!pages.Contains(p)) pages.Add(p);
                }
                if (pages.Count == 0) return -1;
                pages.Sort();
                int maxPage = pages[pages.Count - 1];
                if (maxPage <= 1)
                {
                    // 只有一页，直接数
                    return Regex.Matches(first, "<a href=\"" + Regex.Escape(dir) + "/\\d+\\.html\"").Count;
                }
                var last = Http.Get(Host + dir + "/dindex_" + maxPage + ".html", Host + "/");
                int lastCount = Regex.Matches(last, "<a href=\"" + Regex.Escape(dir) + "/\\d+\\.html\"").Count;
                return (maxPage - 1) * 100 + lastCount;
            }
            catch
            {
                return -1;
            }
        }

        private static string FirstGroup(string input, string pattern, string fallback)
        {
            var m = Regex.Match(input, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : fallback;
        }

        private static string CleanDesc(string desc)
        {
            return BiqugaSite.CleanDesc(desc);
        }
    }
}
