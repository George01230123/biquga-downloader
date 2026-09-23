using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TomatoBiquga;

internal static class DiagMobile
{
    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string dir = args.Length > 0 ? args[0] : "/7_7480";
        if (!dir.StartsWith("/")) dir = "/" + dir;

        Console.WriteLine("=== 逐页抓目录，看每页解析出多少章 ===");
        var first = Http.Get(BiqugaMobileSite.Host + dir + "/dindex_1.html", BiqugaMobileSite.Host + "/");
        var pages = new List<int>();
        foreach (Match m in Regex.Matches(first, Regex.Escape(dir) + "/dindex_(\\d+)\\.html"))
        {
            int p = int.Parse(m.Groups[1].Value);
            if (!pages.Contains(p)) pages.Add(p);
        }
        pages.Sort();
        Console.WriteLine("分页数: " + pages.Count + " (最大 " + pages[pages.Count - 1] + ")");

        var rx = new Regex("<a href=\"" + Regex.Escape(dir) + "/(\\d+)\\.html\"[^>]*>([\\s\\S]*?)</a>");

        for (int i = 0; i < pages.Count; i++)
        {
            int p = pages[i];
            try
            {
                var html = Http.Get(BiqugaMobileSite.Host + dir + "/dindex_" + p + ".html", BiqugaMobileSite.Host + "/");
                var ms = rx.Matches(html);
                string firstTitle = "", firstId = "";
                if (ms.Count > 0)
                {
                    firstId = ms[0].Groups[1].Value;
                    firstTitle = Http.StripTags(ms[0].Groups[2].Value).Trim();
                }
                Console.WriteLine(string.Format("  dindex_{0,-3} {1,6}B  匹配 {2,4} 章   首个: {3} {4}",
                    p, html.Length, ms.Count, firstId, firstTitle.Replace("\n", " ").Substring(0, Math.Min(24, firstTitle.Length))));
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("  dindex_{0,-3} 失败: {1}", p, ex.Message));
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 走 LoadBook 完整流程（16 线程并发抓目录页）===");
        var site = new BiqugaMobileSite { ForceRefresh = true, Workers = 16 };
        var book = site.LoadBook(
            new BookInfo { Site = "biquga-m", Dir = dir, Title = "?", Author = "?", Url = BiqugaMobileSite.Host + dir + "/" },
            m => Console.WriteLine("[站点] " + m));
        Console.WriteLine("LoadBook 结果: 书名=" + book.Title + "  章数=" + book.Chapters.Count);
        if (book.Chapters.Count > 0)
        {
            Console.WriteLine("  首章: " + book.Chapters[0].Id + " " + book.Chapters[0].Title);
            Console.WriteLine("  末章: " + book.Chapters[book.Chapters.Count - 1].Id + " " + book.Chapters[book.Chapters.Count - 1].Title);
        }
    }
}
