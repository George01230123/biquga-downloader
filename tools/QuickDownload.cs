using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using TomatoBiquga;

/// <summary>
/// 全量下载命令：
///   _quickdl.exe full 牧神记 [输出目录]    —— 搜索 → 载入目录（可离线）→ 下载全部章节
///   _quickdl.exe biquga 69_69707 3 [目录] —— 用缓存目录下前 N 章（快速验证）
/// </summary>
internal static class QuickDownload
{
    private static void Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("!!! 出错类型: " + ex.GetType().FullName);
            try { Console.WriteLine("!!! 消息: " + ex.Message); } catch { Console.WriteLine("!!! (消息无法打印)"); }
            try
            {
                var ie = ex.InnerException;
                if (ie != null) Console.WriteLine("!!! 内层: " + ie.GetType().Name + ": " + ie.Message);
            }
            catch { }
            try { Console.WriteLine("!!! 堆栈:\n" + ex.StackTrace); } catch { }
            Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length > 0 && args[0] == "full") { DoFull(args); return; }
        if (args.Length > 0 && args[0] == "cachedfull") { DoCachedFull(args); return; }
        if (args.Length > 0 && args[0] == "mobilefull") { DoMobileFull(args); return; }

        string site = args.Length > 0 ? args[0] : "biquga";      // biquga / fanqie
        string key = args.Length > 1 ? args[1] : "69_69707";     // 缓存键（biquga 用 69_69707）
        int n = args.Length > 2 ? int.Parse(args[2]) : 3;
        string root = args.Length > 3 ? args[3] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");

        Console.WriteLine("=== 用缓存目录直接下载（复刻界面操作）===");
        Console.WriteLine("缓存: " + site + " / " + key);

        var book = DirCache.Load(site, key);
        if (book == null) { Console.WriteLine("!! 没找到缓存"); return; }
        Console.WriteLine("书名: " + book.Title + "   作者: " + book.Author);
        Console.WriteLine("目录: " + book.Chapters.Count + " 章");
        Console.WriteLine("来源: " + book.Url);

        string bd, tp;
        DownloadRunner.ResolvePaths(root, book.Title, out bd, out tp);
        Console.WriteLine("将写入: " + tp);

        var picked = new List<ChapterInfo>();
        foreach (var c in book.Chapters)
        {
            if (c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
            picked.Add(c);
            if (picked.Count >= n) break;
        }
        Console.WriteLine("勾选: " + picked.Count + " 章");

        ISite s = site == "fanqie" ? (ISite)new FanqieSite() : new BiqugaSite();
        RunWithForm(s, book, picked, root, tp, bd);
    }

    /// <summary>
    /// 用【本地缓存的目录】+ 离线抓正文，然后一次性写全本。
    /// 比重新搜书稳（不会选错版本），而且缓存里的目录已经是完整 1067 章。
    /// </summary>
    private static void DoCachedFull(string[] args)
    {
        string key = args.Length > 1 ? args[1] : "69_69707";
        string root = args.Length > 2 ? args[2] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
        var all = System.Diagnostics.Stopwatch.StartNew();

        Console.WriteLine("=== 用缓存目录全量下载 ===");
        var cached = DirCache.Load("biquga", key);
        if (cached == null) { Console.WriteLine("!! 没找到缓存 " + key); return; }
        Console.WriteLine("书名: " + cached.Title + "   作者: " + cached.Author);
        Console.WriteLine("目录: " + cached.Chapters.Count + " 章   来源: " + cached.Url);

        var site = new BiqugaSite { Offline = true, ForceRefresh = true };
        // 触发一次“离线遍历”：把 1067 章的正文全部抓到内存
        Console.WriteLine("\n--- 阶段 1/2：抓正文（顺“上一页”链，每页都要 TLS 握手，预计 40~60 分钟）---");
        var reloaded = site.LoadBook(
            new BookInfo { Site = "biquga", Dir = cached.Dir, Title = cached.Title, Author = cached.Author, Url = cached.Url },
            m => Console.WriteLine("[遍历] " + m));
        Console.WriteLine(string.Format("[遍历] 完成：{0} 章，用时 {1:F0} 分钟",
            reloaded.Chapters.Count, all.Elapsed.TotalMinutes));

        var picked = new List<ChapterInfo>();
        foreach (var c in reloaded.Chapters)
        {
            if (c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
            picked.Add(c);
        }

        string bd, tp;
        DownloadRunner.ResolvePaths(root, reloaded.Title, out bd, out tp);
        Console.WriteLine(string.Format("\n--- 阶段 2/2：写文件（{0} 章）---", picked.Count));
        RunWithForm(site, reloaded, picked, root, tp, bd, all);
    }

    /// <summary>
    /// 移动版全量下载：11 个目录页并发拿全本章节 id → 并发预抓正文 → 一次性写文件。
    /// 这是目前最快的路径（PC 版要 2 小时，移动版约 10 分钟）。
    /// </summary>
    private static void DoMobileFull(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : "/69_69707";
        string root = args.Length > 2 ? args[2] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
        var all = System.Diagnostics.Stopwatch.StartNew();

        Console.WriteLine("=== 移动版全量下载 ===");
        Console.WriteLine("书籍目录: " + dir + "   输出: " + root);

        var site = new BiqugaMobileSite
        {
            ForceRefresh = true,
            PrefetchText = true,   // 载入目录时并发把正文全抓下来
            Workers = 8,
        };

        // 先从 PC 版缓存/详情页拿基本信息
        var stub = new BookInfo { Site = "biquga-m", Dir = dir, Title = "", Author = "", Url = BiqugaMobileSite.Host + dir + "/" };

        Console.WriteLine("\n--- 阶段 1/2：目录 + 并发预抓正文 ---");
        var book = site.LoadBook(stub, m => Console.WriteLine("[移动版] " + m));
        Console.WriteLine(string.Format("[移动版] 阶段 1 完成，用时 {0:F1} 分钟", all.Elapsed.TotalMinutes));

        var picked = new List<ChapterInfo>();
        foreach (var c in book.Chapters)
            if (!c.IsVolume && !string.IsNullOrEmpty(c.Id)) picked.Add(c);

        string bd, tp;
        DownloadRunner.ResolvePaths(root, book.Title, out bd, out tp);
        Console.WriteLine(string.Format("\n--- 阶段 2/2：写文件（{0} 章，正文已在内存）---", picked.Count));
        RunWithForm(site, book, picked, root, tp, bd, all);
    }

    /// <summary>全量：搜索 → 载入目录（离线模式会连正文一起抓）→ 下载全部</summary>
    private static void DoFull(string[] args)
    {
        string kw = args.Length > 1 ? args[1] : "牧神记";
        string root = args.Length > 2 ? args[2] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
        var site = new BiqugaSite { Offline = true, ForceRefresh = true };   // 离线：遍历时把正文全抓下来

        Console.WriteLine("=== 全量下载 ===");
        Console.WriteLine("书名: " + kw + "   输出目录: " + root);
        Console.WriteLine("模式: 离线（目录遍历时连正文一起抓，之后一次性写文件）");
        var all = System.Diagnostics.Stopwatch.StartNew();

        var hits = site.Search(kw, m => Console.WriteLine("[搜索] " + m));
        var book = hits.FirstOrDefault(h => h.Title == kw)
                   ?? hits.FirstOrDefault(h => h.Title.Contains(kw))
                   ?? (hits.Count > 0 ? hits[0] : null);
        if (book == null) { Console.WriteLine("!! 没搜到这本书"); return; }
        Console.WriteLine("[搜索] 选中: 《" + book.Title + "》 " + book.Author + "  " + book.Dir);

        var loaded = site.LoadBook(book, m => Console.WriteLine("[目录] " + m));
        Console.WriteLine(string.Format("[目录] 完成：{0}（用时 {1:F0}s）", loaded.ToString(), all.Elapsed.TotalSeconds));

        var picked = new List<ChapterInfo>();
        foreach (var c in loaded.Chapters)
        {
            if (c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
            picked.Add(c);
        }
        Console.WriteLine("[下载] 准备写 " + picked.Count + " 章");

        string bd, tp;
        DownloadRunner.ResolvePaths(root, loaded.Title, out bd, out tp);
        RunWithForm(site, loaded, picked, root, tp, bd, all);
    }

    private static void RunWithForm(ISite s, BookInfo book, List<ChapterInfo> picked, string root,
        string tp, string bd, System.Diagnostics.Stopwatch outer = null)
    {
        Exception err = null;
        MainForm form = null;
        var done = new System.Threading.ManualResetEventSlim(false);
        var th = new System.Threading.Thread(new System.Threading.ThreadStart(() =>
        {
            try
            {
                form = new MainForm
                {
                    SuppressDialogs = true,
                    StartPosition = FormStartPosition.Manual,
                };
                form.Left = -4000; form.Top = -4000;
                form.Show();
                form.DownloadCore(s, book, picked, root);
            }
            catch (Exception ex) { err = ex; }
            finally
            {
                try { if (form != null) form.BeginInvoke(new Action(() => form.Close())); } catch { }
                done.Set();
            }
        }));
        th.SetApartmentState(System.Threading.ApartmentState.STA);
        th.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done.IsSet)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(40);
        }

        Console.WriteLine("\n=== 结果 ===");
        Console.WriteLine("下载阶段耗时: " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        if (outer != null) Console.WriteLine("总耗时: " + outer.Elapsed.TotalSeconds.ToString("F1") + "s");
        if (err != null) { Console.WriteLine("!! 异常: " + err.Message); return; }
        Console.WriteLine("文件存在: " + File.Exists(tp) + "  →  " + tp);
        if (File.Exists(tp))
        {
            var fi = new FileInfo(tp);
            var t = File.ReadAllText(tp, Encoding.UTF8);
            Console.WriteLine(string.Format("大小: {0:N0} 字节 ({1:N2} MB), 字符 {2:N0}",
                fi.Length, fi.Length / 1048576.0, t.Length));
            Console.WriteLine("章节分隔线: " + System.Text.RegularExpressions.Regex.Matches(t, @"-{6,}").Count);
            Console.WriteLine("开头 120 字: " + t.Substring(0, Math.Min(120, t.Length)).Replace("\n", " ⏎ "));
            Console.WriteLine("结尾 120 字: " + t.Substring(Math.Max(0, t.Length - 120)).Replace("\n", " ⏎ "));
        }
        else if (Directory.Exists(bd))
        {
            Console.WriteLine("目录内容:");
            foreach (var f in Directory.GetFiles(bd)) Console.WriteLine("   " + f);
        }
        else Console.WriteLine("!! 连目录都不存在: " + bd);
    }
}
