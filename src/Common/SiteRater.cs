using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>一次实测的评分结果。</summary>
    public sealed class SiteScore
    {
        public string Key;
        public string DisplayName;

        /// <summary>搜索耗时（ms）。失败 = PositiveInfinity</summary>
        public double SearchMs = double.PositiveInfinity;
        /// <summary>载入目录耗时（ms）</summary>
        public double TocMs = double.PositiveInfinity;
        /// <summary>抓一章正文耗时（ms）</summary>
        public double ChapterMs = double.PositiveInfinity;

        /// <summary>实测到的目录章数（0 = 没测出来）</summary>
        public int TocCount;

        /// <summary>失败原因；测成功则为 null</summary>
        public string Error;

        /// <summary>测出来的这本书（给用户看："实测用的哪本书"）</summary>
        public string SampleTitle;

        /// <summary>综合分：搜索 + 目录 + 正文单章。越小越快。</summary>
        public double TotalMs { get { return SearchMs + TocMs + ChapterMs; } }

        public bool Ok { get { return Error == null && !double.IsInfinity(TotalMs); } }

        /// <summary>过了这个时间就重测</summary>
        public DateTime MeasuredAt;

        /// <summary>
        /// 这一项**没测出来**（不是因为"测得很快"）。
        /// 和 0 区分开：0 是"瞬间完成"，未测是"不知道"。
        /// </summary>
        public bool SearchMeasured, TocMeasured, ChapterMeasured;

        /// <summary>
        /// 造一个"三项都测到了"的分数。
        ///
        /// 为什么要这么个工厂：三项的"已测"标志默认是 false，
        /// 直接给 SearchMs/TocMs/ChapterMs 赋值**不算测到**（MarkUnmeasured 会
        /// 把它们清成 ∞）。手写对象字面量时很容易忘掉标志位，于是分数读回来变 ∞、
        /// 站点被判成不可用 —— 这个坑我踩过一次，所以给个不会写错的入口。
        /// </summary>
        public static SiteScore Measured(string key, string displayName,
            double searchMs, double tocMs, double chapterMs)
        {
            return new SiteScore
            {
                Key = key,
                DisplayName = displayName,
                SearchMs = searchMs,
                TocMs = tocMs,
                ChapterMs = chapterMs,
                SearchMeasured = true,
                TocMeasured = true,
                ChapterMeasured = true,
                MeasuredAt = DateTime.Now,
            };
        }

        public void MarkUnmeasured()
        {
            if (!SearchMeasured) SearchMs = double.PositiveInfinity;
            if (!TocMeasured) TocMs = double.PositiveInfinity;
            if (!ChapterMeasured) ChapterMs = double.PositiveInfinity;
        }

        public override string ToString()
        {
            if (!Ok) return (DisplayName ?? Key) + "：不可用（" + (Error ?? "未知") + "）";
            return string.Format(CultureInfo.InvariantCulture,
                "{0}：{1:F0} ms（搜索 {2:F0} + 目录 {3:F0} + 正文 {4:F0}）",
                DisplayName ?? Key, TotalMs, SearchMs, TocMs, ChapterMs);
        }
    }

    /// <summary>
    /// 「哪个站点最快」的实测与排名。
    ///
    /// 定义（用户确认过的口径）：综合分 = 搜索 + 目录 + 正文单章，**升序**取最小。
    /// 只测这三件事，因为这三件事正好对应"搜一本书 → 载入目录 → 开始下载"
    /// 的真实等待；只测搜索是不够的（有的站搜索快、正文页却慢）。
    ///
    /// 几条刻意的克制：
    ///   · 每站只发 **3 个请求**（搜索 / 详情页 / 一章正文），**绝不遍历整本**。
    ///   · 结果写进 站点配置.ini，**24 小时过期**；过期重测。
    ///   · 启动时后台测一次、**不弹窗**；也能由「检测站点」按钮手动触发。
    ///   · 任何一个站失败都不影响别的站；**全都失败时调用方回退到内置笔趣阁**。
    ///   · SuppressDialogs（自测/自动化）下**不联网**，直接用缓存或空结果。
    /// </summary>
    public static class SiteRater
    {
        /// <summary>测出来的分数能信多久</summary>
        public static double MaxAgeHours = 24;

        private static readonly object _lock = new object();
        private static List<SiteScore> _cached;

        /// <summary>评测时用的关键词：规则表站点用自己声明并实测过的书名</summary>
        public static string KeywordFor(SiteDescriptor d)
        {
            var rule = d == null ? null : d.Rule as SiteRule;
            if (rule != null && rule.KnownTitles != null && rule.KnownTitles.Length > 0)
                return rule.KnownTitles[0];

            // 内置站点没有规则表，用实测过的固定书名兜底：
            //   biquga / biquga-m 都在笔趣阁体系里，「沧元图」实测能搜到。
            if (d != null && (d.Key == "biquga" || d.Key == "biquga-m")) return "沧元图";
            // 番茄没有关键词搜索（要走 book_id / 浏览器），不参与评分
            return null;
        }

        /// <summary>
        /// 给单个站点打分。**会联网**，每次最多 3 个请求。
        /// 任何异常都被收敛成"这个站不可用"，绝不往外抛。
        ///
        /// 整站超过 <see cref="MaxSiteMs"/> 就放弃并记成超时 ——
        /// 实测笔趣阁 PC 版的目录遍历要 **91 万毫秒（15 分钟）**，
        /// 不设上限的话点一次「检测站点」要等一刻钟，等于不能用。
        /// </summary>
        public static SiteScore Measure(SiteDescriptor d)
        {
            var box = new SiteScoreBox();
            var th = new System.Threading.Thread(delegate () { box.Value = MeasureCore(d); });
            th.IsBackground = true;
            th.Start();
            if (!th.Join(MaxSiteMs))
            {
                // 线程还在跑（后台线程），这里如实返回"超时"，不假装测到了
                return new SiteScore
                {
                    Key = d.Key,
                    DisplayName = d.DisplayName,
                    Error = "实测超时（超过 " + (MaxSiteMs / 1000) + " 秒）",
                    MeasuredAt = DateTime.Now,
                };
            }
            var s = box.Value;
            if (s == null)
            {
                return new SiteScore
                {
                    Key = d.Key, DisplayName = d.DisplayName,
                    Error = "实测异常结束", MeasuredAt = DateTime.Now,
                };
            }
            s.MarkUnmeasured();     // 没测到的项一律记成 ∞，绝不能留 0 冒充"很快"
            return s;
        }

        /// <summary>整站实测的时间上限。超过就判超时。</summary>
        public static int MaxSiteMs = 90000;

        /// <summary>把返回值从另一个线程带回来（C# 5 没有 Task 之外的好办法，这个最直白）</summary>
        private sealed class SiteScoreBox { public SiteScore Value; }

        private static SiteScore MeasureCore(SiteDescriptor d)
        {
            var s = new SiteScore
            {
                Key = d.Key,
                DisplayName = d.DisplayName,
                MeasuredAt = DateTime.Now,
            };

            string kw = KeywordFor(d);
            if (string.IsNullOrEmpty(kw))
            {
                s.Error = "该站点没有可用于评测的关键词搜索";
                return s;
            }

            ISite site = null;
            try { site = d.Factory(); }
            catch (Exception ex) { s.Error = "创建站点失败：" + ex.Message; return s; }

            // ★ 测速度必须**绕开目录缓存**，否则会把"缓存命中"当成"这个站很快"。
            //   实测过一次：笔趣阁 PC 版的目录缓存命中只要 36 ms，
            //   而它真实遍历要 15 分钟 —— 不绕开的话排名完全失真。
            var bq = site as BiqugaSite;
            if (bq != null) bq.SuppressDirCache = true;

            // 没实现搜索的站（番茄）不参与
            if (!d.Has(SiteCapabilities.Search))
            {
                s.Error = "该站点不支持关键词搜索";
                return s;
            }

            var sw = new System.Diagnostics.Stopwatch();

            // ---- 1) 搜索 ----
            List<BookInfo> found = null;
            try
            {
                sw.Restart();
                found = site.Search(kw, null);
                sw.Stop();
                s.SearchMs = sw.Elapsed.TotalMilliseconds;
                s.SearchMeasured = true;
            }
            catch (Exception ex)
            {
                s.Error = "搜索失败：" + ex.Message;
                return s;
            }
            if (found == null || found.Count == 0)
            {
                s.Error = "搜索「" + kw + "」无结果";
                return s;
            }

            // ---- 2) 载入目录 ----
            BookInfo book = null;
            try
            {
                sw.Restart();
                book = site.LoadBook(found[0], null);
                sw.Stop();
                s.TocMs = sw.Elapsed.TotalMilliseconds;
                s.TocMeasured = true;
            }
            catch (Exception ex)
            {
                s.Error = "载入目录失败：" + ex.Message;
                return s;
            }
            if (book == null || book.Chapters == null || book.Chapters.Count == 0)
            {
                s.Error = "目录为空";
                return s;
            }
            s.TocCount = book.Chapters.Count;
            s.SampleTitle = book.Title;

            // ---- 3) 抓一章正文（取中间那章，避开卷首公告） ----
            try
            {
                var mid = book.Chapters[book.Chapters.Count / 2];
                sw.Restart();
                string text = site.LoadChapter(book, mid, null);
                sw.Stop();
                if (string.IsNullOrEmpty(text))
                {
                    s.Error = "正文抓取为空";
                    return s;
                }
                s.ChapterMs = sw.Elapsed.TotalMilliseconds;
                s.ChapterMeasured = true;
            }
            catch (Exception ex)
            {
                s.Error = "抓正文失败：" + ex.Message;
                return s;
            }

            return s;
        }

        /// <summary>对全部站点逐个打分（串行 —— 并发测会互相抢带宽，反而测不准）</summary>
        public static List<SiteScore> MeasureAll(Action<string> log)
        {
            var list = new List<SiteScore>();
            foreach (var d in SiteRegistry.All)
            {
                if (log != null) log("  实测「" + d.DisplayName + "」…");
                var s = Measure(d);
                list.Add(s);
                if (log != null) log("    " + s);
            }
            return list;
        }

        /// <summary>按综合分升序排名（不可用的排最后，且彼此保持注册顺序）</summary>
        public static List<SiteScore> Rank(IEnumerable<SiteScore> scores)
        {
            var list = new List<SiteScore>(scores);
            list.Sort(delegate (SiteScore a, SiteScore b)
            {
                bool ao = a.Ok, bo = b.Ok;
                if (ao != bo) return ao ? -1 : 1;          // 可用的永远在前
                if (!ao && !bo) return 0;                   // 都不可用：保持原序
                int c = a.TotalMs.CompareTo(b.TotalMs);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Key, b.Key); // 分数相同就按 Key 稳定排序
            });
            return list;
        }

        /// <summary>
        /// 取缓存（过期或没有则返回 null）。缓存来自 站点配置.ini 的 [评分] 段。
        /// </summary>
        public static List<SiteScore> LoadCache()
        {
            lock (_lock) { return _cached; }
        }

        public static void SetCache(List<SiteScore> scores)
        {
            lock (_lock) { _cached = scores; }
        }

        /// <summary>缓存是否还新鲜</summary>
        public static bool IsFresh(List<SiteScore> scores)
        {
            if (scores == null || scores.Count == 0) return false;
            foreach (var s in scores)
                if ((DateTime.Now - s.MeasuredAt).TotalHours > MaxAgeHours) return false;
            return true;
        }

        /// <summary>当前可用的最快站点 Key；没有可用的（或没测过）返回 null</summary>
        public static string FastestKey(List<SiteScore> scores)
        {
            if (scores == null || scores.Count == 0) return null;
            foreach (var s in Rank(scores))
            {
                // 只认**注册表里存在**的 Key：不认识的 Key 即使分低也不能被选中，
                // 否则界面会拿它去造一个根本不存在的站点对象。
                if (s.Ok && SiteRegistry.ByKey(s.Key) != null) return s.Key;
            }
            return null;
        }

        // ============================================================
        //  持久化：写进 站点配置.ini 的 [评分] 段
        // ============================================================

        private const string Section = "评分";

        /// <summary>
        /// 把评分写进 站点配置.ini。
        ///
        /// 为什么用 ini 而不是单独一个 json：项目里所有配置都是"能用记事本改"的
        /// 纯文本，多一个 json 就多一种要解释的东西。（和历史一样，这个决定是为了
        /// 让用户能自己看懂文件里有什么。）
        /// </summary>
        public static void Save(List<SiteScore> scores, string path)
        {
            if (scores == null || scores.Count == 0) return;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                if (File.Exists(path))
                {
                    string old = File.ReadAllText(path, Encoding.UTF8);
                    sb.Append(StripSection(old, Section));
                }
                if (sb.Length > 0 && !sb.ToString().EndsWith("\n")) sb.AppendLine();
                sb.AppendLine("[" + Section + "]");
                sb.AppendLine("# 由「自动选最快站点」实测写入，24 小时后自动重测。");
                sb.AppendLine("# totalsearch/toc/chapter 单位毫秒，越小越快；删掉本段就会重新测。");
                foreach (var s in scores)
                {
                    sb.AppendLine(SafeKey(s.Key) + "=" + Fmt(s));
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                SetCache(scores);
            }
            catch { /* 写不进去不影响使用，只是下次还要重测 */ }
        }

        /// <summary>
        /// 从 站点配置.ini 读回评分。返回 null = 没有可用记录。
        ///
        /// 单行格式：`评分=搜索,目录,正文,章数,时间戳,书名`；不可用站点写 `-`。
        /// 之所以把 DisplayName 也存进去：界面要在**不联网**的情况下显示排名。
        /// </summary>
        public static List<SiteScore> Load(string path)
        {
            var list = new List<SiteScore>();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string section = null;
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    if (!string.Equals(section, Section, StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    var s = ParseLine(key, val);
                    if (s != null) list.Add(s);
                }
            }
            catch { return null; }
            if (list.Count == 0) return null;
            SetCache(list);
            return list;
        }

        private static SiteScore ParseLine(string key, string val)
        {
            var s = new SiteScore { Key = key, DisplayName = SiteRegistry.LabelOf(key) };

            var f = val.Split(',');
            if (f.Length < 5) return null;

            // ★ 不可用的站点也**必须**把时间戳存下来。
            //   早先不可用一律写成 "-"，读回来 MeasuredAt 就是 DateTime.MinValue，
            //   IsFresh 于是判"过期" —— 表现是**每次启动都重测一遍**
            //   （哪怕 5 个站点里只有 1 个不可用）。现在统一同一套字段：
            //   不可用只是三个耗时留空，时间戳照存。
            if (f[0] == "-") s.Error = "上次实测不可用";
            else
            {
                double a, b, c;
                if (double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a))
                { s.SearchMs = a; s.SearchMeasured = true; }
                if (double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                { s.TocMs = b; s.TocMeasured = true; }
                if (double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c))
                { s.ChapterMs = c; s.ChapterMeasured = true; }
            }

            int n;
            int.TryParse(f[3], out n);
            s.TocCount = n;

            long ticks;
            if (long.TryParse(f[4], out ticks))
            {
                try { s.MeasuredAt = new DateTime(ticks, DateTimeKind.Local); }
                catch { s.MeasuredAt = DateTime.MinValue; }
            }
            if (f.Length >= 6) s.SampleTitle = f[5];
            return s;
        }

        /// <summary>
        /// 固定 6 段：搜索,目录,正文,章数,Ticks时间戳,书名。
        /// 不可用时三项耗时**留空**（不是 0 —— 0 的含义是"瞬间完成"）。
        /// </summary>
        private static string Fmt(SiteScore s)
        {
            // ★ 时间戳必须无损往返：Ticks 是 100ns 单位。
            //   原来写 {4:F0}（四舍五入的毫秒）却读回来当 Ticks 用，
            //   读回的时间比原值早最多 1 秒 → 误判过期 → 每次启动重测。
            return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5}",
                s.SearchMeasured ? Num(s.SearchMs) : "",
                s.TocMeasured ? Num(s.TocMs) : "",
                s.ChapterMeasured ? Num(s.ChapterMs) : "",
                s.TocCount,
                s.MeasuredAt.Ticks.ToString(CultureInfo.InvariantCulture),
                (s.SampleTitle ?? "").Replace(",", " "));
        }

        private static string Num(double d)
        {
            if (double.IsInfinity(d) || double.IsNaN(d)) return "";
            return d.ToString("F0", CultureInfo.InvariantCulture);
        }

        private static string SafeKey(string k)
        {
            if (string.IsNullOrEmpty(k)) return "unknown";
            return k.Replace("[", "_").Replace("]", "_").Replace("=", "_").Replace("\r", "").Replace("\n", "");
        }

        /// <summary>把某个段从 ini 文本里摘掉（用于覆盖写）</summary>
        private static string StripSection(string text, string section)
        {
            var sb = new StringBuilder();
            bool skipping = false;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    skipping = string.Equals(name, section, StringComparison.OrdinalIgnoreCase);
                    if (skipping) continue;
                }
                if (skipping) continue;
                sb.AppendLine(raw);
            }
            return sb.ToString();
        }
    }
}
