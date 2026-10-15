using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using TomatoBiquga;

/// <summary>
/// **联网实测探针**（和 _offlinetests / _e2e 的区别：那两个不联网）。
///
/// 存在的理由：有一类 bug 只在**真发请求**时才会出现，离线断言结构上抓不到。
/// 最典型的一次 —— 为了让 HTTP 状态码落到单独文件，`FetchWithCurl` 在命令行末尾
/// 拼了 `&gt; "codeFile"`；但 `UseShellExecute=false` 时 `ProcessStartInfo` 是直接
/// 启动 curl.exe、不经过 cmd.exe，`&gt;` 于是被当成 curl 的参数 →
/// **每一次请求都返回 curl 退出码 3**，整个程序完全不能用，
/// 而 691 条离线单测**全绿**。
///
/// 所以这个探针专门做"真访问站点"这件事：
///   live cover  &lt;详情页URL&gt;              封面解析 + 真实下载 + 落盘回读
///   live fanqie &lt;book_id&gt;                番茄目录接口
///   live export &lt;下载根目录&gt; &lt;书名&gt; [cache目录]  用真实下载的 txt 导出 EPUB/Markdown
///
/// 用法（在能联网的机器上跑；全部通过返回 0）：
///   _liveprobe.exe cover  "https://www.biquga.com/10_10333/"
///   _liveprobe.exe export "%TEMP%\real-dl" "沧元图" "dist\cache"
/// </summary>
internal static class LiveProbe
{
    private static int _pass, _fail;
    private static readonly List<string> Fails = new List<string>();

    private static void Chk(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine("  [ok] " + name); return; }
        _fail++;
        var line = "[FAIL] " + name + (detail.Length > 0 ? " → " + detail : "");
        Console.WriteLine("  " + line);
        Fails.Add(line);
    }

    private static int Main(string[] args)
    {
        // 走 ConsoleSetup：控制台代码页与 OutputEncoding 一起切。
        // 这个探针是用户最常在 cmd 里直接跑的，原来只设 OutputEncoding → 中文乱码。
        ConsoleSetup.UseUtf8();
        string mode = args.Length > 0 ? args[0] : "cover";
        Console.WriteLine("=== 联网实测探针：" + mode + " ===");
        Console.WriteLine();

        try
        {
            if (mode == "cover") Cover(args);
            else if (mode == "fanqie") Fanqie(args);
            else if (mode == "export") Export(args);
            else if (mode == "typo") Typo(args);
            else if (mode == "typooffline") TypoOffline(args);
            else if (mode == "probe") Probe(args);
            else if (mode == "sites") Sites(args);
            else if (mode == "rate") Rate(args);
            else { Console.WriteLine("未知模式。可用：cover / fanqie / export / typo / typooffline / probe / sites / rate"); return 1; }
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("[FAIL] 探针异常: " + ex.GetType().Name + " " + ex.Message); Console.WriteLine(ex.StackTrace);
        }

        Console.WriteLine();
        Console.WriteLine(string.Format("通过 {0}/{1}", _pass, _pass + _fail));
        foreach (var f in Fails) Console.WriteLine("  " + f);
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ 「自动选最快」实测

    /// <summary>
    /// 跑一遍 SiteRater（就是界面「自动选最快」用的那套），打印排名，
    /// 并把结果写进 站点配置.ini 的 [评分] 段，然后读回验证往返。
    ///
    ///   _liveprobe.exe rate [ini路径]
    /// </summary>
    private static void Rate(string[] args)
    {
        string ini = args.Length > 1 ? args[1] : SiteProfileStore.DefaultPath;
        Console.WriteLine("实测各站点速度（综合分 = 搜索 + 目录 + 正文单章），每站最多 3 个请求");
        Console.WriteLine("ini = " + ini);
        Console.WriteLine();

        List<SiteScore> scores = null;
        try { scores = SiteRater.MeasureAll(delegate (string m) { Console.WriteLine(m); }); }
        catch (Exception ex) { Console.WriteLine("[FAIL] 实测异常：" + ex.Message); _fail++; return; }

        Chk("实测：每个注册站点都有一条结果",
            scores != null && scores.Count == SiteRegistry.Count,
            scores == null ? "null" : scores.Count + " / " + SiteRegistry.Count);

        Console.WriteLine();
        Console.WriteLine("排名（越快越靠前）：");
        int n = 0;
        foreach (var s in SiteRater.Rank(scores))
        {
            n++;
            Console.WriteLine(string.Format("  {0}. {1}", n, s));
            if (s.TocCount > 0)
                Console.WriteLine(string.Format("     实测书：《{0}》，目录 {1} 章", s.SampleTitle, s.TocCount));
        }

        string fast = SiteRater.FastestKey(scores);
        Console.WriteLine();
        Console.WriteLine("最快站点 = " + (fast == null ? "（无可用站点 → 界面会回退到笔趣阁 PC版）" : SiteRegistry.LabelOf(fast)));
        Chk("实测：至少有一个可用站点", fast != null || scores.Count == 0);
        if (fast != null)
            Chk("实测：最快站点确实在注册表里", SiteRegistry.ByKey(fast) != null);

        // 写盘 + 读回
        SiteRater.Save(scores, ini);
        var back = SiteRater.Load(ini);
        Chk("实测：评分能写进 ini 并读回", back != null && back.Count == scores.Count,
            back == null ? "读回 null" : back.Count + " / " + scores.Count);
        Chk("实测：读回后仍判定为新鲜（24 小时内）", SiteRater.IsFresh(back));
        if (back != null)
        {
            string fast2 = SiteRater.FastestKey(back);
            Chk("实测：读回后最快站点一致", string.Equals(fast, fast2, StringComparison.Ordinal),
                fast + " vs " + fast2);
        }
    }

    // ------------------------------------------------------------ 规则表站点全链路实测
    /// <summary>
    /// 对**每个规则表站点**跑一遍真实全链路：搜索 → 载入目录 → 抓第一章正文。
    ///
    /// 这是"规则表到底对不对"的唯一权威验证 —— 离线测试只能证明正则对着
    /// **存档的 HTML** 能跑，证明不了站点今天还长这样。
    ///
    /// 顺便打印每个维度的耗时，这就是"自动选最快站点"评分的原始数据。
    ///
    ///   _liveprobe.exe sites [关键词]     不带关键词就用规则里的 KnownTitles[0]
    /// </summary>
    private static void Sites(string[] args)
    {
        string forceKeyword = args.Length > 1 ? args[1] : null;
        var rules = SiteRules.All();
        Console.WriteLine(string.Format("规则表站点全链路实测（{0} 个站点）：搜索 → 目录 → 正文", rules.Count));
        Console.WriteLine();

        foreach (var rule in rules)
        {
            string kw = forceKeyword;
            if (string.IsNullOrEmpty(kw) && rule.KnownTitles != null && rule.KnownTitles.Length > 0)
                kw = rule.KnownTitles[0];

            Console.WriteLine("【" + rule.DisplayName + "】 " + rule.Key + "   关键词：" + kw);
            var site = new RuleSite(rule);
            var sw = new System.Diagnostics.Stopwatch();

            // --- 探活 ---
            string reason = null;
            try { reason = site.Probe(); } catch (Exception ex) { reason = ex.Message; }
            if (reason != null)
            {
                Console.WriteLine("    [FAIL] 站点不可用：" + reason);
                _fail++; Fails.Add(rule.Key + " 不可用：" + reason);
                Console.WriteLine();
                continue;
            }

            // --- 搜索 ---
            List<BookInfo> found = null;
            sw.Restart();
            try { found = site.Search(kw, null); }
            catch (Exception ex) { Console.WriteLine("    [FAIL] 搜索异常：" + ex.Message); }
            sw.Stop();
            long tSearch = sw.ElapsedMilliseconds;
            Chk(rule.Key + "：搜索有结果", found != null && found.Count > 0,
                found == null ? "null" : found.Count + " 条");
            if (found == null || found.Count == 0) { Console.WriteLine(); continue; }
            Console.WriteLine(string.Format("    搜索 {0} ms，{1} 条，第一条：《{2}》",
                tSearch, found.Count, found[0].Title));
            Chk(rule.Key + "：搜索拿到了书名", !string.IsNullOrEmpty(found[0].Title)
                && found[0].Title != "（载入后取书名）");

            // --- 载入目录 ---
            BookInfo book = null;
            sw.Restart();
            try { book = site.LoadBook(found[0], null); }
            catch (Exception ex) { Console.WriteLine("    [FAIL] 载入目录异常：" + ex.Message); }
            sw.Stop();
            long tToc = sw.ElapsedMilliseconds;
            Chk(rule.Key + "：目录非空", book != null && book.Chapters != null && book.Chapters.Count > 0,
                book == null ? "null" : (book.Chapters == null ? "null" : book.Chapters.Count + " 章"));
            if (book == null || book.Chapters == null || book.Chapters.Count == 0) { Console.WriteLine(); continue; }
            Console.WriteLine(string.Format("    目录 {0} ms，{1} 章，范围：{2} → {3}",
                tToc, book.Chapters.Count, book.Chapters[0].Title, book.Chapters[book.Chapters.Count - 1].Title));
            Console.WriteLine("    前 3 章：" + book.Chapters[0].Id + "=《" + book.Chapters[0].Title + "》 "
                + (book.Chapters.Count > 1 ? book.Chapters[1].Id + "=《" + book.Chapters[1].Title + "》 " : "")
                + (book.Chapters.Count > 2 ? book.Chapters[2].Id + "=《" + book.Chapters[2].Title + "》" : ""));
            Console.WriteLine(string.Format("    书名《{0}》 作者：{1}", book.Title, book.Author));
            Chk(rule.Key + "：目录章节标题不像 id 兜底",
                book.Chapters[0].Title != "第 1 章");

            // --- 正文（取中间一章，避开卷首/公告） ---
            var mid = book.Chapters[book.Chapters.Count / 2];
            sw.Restart();
            string text = null;
            try { text = site.LoadChapter(book, mid, null); }
            catch (Exception ex) { Console.WriteLine("    [FAIL] 抓正文异常：" + ex.Message); }
            sw.Stop();
            long tCh = sw.ElapsedMilliseconds;
            int cjk = 0;
            if (!string.IsNullOrEmpty(text))
                foreach (var c in text) if (c >= 0x4e00 && c <= 0x9fff) cjk++;
            Chk(rule.Key + "：正文字数够（>300 汉字）", cjk > 300, cjk + " 汉字");
            Console.WriteLine(string.Format("    正文 {0} ms，{1} 汉字（第 {2} 章 {3}）",
                tCh, cjk, book.Chapters.Count / 2 + 1, mid.Title));
            if (!string.IsNullOrEmpty(text))
            {
                var head = text.Length > 90 ? text.Substring(0, 90) : text;
                Console.WriteLine("    正文开头：" + head.Replace("\n", " "));
                Chk(rule.Key + "：正文没有残留 HTML 标签",
                    text.IndexOf("<br", StringComparison.OrdinalIgnoreCase) < 0
                    && text.IndexOf("&nbsp;", StringComparison.Ordinal) < 0
                    && text.IndexOf("<div", StringComparison.OrdinalIgnoreCase) < 0);
            }

            Console.WriteLine(string.Format("    ── 综合分（越小越快）：{0} ms  = 搜索 {1} + 目录 {2} + 正文 {3}",
                tSearch + tToc + tCh, tSearch, tToc, tCh));
            Console.WriteLine();
        }
    }

    private static void Probe(string[] args)
    {
        // 站点列表来自注册表（以前这里是手写的三个），加站点不用改这里
        var sites = new List<ISite>();
        foreach (var d in SiteRegistry.All) sites.Add(d.Factory());
        Console.WriteLine(string.Format("对 {0} 个站点各探活一次（各发 1 个请求）", sites.Count));
        Console.WriteLine();
        foreach (var s in sites)
        {
            var p = s as IProbeable;
            Console.WriteLine("【" + s.Name + "】");
            if (p == null) { Console.WriteLine("    （不支持探活）"); continue; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string reason = null;
            try { reason = p.Probe(); }
            catch (Exception ex) { reason = ex.Message; }
            sw.Stop();

            if (reason == null)
            {
                Console.WriteLine(string.Format("    可用（{0} ms）", sw.ElapsedMilliseconds));
                _pass++;
            }
            else
            {
                Console.WriteLine("    不可用：" + reason);
                _pass++;   // "探活本身工作正常"就算通过，站点不通不是探针的失败
            }
        }
        Console.WriteLine();
        Console.WriteLine("说明：探活能正确区分「站点通」与「不通且原因是什么」，");
        Console.WriteLine("      站点本身不通不算探针失败。");
    }

    // ------------------------------------------------------------ 错字检测（离线双源）

    /// <summary>
    /// 用**目录缓存里的移动版正文**（PrefetchText 抓到的那份）当对照源，
    /// 和 PC 版现场抓的正文比对。
    ///
    /// 为什么要有这个模式：移动版 (`m.biquga.com`) 在部分网络下不可用，
    /// 那样就没法现场抓第二份。而移动版缓存文件里正好存着移动版的正文
    /// （离线模式预抓的），它是**真正来自另一个源**的内容 —— 拿它做对照完全成立。
    /// 这样就有一条"不依赖移动版可用"的双源比对验证路径。
    ///
    /// 用法: typooffline <下载根目录> <书名> <书dir> <cache目录> <cache site key> [章数]
    ///   例: typooffline "%TEMP%\real-dl" 沧元图 10_10333 "dist\cache" biquga-m 5
    /// </summary>
    private static void TypoOffline(string[] args)
    {
        if (args.Length < 6)
        {
            Console.WriteLine("用法: typooffline <下载根目录> <书名> <书dir> <cache目录> <cache站点key> [章数=5]");
            return;
        }
        string root = args[1], title = args[2], dirKey = args[3], cacheSrc = args[4], otherKey = args[5];
        int limit = args.Length > 6 ? int.Parse(args[6]) : 5;

        // 先把 cache 复制到探针同目录（DirCache 固定读 exe 同目录\cache）。
        // 注意：不能用 File.Copy —— 缓存可能正被另一个进程写，
        // File.Copy 不支持共享读，会直接抛"文件正由另一进程使用"。
        var to = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(cacheSrc, otherKey + "_" + dirKey + ".json"))
            File.WriteAllText(Path.Combine(to, Path.GetFileName(f)),
                DirCache.ReadAllTextShared(f), new UTF8Encoding(false));

        var otherBook = DirCache.Load(otherKey, dirKey);
        Chk("对照源缓存载入（" + otherKey + "）", otherBook != null && otherBook.Chapters.Count > 0,
            otherBook == null ? "null" : otherBook.Chapters.Count.ToString());
        if (otherBook == null) return;
        Console.WriteLine("    对照源：《" + otherBook.Title + "》 " + otherBook.Chapters.Count + " 章");

        // 主源：PC 版现场抓
        var pc = new BiqugaSite();
        var stub = new BookInfo { Site = "biquga", Dir = "/" + dirKey, Url = BiqugaSite.Origin + "/" + dirKey + "/" };
        var book = pc.LoadBook(stub, m => Console.WriteLine("    [目录] " + m));
        Chk("主源目录载入", book != null && book.Chapters.Count > 0);
        if (book == null) return;

        // 正文：PC 版按需抓（这里只抓前 N 章，所以不触发整本遍历）
        Console.WriteLine();
        Console.WriteLine("开始双源比对（主源=PC 现场抓，对照源=" + otherKey + " 缓存正文）");
        var result = new TypoFinder.Result { Title = book.Title, OtherSource = otherKey + "（缓存正文）" };
        int n = 0, done = 0;
        foreach (var c in book.Chapters)
        {
            if (c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
            if (n >= limit) break;
            if (done >= limit) break;

            // 对照源里找同一个 cid
            ChapterInfo oc = null;
            foreach (var x in otherBook.Chapters) if (x.Id == c.Id) { oc = x; break; }
            if (oc == null) { Console.WriteLine("    " + c.Title + " —— 对照源没有这个 cid，跳过"); continue; }
            n++;

            string primary = null, other = null;
            try { primary = pc.LoadChapter(book, c, null); }
            catch (Exception ex) { Console.WriteLine("    主源抓取失败: " + ex.Message); }
            other = oc.Text;

            if (string.IsNullOrEmpty(primary) || string.IsNullOrEmpty(other))
            {
                result.ComparedChapters++;
                result.SkippedChapters++;
                Console.WriteLine("    " + c.Title + " —— 有一边没内容，跳过");
                continue;
            }

            done++;
            result.ComparedChapters++;
            var diffs = TypoFinder.CompareChapter(c.Title, primary, other);
            if (diffs.Count == 0)
            {
                result.IdenticalChapters++;
                Console.WriteLine("    " + c.Title + " —— 一致");
            }
            else
            {
                result.Diffs.AddRange(diffs);
                result.PerChapter[c.Title] = diffs.Count;
                Console.WriteLine("    " + c.Title + " —— " + diffs.Count + " 处差异");
                for (int k = 0; k < diffs.Count && k < 4; k++)
                {
                    var d = diffs[k];
                    Console.WriteLine(string.Format("          @{0}  PC[{1}]  移动[{2}]   上下文 {3}",
                        d.Position, d.Primary, d.Other, d.Context));
                }
                if (diffs.Count > 4) Console.WriteLine("          …还有 " + (diffs.Count - 4) + " 处");
            }
        }

        Console.WriteLine();
        Console.WriteLine(result.Summary());
        Chk("双源比对跑通（至少比了 1 章）", result.ComparedChapters > 0, result.ComparedChapters.ToString());

        string bookDir, txtPath;
        DownloadRunner.ResolvePaths(root, title, out bookDir, out txtPath);
        var rep = Path.Combine(bookDir, "错字检测报告.txt");
        File.WriteAllText(rep, TypoFinder.BuildReport(book, result, txtPath, result.OtherSource), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(bookDir, "错字检测报告.csv"), TypoFinder.BuildCsv(result), new UTF8Encoding(true));
        Console.WriteLine("报告：" + rep);

        if (result.ComparedChapters > 0)
            Console.WriteLine(string.Format("一致率：{0:P0}（{1}/{2} 章）",
                (double)result.IdenticalChapters / result.ComparedChapters,
                result.IdenticalChapters, result.ComparedChapters));
    }

    // ------------------------------------------------------------ 错字检测（双源比对）

    private static void Typo(string[] args)
    {
        // 用法: typo <下载根目录> <书名> <对照站点key> [比对的章数] [cache目录]
        if (args.Length < 4)
        {
            Console.WriteLine("用法: typo <下载根目录> <书名> <对照站点> [章数=10] [cache目录]");
            Console.WriteLine("  对照站点：biquga（PC）或 biquga-m（移动版）");
            return;
        }
        string root = args[1], title = args[2], otherKey = args[3];
        int limit = args.Length > 4 ? int.Parse(args[4]) : 10;
        string cacheSrc = args.Length > 5 ? args[5] : null;

        string bookDir, txtPath;
        DownloadRunner.ResolvePaths(root, title, out bookDir, out txtPath);
        Console.WriteLine("主源 txt：" + txtPath);
        if (!File.Exists(txtPath)) { Chk("找到主源 txt", false, txtPath); return; }
        Chk("找到主源 txt", true);

        // 目录缓存
        var book = DirCache.Load("biquga", "10_10333");
        if (book == null && cacheSrc != null && Directory.Exists(cacheSrc))
        {
            var to = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(cacheSrc, "biquga_10_10333.json"))
                File.WriteAllText(Path.Combine(to, Path.GetFileName(f)),
                    DirCache.ReadAllTextShared(f), new UTF8Encoding(false));
            book = DirCache.Load("biquga", "10_10333");
        }
        Chk("目录缓存载入", book != null && book.Chapters.Count > 0);
        if (book == null) return;

        // 主源正文：走界面同一份解析
        var mi = typeof(MainForm).GetMethod("ParseTxtIntoChapters", BindingFlags.NonPublic | BindingFlags.Static);
        Chk("找到 MainForm.ParseTxtIntoChapters", mi != null);
        if (mi == null) return;
        var primary = (List<ChapterInfo>)mi.Invoke(null, new object[] { txtPath, book });
        Chk("主源解析出章节", primary != null && primary.Count > 0,
            primary == null ? "null" : primary.Count.ToString());
        if (primary == null || primary.Count == 0) return;
        Console.WriteLine("    主源 " + primary.Count + " 章有正文");

        // 对照源：同一个站点类型，重新抓这些章
        var other = otherKey == "biquga-m"
            ? (ISite)new BiqugaMobileSite { Workers = 6 }
            : (ISite)new BiqugaSite();

        Console.WriteLine();
        Console.WriteLine("对照源：" + other.Name + "（重新抓取，只抓前 " + limit + " 章）");
        var result = new TypoFinder.Result { Title = book.Title, OtherSource = other.Name };
        int n = 0;
        foreach (var c in primary)
        {
            if (n >= limit) break;
            n++;
            // 对照源用的是同一个 cid（同一本书 id 一样），所以可以直接按 Id 抓
            string text = null;
            try { text = other.LoadChapter(book, c, null); }
            catch (Exception ex) { Console.WriteLine("    [" + n + "] " + c.Title + " 抓取失败：" + ex.Message); }

            result.ComparedChapters++;
            if (string.IsNullOrEmpty(text))
            {
                result.SkippedChapters++;
                Console.WriteLine("    [" + n + "] " + c.Title + " —— 对照源没有内容，跳过");
                continue;
            }

            var diffs = TypoFinder.CompareChapter(c.Title, c.Text, text);
            if (diffs.Count == 0)
            {
                result.IdenticalChapters++;
                Console.WriteLine("    [" + n + "] " + c.Title + " —— 一致");
            }
            else
            {
                result.Diffs.AddRange(diffs);
                result.PerChapter[c.Title] = diffs.Count;
                Console.WriteLine("    [" + n + "] " + c.Title + " —— " + diffs.Count + " 处差异");
                for (int k = 0; k < diffs.Count && k < 3; k++)
                {
                    var d = diffs[k];
                    Console.WriteLine(string.Format("          @{0} 主源[{1}] 对照[{2}]",
                        d.Position, d.Primary, d.Other));
                }
                if (diffs.Count > 3) Console.WriteLine("          …还有 " + (diffs.Count - 3) + " 处");
            }
        }

        Console.WriteLine();
        Console.WriteLine(result.Summary());
        Chk("双源比对跑通（至少比了 1 章）", result.ComparedChapters > 0, result.ComparedChapters.ToString());

        // 写报告
        var rep = Path.Combine(bookDir, "错字检测报告.txt");
        File.WriteAllText(rep, TypoFinder.BuildReport(book, result, txtPath, other.Name), new UTF8Encoding(true));
        var csv = Path.Combine(bookDir, "错字检测报告.csv");
        File.WriteAllText(csv, TypoFinder.BuildCsv(result), new UTF8Encoding(true));
        Console.WriteLine("报告：" + rep);
        Console.WriteLine("CSV ：" + csv);

        // 结果解读：一致率太低通常说明"这两个源差别很大"，而不是"错字多"
        if (result.ComparedChapters > 0)
        {
            double sameRatio = (double)result.IdenticalChapters / result.ComparedChapters;
            Console.WriteLine(string.Format("一致率：{0:P0}（{1}/{2} 章）",
                sameRatio, result.IdenticalChapters, result.ComparedChapters));
            if (sameRatio < 0.3)
                Console.WriteLine("提示：一致率偏低，多半是两个源本身的排版/版本差异较大，不一定是错字多。");
        }
    }

    // ------------------------------------------------------------ 封面

    private static void Cover(string[] args)
    {
        string url = args.Length > 1 ? args[1] : BiqugaSite.Origin + "/10_10333/";

        Console.WriteLine("[1] 抓详情页");
        var html = Http.Get(url, BiqugaSite.Origin + "/");
        Console.WriteLine("    页面 " + html.Length.ToString("N0") + " 字节");
        Chk("详情页抓取成功（说明 curl 命令行是对的）", html.Length > 5000, html.Length.ToString());

        Console.WriteLine();
        Console.WriteLine("[2] 封面选择器命中情况");
        // 这三个就是 CoverFetcher 调用方实际用的字段，按优先级
        var probes = new[]
        {
            new[] { "og:image", "og:image\" content=\"([^\"]*)\"" },
            new[] { "og:novel:image", "og:novel:image\" content=\"([^\"]*)\"" },
            new[] { "<img cover|image|files>", "<img[^>]+(?:data-src|src)=\"([^\"]*(?:cover|image|files)[^\"]*)\"" },
        };
        string found = null, foundBy = null;
        foreach (var p in probes)
        {
            var m = Regex.Match(html, p[1]);
            Console.WriteLine(string.Format("    {0,-24} {1}", p[0], m.Success ? "命中 → " + m.Groups[1].Value : "（没命中）"));
            if (m.Success && found == null) { found = m.Groups[1].Value; foundBy = p[0]; }
        }
        Chk("至少一个封面选择器命中", found != null);
        if (found == null) return;

        Console.WriteLine();
        Console.WriteLine("[3] 封面地址补全 + 真实下载");
        var abs = CoverFetcher.Absolutize(found, url);
        Console.WriteLine("    命中字段: " + foundBy);
        Console.WriteLine("    绝对地址: " + abs);
        Chk("补全成绝对地址", abs.StartsWith("http"));

        var bytes = Http.GetBytes(abs, url);
        Chk("封面下载成功（防盗链没拦住）", bytes != null && bytes.Length > 500,
            bytes == null ? "null" : bytes.Length.ToString());
        if (bytes == null) return;

        var ext = CoverFetcher.SniffExt(bytes, abs);
        Chk("按魔术字节识别出格式", ext != null, ext ?? "null");
        Console.WriteLine("    " + bytes.Length.ToString("N0") + " 字节  " + ext + "  " + CoverFetcher.MimeOf(ext));

        Console.WriteLine();
        Console.WriteLine("[4] 落盘 + 回读");
        var dir = Path.Combine(Path.GetTempPath(), "liveprobe-cover-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(dir);
        try
        {
            var p = CoverFetcher.Ensure(abs, dir, url, m => Console.WriteLine("    [log] " + m));
            Chk("封面已落地", p != null && File.Exists(p));
            string e2;
            var rb = CoverFetcher.Read(dir, out e2);
            Chk("回读字节数一致", rb != null && rb.Length == bytes.Length,
                rb == null ? "null" : rb.Length + " vs " + bytes.Length);
            Chk("重复调用不重复下载（第二次直接命中）", CoverFetcher.Ensure(abs, dir, url, null) != null);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------ 番茄

    private static void Fanqie(string[] args)
    {
        string id = args.Length > 1 ? args[1] : "7256784068786785336";
        var site = new FanqieSite();
        // 强制忽略本地目录缓存：缓存可能由旧版本代码写入（里面没有封面/字数字段），
        // 走缓存会把"字段到底能不能解析出来"这件事盖住 —— 这个探针就栽过一次。
        site.ForceRefresh = true;
        var hits = site.Search(id, null);
        Chk("番茄 Search 解析出 book_id", hits.Count > 0 && !string.IsNullOrEmpty(hits[0].BookId));
        if (hits.Count == 0) return;

        var book = site.LoadBook(hits[0], m => Console.WriteLine("    [log] " + m));
        Chk("番茄目录载入成功", book != null && book.Chapters.Count > 0,
            book == null ? "null" : book.Chapters.Count.ToString());
        if (book == null) return;
        Console.WriteLine("    《" + book.Title + "》 " + book.Author + "  " + book.Chapters.Count + " 条目");

        int vols = 0, chs = 0;
        foreach (var c in book.Chapters) { if (c.IsVolume) vols++; else chs++; }
        Chk("识别出了分卷行（用来做 EPUB 嵌套目录）", vols > 0, "vols=" + vols);
        Console.WriteLine("    正文章 " + chs + "，分卷行 " + vols);
        Chk("封面地址已解析", !string.IsNullOrEmpty(book.CoverUrl), "[" + book.CoverUrl + "]");
        Chk("字数已解析（站点标称）", book.WordCount > 0, book.WordCount.ToString());

        // 正文：番茄有风控，失败是预期内的，不算测试失败，但要如实报出来
        var target = book.Chapters.Find(c => !c.IsVolume && !string.IsNullOrEmpty(c.Id));
        if (target != null)
        {
            Console.WriteLine();
            Console.WriteLine("[正文] 尝试第一章：" + target.Title);
            try
            {
                var t = site.LoadChapter(book, target, null);
                Console.WriteLine("    正文 " + (t == null ? 0 : t.Length) + " 字");
                Console.WriteLine("    （番茄网页版有风控，拿到 0 字属预期，不算失败）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("    风控拦截：" + ex.Message);
                Console.WriteLine("    （预期内。番茄正文建议配合第三方核心使用）");
            }
        }
    }

    // ------------------------------------------------------------ 真实产物导出

    private static void Export(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("用法: export <下载根目录> <书名> [cache目录]"); return; }
        string root = args[1], title = args[2];

        string bookDir, txtPath;
        DownloadRunner.ResolvePaths(root, title, out bookDir, out txtPath);
        if (!File.Exists(txtPath)) { Chk("找到已下载的 txt", false, txtPath); return; }
        Chk("找到已下载的 txt", true);
        Console.WriteLine("    " + txtPath + "  " + new FileInfo(txtPath).Length.ToString("N0") + " 字节");

        // 封面
        string ext;
        var cover = CoverFetcher.Read(bookDir, out ext);
        Console.WriteLine("    封面: " + (cover == null ? "(无)" : cover.Length.ToString("N0") + " 字节 " + ext));

        // 目录缓存：DirCache 固定在"exe 同目录\cache"，探针 exe 可能在别处，
        // 所以允许显式传一个 cache 目录并复制过来
        var book = DirCache.Load("biquga", "10_10333");
        if (book == null && args.Length > 3 && Directory.Exists(args[3]))
        {
            var to = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            Directory.CreateDirectory(to);
            // 用 ReadAllTextShared 而不是 File.Copy：缓存可能正被另一个进程写，
            // File.Copy 不支持共享读，会抛"文件正由另一进程使用"。
            foreach (var f in Directory.GetFiles(args[3], "biquga_10_10333.json"))
                File.WriteAllText(Path.Combine(to, Path.GetFileName(f)),
                    DirCache.ReadAllTextShared(f), new UTF8Encoding(false));
            book = DirCache.Load("biquga", "10_10333");
        }
        Chk("目录缓存载入成功", book != null && book.Chapters.Count > 0,
            book == null ? "null（试试第 4 个参数传 cache 目录）" : book.Chapters.Count.ToString());
        if (book == null) return;
        Console.WriteLine("    《" + book.Title + "》 " + book.Author + "  " + book.Chapters.Count + " 章");

        // 用界面自己的解析方法（反射），保证测的是真正跑的那段代码
        var mi = typeof(MainForm).GetMethod("ParseTxtIntoChapters", BindingFlags.NonPublic | BindingFlags.Static);
        Chk("找到 MainForm.ParseTxtIntoChapters", mi != null);
        if (mi == null) return;

        var chapters = (List<ChapterInfo>)mi.Invoke(null, new object[] { txtPath, book });
        Chk("从真实 txt 解析出章节", chapters != null && chapters.Count > 0,
            chapters == null ? "null" : chapters.Count.ToString());
        if (chapters == null || chapters.Count == 0) return;

        long body = 0;
        foreach (var c in chapters) body += c.Text == null ? 0 : c.Text.Length;
        Console.WriteLine("    解析出 " + chapters.Count + " 章，正文合计 " + body.ToString("N0") + " 字");
        Chk("正文非空", body > 500, body.ToString());

        // EPUB（带真实封面）
        Console.WriteLine();
        Console.WriteLine("[EPUB] 用真实正文 + 真实封面导出");
        var epub = Path.Combine(bookDir, "liveprobe.epub");
        EpubWriter.Write(epub, book, chapters, cover, ext);
        var esz = new FileInfo(epub).Length;
        Console.WriteLine("    " + esz.ToString("N0") + " 字节");
        Chk("EPUB 生成且体积合理", esz > 3000, esz.ToString());

        using (var zip = ZipFile.OpenRead(epub))
        {
            Chk("mimetype 是第一个条目且不压缩", zip.Entries[0].FullName == "mimetype");
            var mt = zip.Entries[0];
            Chk("mimetype 内容正确", ReadEntry(zip, "mimetype") == "application/epub+zip");
            if (cover != null)
            {
                var img = zip.GetEntry("OEBPS/images/cover" + (ext ?? ".jpg"));
                Chk("真实封面进了包", img != null && img.Length > 500,
                    img == null ? "找不到" : img.Length.ToString());
            }
            int parsed = 0, bad = 0;
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.EndsWith(".xhtml") && !e.FullName.EndsWith(".opf")) continue;
                try { var d = new XmlDocument(); d.LoadXml(ReadEntry(zip, e.FullName)); parsed++; }
                catch { bad++; }
            }
            Chk("全部 XHTML/OPF 可被真 XML 解析（" + parsed + " 个）", bad == 0 && parsed > 0,
                "坏 " + bad);
            var nav = ReadEntry(zip, "OEBPS/nav.xhtml");
            Chk("nav 有章节链接", CountOf(nav, "<li><a href=") == chapters.Count,
                CountOf(nav, "<li><a href=") + " vs " + chapters.Count);
        }

        // 繁体
        Console.WriteLine();
        Console.WriteLine("[繁体] 同一批真实正文转繁体后导出");
        var trad = new List<ChapterInfo>();
        foreach (var c in chapters)
            trad.Add(new ChapterInfo
            {
                Id = c.Id, Order = c.Order,
                Title = ZhConvert.ToTraditional(c.Title),
                Text = ZhConvert.ToTraditional(c.Text),
            });
        var epubT = Path.Combine(bookDir, "liveprobe_繁体.epub");
        EpubWriter.Write(epubT, book, trad, cover, ext);
        Chk("繁体 EPUB 生成", File.Exists(epubT) && new FileInfo(epubT).Length > 3000);
        var sample = trad[0].Title + " " + (trad[0].Text ?? "");
        Console.WriteLine("    样张: " + sample.Substring(0, Math.Min(70, sample.Length)).Replace("\n", " "));
        Chk("繁体结果确实变了（含繁体专有字）", ZhConvert.LooksTraditional(sample));
        // 简→繁→简 必须回到原文（真实长文本上的往返）
        var back = ZhConvert.ToSimplified(trad[0].Text ?? "");
        Chk("真实正文往返无损", back == (chapters[0].Text ?? ""));

        // Markdown：注意 `## 目录` 也是一个 H2，所以用**锚点**数章数，别用 H2 数
        Console.WriteLine();
        Console.WriteLine("[Markdown] 导出真实正文");
        var md = Path.Combine(bookDir, "liveprobe.md");
        MarkdownWriter.Write(md, book, chapters, true, null);
        var mdText = File.ReadAllText(md, Encoding.UTF8);
        Chk("Markdown 生成", new FileInfo(md).Length > 3000);
        Chk("有 YAML 头", mdText.StartsWith("---\n"));
        Chk("章节锚点数 == 章数（用锚点数，不用 H2 数：`## 目录` 也是 H2）",
            CountOf(mdText, "<a id=\"ch") == chapters.Count,
            CountOf(mdText, "<a id=\"ch") + " vs " + chapters.Count);
        Chk("YAML 里章节数正确", mdText.Contains("chapters: " + chapters.Count + "\n"));
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e == null) return "";
        using (var s = e.Open())
        using (var r = new StreamReader(s, Encoding.UTF8))
            return r.ReadToEnd();
    }

    private static int CountOf(string hay, string needle)
    {
        if (string.IsNullOrEmpty(hay) || string.IsNullOrEmpty(needle)) return 0;
        int n = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
