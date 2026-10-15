using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using TomatoBiquga;

/// <summary>
/// 下载耗时基准：以**真实并发**抓 N 章，量出"下载整本要多久"。
///
/// 为什么不能拿单章耗时 × 章数：那忽略了并发。真实下载是 6~8 路并发，
/// 所以必须用项目自己的 DownloadRunner（和界面走同一条代码路径）来量。
/// </summary>
internal static class RateBench
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string key = args.Length > 0 ? args[0] : "xbiquwk";
        int n = args.Length > 1 ? int.Parse(args[1]) : 12;
        int workers = args.Length > 2 ? int.Parse(args[2]) : 6;

        var rule = SiteRules.ByKey(key);
        if (rule == null) { Console.WriteLine("未知站点: " + key); return 1; }
        string kw = rule.KnownTitles[0];

        Console.WriteLine("=== 下载速率基准 ===");
        Console.WriteLine("站点   : " + rule.DisplayName + " (" + key + ")");
        Console.WriteLine("关键词 : " + kw);
        Console.WriteLine("取样   : " + n + " 章，并发 " + workers);
        Console.WriteLine();

        var site = new RuleSite(rule);

        // 1) 搜索 + 目录
        var sw = Stopwatch.StartNew();
        var found = site.Search(kw, null);
        sw.Stop();
        long tSearch = sw.ElapsedMilliseconds;
        if (found == null || found.Count == 0) { Console.WriteLine("搜索无结果"); return 1; }

        sw.Restart();
        var book = site.LoadBook(found[0], null);
        sw.Stop();
        long tToc = sw.ElapsedMilliseconds;
        int total = book.Chapters.Count;
        Console.WriteLine(string.Format("搜索      : {0,7} ms  ({1} 条)", tSearch, found.Count));
        Console.WriteLine(string.Format("载入目录  : {0,7} ms  ({1} 章)   ← 全本目录", tToc, total));
        Console.WriteLine();

        // 2) 取中间一段章节（避开卷首公告/卷尾）
        int from = total / 3;
        var slice = new List<ChapterInfo>();
        for (int i = from; i < Math.Min(from + n, total); i++) slice.Add(book.Chapters[i]);

        // 3) 用项目自己的 DownloadRunner 并发抓（与界面同一条代码路径）
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ratebench-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tmp);

        var runner = new DownloadRunner
        {
            Site = site,
            Book = book,
            Chapters = slice,
            RootDir = tmp,
            Workers = workers,
            Log = delegate { },
        };

        sw.Restart();
        runner.Run();
        sw.Stop();
        long ms = sw.ElapsedMilliseconds;

        double perCh = (double)ms / slice.Count;
        Console.WriteLine(string.Format("并发抓取  : {0,7} ms / {1} 章   并发={2}", ms, slice.Count, workers));
        Console.WriteLine(string.Format("            {0:F0} ms/章   成功率 {1}/{2}", perCh, runner.Ok, slice.Count));
        Console.WriteLine();

        // 4) 折算全本
        double bodyMin = perCh * total / 1000.0 / 60.0;
        double tocMin = tToc / 1000.0 / 60.0;
        Console.WriteLine("── 折算（用本目录的 " + total + " 章）──");
        Console.WriteLine(string.Format("  载入目录      : {0,7:F1} 分", tocMin));
        Console.WriteLine(string.Format("  下载全本正文  : {0,7:F1} 分   （{1:F0} ms/章 × {2} 章，并发 {3}）",
            bodyMin, perCh, total, workers));
        Console.WriteLine(string.Format("  合计          : {0,7:F1} 分", tocMin + bodyMin));
        Console.WriteLine();
        Console.WriteLine("  注：换算成 H:MM → " + TimeSpan.FromMinutes(tocMin + bodyMin).ToString(@"h\:mm"));

        try { System.IO.Directory.Delete(tmp, true); } catch { }
        return 0;
    }
}
