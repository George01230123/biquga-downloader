using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using TomatoBiquga;

/// <summary>
/// 风控阈值实测：把并发从低到高拉，看什么时候开始被限流。
///
/// 目的：回答"SoNovel 用 50 并发为什么不触发风控"。
/// 要区分两件事：
///   ① 站点在多少并发/多高频率下会开始限流（这是站点属性）
///   ② 被限流后工具怎么反应（这是工具属性 —— 本工具会降并发+退避）
///
/// 用法：_rattest.exe &lt;siteKey&gt; [起始并发] [结束并发]
/// </summary>
internal static class RatTest
{
    private static int Main(string[] args)
    {
        // 控制台代码页 + OutputEncoding 一起切（只设后者在 cmd 里是乱码）。
        ConsoleSetup.UseUtf8();
        string key = args.Length > 0 ? args[0] : "xbiquwk";
        int lo = args.Length > 1 ? int.Parse(args[1]) : 4;
        int hi = args.Length > 2 ? int.Parse(args[2]) : 24;

        var desc = SiteRegistry.ByKey(key);
        var rule = desc.Rule as SiteRule;
        // 规则表站点用自己声明的基准书；内置站点（biquga）没有规则表，
        // 用实测能搜到的固定书名。★ 不能对 biquga 调 rule.KnownTitles —— rule 是 null。
        string kw = rule != null && rule.KnownTitles.Length > 0 ? rule.KnownTitles[0] : "剑来";

        Console.WriteLine("=== 风控阈值实测 ===");
        Console.WriteLine("站点: " + desc.DisplayName + "   关键词: " + kw + "   每档 25 章");
        Console.WriteLine();

        // 目录只取一次
        ISite site0 = desc.Factory();
        var found = site0.Search(kw, null);
        if (found == null || found.Count == 0) { Console.WriteLine("搜索无结果（可能被限流，稍后再试）"); return 1; }
        var book = site0.LoadBook(found[0], null);
        Console.WriteLine("目录: " + book.Chapters.Count + " 章  《" + book.Title + "》");
        Console.WriteLine();

        // ★ 每档必须换一批**没抓过的**章节。
        //   否则第 2 档开始会命中第一次留下的正文缓存
        //   （日志会写"25/25 章直接复用了目录遍历时已抓到的正文"），
        //   于是 25 章 39 ms —— 看着像"并发很高也不被限流"，其实是没发请求。
        //   我踩过一次，所以这里显式按档位偏移取不同的章节区间。
        Console.WriteLine("  {0,-6} {1,-11} {2,-11} {3,-10} {4}", "并发", "墙钟", "每章", "限流计数", "成功/总数");
        Console.WriteLine("  " + new string('-', 56));

        for (int w = lo, step = 0; w <= hi; w += 4, step++)
        {
            int off = book.Chapters.Count / 3 + step * 30;      // 每档往后挪 30 章
            if (off + 25 > book.Chapters.Count) off = book.Chapters.Count / 3;
            var batch = new List<ChapterInfo>();
            for (int i = off; i < Math.Min(off + 25, book.Chapters.Count); i++)
                batch.Add(book.Chapters[i]);

            var fresh = desc.Factory();
            // ★ 新实例必须先把目录走一遍，否则：
            //   · RuleSite：算不出章节 URL（_chapterUrls 为空）→ 全部静默跳过
            //   · biquga  ：没有 Dir 上下文
            //   同时顺手禁掉 biquga 的目录缓存，否则量的是缓存命中而不是真实成本。
            try { fresh.LoadBook(found[0], null); } catch (Exception ex) { Console.WriteLine("  预热失败: " + ex.Message); }
            var bqFresh = fresh as BiqugaSite;
            if (bqFresh != null) bqFresh.SuppressDirCache = true;
            Http.ResetThrottle();

            var b = new BookInfo
            {
                Site = book.Site, Title = book.Title, Author = book.Author,
                Url = book.Url, Dir = book.Dir, Chapters = batch,
            };
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rattest-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tmp);

            var sw = Stopwatch.StartNew();
            var r = new DownloadRunner
            { Site = fresh, Book = b, Chapters = batch, RootDir = tmp, Workers = w, Log = delegate { } };
            r.Run();
            sw.Stop();
            int hits = Http.ThrottleHits;

            Console.WriteLine(string.Format("  {0,-6} {1,-11} {2,-11} {3,-10} {4}",
                w, sw.ElapsedMilliseconds + " ms",
                ((double)sw.ElapsedMilliseconds / batch.Count).ToString("F0") + " ms",
                hits, r.Ok + "/" + batch.Count));

            try { System.IO.Directory.Delete(tmp, true); } catch { }
            Thread.Sleep(3000);      // 档间留间隔，避免上一档的限流影响下一档
        }

        Console.WriteLine();
        Console.WriteLine("  说明：限流计数 > 0 表示站点返回了 429/503/403 或正文里出现限流特征词。");
        Console.WriteLine("        计数每 +2，下载窗口就 -1（AdaptiveWorkers），最低降到 1。");
        return 0;
    }
}
