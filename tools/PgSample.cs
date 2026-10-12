using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TomatoBiquga;

/// <summary>
/// 多页章节抓取基线采样器。
///
/// 用法：pgsample &lt;ids.txt&gt; &lt;输出.txt&gt;
///
/// 为什么需要它：我要把「同一章的续页并发抓」这件事加进去，
/// 而**续页拼接的顺序/内容一旦错，正文就会被静默截断或错位** ——
/// 这种损坏用户不会立刻发现。
/// 所以在改之前先把当前（串行）结果**逐字存成基线**，
/// 改完再跑一次、逐字比对。不一致就不提交。
///
/// 输出的每一段格式：===ID===\n长度\n正文
/// </summary>
internal static class PgSample
{
    private static void Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var ids = File.ReadAllLines(args[0], Encoding.UTF8);
        var sb = new StringBuilder();
        var site = new BiqugaSite();
        var book = new BookInfo
        {
            Site = "biquga",
            Title = "采样",
            Dir = "/10_10333",
            Url = "https://www.biquga.com/10_10333/",
        };

        int done = 0;
        foreach (var raw in ids)
        {
            var id = raw.Trim();
            if (id.Length == 0) continue;
            string text = null;
            try { text = site.LoadChapter(book, new ChapterInfo { Id = id, Title = "t" }, null); }
            catch (Exception ex) { text = "<异常 " + ex.GetType().Name + ">"; }
            if (text == null) text = "<null>";
            sb.Append("===").Append(id).Append("===\n");
            sb.Append(text.Length).Append('\n');
            sb.Append(text).Append('\n');
            done++;
            Console.WriteLine("  " + id + " 长度=" + text.Length);
        }
        File.WriteAllText(args[1], sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine("采样完成：" + done + " 章 → " + args[1]);
        Console.WriteLine("总字符 = " + sb.Length);
    }
}
