using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TomatoBiquga;

/// <summary>自测程序：不开界面，直接验证两个站点的抓取逻辑</summary>
internal static class TestMain
{
    private static void Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch { }
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            try
            {
                Console.WriteLine("!!! 异常类型: " + ex.GetType().FullName);
                Console.WriteLine("!!! 消息: " + Safe(() => ex.Message));
                Console.WriteLine("!!! 内层: " + Safe(() => ex.InnerException == null ? "(无)" : ex.InnerException.Message));
                Console.WriteLine("!!! 堆栈: " + Safe(() => ex.StackTrace));
            }
            catch
            {
                Console.WriteLine("!!! 异常信息无法打印");
            }
        }
        Console.WriteLine("[测试结束]");
    }

    private static string Safe(Func<string> f)
    {
        try { return f() ?? "(空)"; }
        catch { return "(读取失败)"; }
    }

    private static void Run(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "biquga";

        if (mode == "biquga")
        {
            var site = new BiqugaSite();
            string kw = args.Length > 1 ? args[1] : "沧元图";
            int limit = args.Length > 2 ? int.Parse(args[2]) : 3;

            var hits = site.Search(kw, m => Console.WriteLine("[搜索] " + m));
            Console.WriteLine("结果数: " + hits.Count);
            foreach (var h in hits.Take(8))
                Console.WriteLine(string.Format("   《{0}》 {1} [{2}]  {3}", h.Title, h.Author, h.Category, h.Dir));
            if (hits.Count == 0) return;

            // 挑最匹配的一条
            var pick = hits.FirstOrDefault(h => h.Title == kw) ?? hits[0];
            Console.WriteLine("\n选中: 《" + pick.Title + "》 " + pick.Dir);

            // 只测前 N 章的抓取（用第一章 + 顺链）
            var html = Http.Get(pick.Url, BiqugaSite.Origin + "/");
            var first = System.Text.RegularExpressions.Regex.Match(html,
                "og:novel:read_url\" content=\"[^\"]*?/(\\d+_\\d+)/(\\d+)\\.html\"");
            if (!first.Success) { Console.WriteLine("没找到第一章"); return; }
            var dir = "/" + first.Groups[1].Value;
            var cid = first.Groups[2].Value;
            Console.WriteLine("第一章 cid=" + cid + " dir=" + dir);

            int ok = 0;
            for (int i = 0; i < limit; i++)
            {
                var url = BiqugaSite.Origin + dir + "/" + cid + ".html";
                var page = Http.Get(url, BiqugaSite.Origin + dir + "/");
                var parsed = BiqugaSite.ParseChapterHtml(page);
                Console.WriteLine(string.Format("\n--- [{0}] {1} ---", i + 1, parsed.Title));
                Console.WriteLine("正文长度: " + parsed.Text.Length);
                Console.WriteLine(parsed.Text.Length > 120 ? parsed.Text.Substring(0, 120) + "…" : parsed.Text);
                var np = BiqugaSite.ParseNextPath(parsed.NextPath);
                if (np == null) { Console.WriteLine("链尾"); break; }
                cid = np.Item1;
                ok++;
            }
            Console.WriteLine("\n连续抓取 " + ok + " 章成功");
        }
        else if (mode == "write")
        {
            // 端到端：搜索 -> 完整目录 -> 下载前 N 章 -> 写 txt（验证与界面里完全相同的代码路径）
            var site = new BiqugaSite { CrawlWorkers = 8 };
            string kw = args.Length > 1 ? args[1] : "沧元图";
            int n = args.Length > 2 ? int.Parse(args[2]) : 5;

            var hits = site.Search(kw, m => Console.WriteLine("[搜索] " + m));
            var pick = hits.FirstOrDefault(h => h.Title == kw) ?? (hits.Count > 0 ? hits[0] : null);
            if (pick == null) { Console.WriteLine("没搜到"); return; }
            Console.WriteLine("选中：《" + pick.Title + "》 " + pick.Dir);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var book = site.LoadBook(pick, m => Console.WriteLine("[目录] " + m));
            Console.WriteLine(string.Format("目录遍历完成：{0} 章，用时 {1:F1}s", book.Chapters.Count, sw.Elapsed.TotalSeconds));
            Console.WriteLine("  首章: " + book.Chapters[0].Title);
            Console.WriteLine("  末章: " + book.Chapters[book.Chapters.Count - 1].Title);
            Console.WriteLine("  正文缓存命中: " + "（顺链时已抓）");

            var take = book.Chapters.Take(n).ToList();
            var sb = new StringBuilder();
            sb.AppendLine(book.Title).AppendLine("作者：" + book.Author).AppendLine("来源：" + book.Url).AppendLine();
            int okCh = 0, skip = 0;
            foreach (var c in take)
            {
                var t = site.LoadChapter(book, c, null);
                if (string.IsNullOrEmpty(t) || t.Length < 40) { skip++; Console.WriteLine("  跳过（站点公告/空内容）: " + c.Title); continue; }
                okCh++;
                sb.AppendLine().AppendLine(c.Title).AppendLine().AppendLine(t);
                Console.WriteLine(string.Format("  {0}：{1} 字", c.Title, t.Length));
            }
            var outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "测试输出_" + Http.SafeFileName(book.Title) + ".txt");
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
            Console.WriteLine(string.Format("\n成功 {0} 章（跳过 {1}），已写出：{2}（{3:N0} 字节）",
                okCh, skip, outPath, new FileInfo(outPath).Length));
            Console.WriteLine("开头 180 字：" + sb.ToString().Substring(0, Math.Min(180, sb.Length)).Replace("\n", " ⏎ "));
        }
        else if (mode == "dl")
        {
            // 端到端：移动版目录 → 取前 N 章 → 走**界面同一份** DownloadRunner 写文件。
            // 用来验证表头区/BOM/缺失章节报告/重试这几条新逻辑在真网络下也对。
            // 用法：_selftest.exe dl /69_69707 6 [输出根目录]
            string dir = args.Length > 1 ? args[1] : "/69_69707";
            int n = args.Length > 2 ? int.Parse(args[2]) : 6;
            string root = args.Length > 3 ? args[3] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");

            Console.WriteLine("=== 端到端小样本下载（移动版）===");
            var site = new BiqugaMobileSite { PrefetchText = false, Workers = 8 };
            var stub = new BookInfo { Site = "biquga-m", Dir = dir, Url = BiqugaMobileSite.Host + dir + "/" };
            var book = site.LoadBook(stub, m => Console.WriteLine("[目录] " + m));
            Console.WriteLine(string.Format("《{0}》 {1} 章", book.Title, book.Chapters.Count));

            var picked = new List<ChapterInfo>();
            foreach (var c in book.Chapters)
            {
                if (c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
                picked.Add(c);
                if (picked.Count >= n) break;
            }
            Console.WriteLine("本次下载前 " + picked.Count + " 章");

            var runner = new DownloadRunner
            {
                Site = site,
                Book = book,
                Chapters = picked,
                RootDir = root,
                RetryPasses = AppSettings.Current.RetryPasses,
                Log = m => Console.WriteLine("[下载] " + m),
            };
            runner.Run();

            Console.WriteLine();
            Console.WriteLine("输出文件：" + runner.OutputFile);
            Console.WriteLine("缺失报告：" + (runner.ReportFile ?? "(没有缺章，未生成)"));
            Console.WriteLine(string.Format("结果：成功 {0}，跳过 {1}，失败 {2}", runner.Ok, runner.Skipped, runner.Failed));
            if (File.Exists(runner.OutputFile))
            {
                var bytes = File.ReadAllBytes(runner.OutputFile);
                Console.WriteLine("BOM：" + (bytes.Length >= 3 && bytes[0] == 0xEF ? "有" : "没有"));
                var text = new UTF8Encoding(true).GetString(bytes);
                Console.WriteLine("表头区标记：" + (text.Contains("#header-zone:") ? "有" : "没有"));
                int sep = text.IndexOf(new string('=', 46), StringComparison.Ordinal);
                Console.WriteLine("--- 表头 ---");
                Console.WriteLine(text.Substring(0, Math.Min(text.Length, sep + 47)).TrimEnd('\n'));
                Console.WriteLine("--- 表头结束 ---");
            }
        }
        else if (mode == "fanqie")
        {
            var site = new FanqieSite();
            string input = args.Length > 1 ? args[1] : "7256784068786785336";

            // 和界面完全一致的路径：先 Search（解析链接/ID），失败时给引导
            var hits = site.Search(input, m => Console.WriteLine("[番茄] " + m));
            if (hits.Count == 0)
            {
                Console.WriteLine("输入既不是 book_id 也不是链接。");
                Console.WriteLine("番茄没有公开的中文搜索接口，请用界面上的「浏览器搜索」，");
                Console.WriteLine("或在浏览器里手动打开：" + FanqieSite.SearchPageUrl(input));
                return;
            }
            var item = hits[0];
            Console.WriteLine("解析出 book_id: " + item.BookId);

            var book = site.LoadBook(item, m => Console.WriteLine("[番茄] " + m));
            Console.WriteLine("书名: " + book.Title + "  作者: " + book.Author + "  状态: " + book.Status);
            Console.WriteLine("章节条目数: " + book.Chapters.Count + "（含分卷标题行）");
            foreach (var c in book.Chapters.Take(4)) Console.WriteLine("   " + c.Title + "  id=" + c.Id);
            Console.WriteLine("    …");
            foreach (var c in book.Chapters.Skip(Math.Max(0, book.Chapters.Count - 2))) Console.WriteLine("   " + c.Title + "  id=" + c.Id);

            var target = book.Chapters.FirstOrDefault(c => !c.IsVolume && !string.IsNullOrEmpty(c.Id));
            if (target == null) { Console.WriteLine("没有可下载章节"); return; }
            Console.WriteLine("\n尝试下载第一章: " + target.Title);
            try
            {
                var text = site.LoadChapter(book, target, m => Console.WriteLine("[番茄] " + m));
                Console.WriteLine("正文长度: " + (text == null ? 0 : text.Length));
                if (!string.IsNullOrEmpty(text))
                    Console.WriteLine(text.Length > 300 ? text.Substring(0, 300) + "…" : text);
            }
            catch (Exception ex)
            {
                Console.WriteLine("正文下载未成功（预期内，站点有风控）：");
                Console.WriteLine("  " + ex.Message);
            }
        }
        else if (mode == "official")
        {
            // 端到端：调用原版 TomatoNovelDownloader 的本地 API 下载番茄小说（拿干净完整正文）
            string bookId = args.Length > 1 ? args[1] : "7256784068786785336";
            var exe = TomatoCore.GuessDefaultExePath();
            Console.WriteLine("官方工具路径: " + (exe == null ? "(未找到)" : exe));
            var core = new TomatoCore { ExePath = exe };
            Console.WriteLine("服务地址: " + core.BaseUrl + "   已在运行=" + core.IsRunning());

            try
            {
                core.EnsureRunning(m => Console.WriteLine("[服务] " + m));
                Console.WriteLine("状态: " + core.StatusJson());

                var jobId = core.SubmitJob(bookId, m => Console.WriteLine("[任务] " + m));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool cancel = false;
                string last = "";
                var final = core.WaitJob(jobId, st =>
                {
                    var s = string.Format("{0}/{1} 章", st.SavedChapters, st.TotalChapters);
                    if (s != last)
                    {
                        last = s;
                        Console.WriteLine(string.Format("  [{0,3}s] {1}  state={2}  {3}",
                            (int)sw.Elapsed.TotalSeconds, st.Title, st.State, s));
                    }
                }, ref cancel);

                Console.WriteLine(string.Format("\n任务结束：state={0}  成功 {1}/{2} 章  用时 {3:F0}s",
                    final.State, final.SavedChapters, final.TotalChapters, sw.Elapsed.TotalSeconds));

                var dir = core.GetSaveDir();
                var file = core.FindOutputFile(final.Title, dir);
                Console.WriteLine("save_dir: " + dir);
                Console.WriteLine("输出文件: " + (file == null ? "(未找到)" : file));
                if (file != null)
                {
                    var fi = new FileInfo(file);
                    Console.WriteLine(string.Format("大小: {0:N0} 字节 ({1:N2} MB)", fi.Length, fi.Length / 1048576.0));
                }
                core.ShutdownIfOwned();
                Console.WriteLine("已关闭由本程序启动的服务进程");
            }
            catch (Exception ex)
            {
                Console.WriteLine("失败：" + ex.Message);
            }
        }
        else if (mode == "gui100")
        {
            // 完全复刻界面「开始下载选中章节」的路径：搜索 → 载入目录 → 取前 N 章 → DownloadRunner
            var site = new BiqugaSite();
            string kw = args.Length > 1 ? args[1] : "沧元图";
            int n = args.Length > 2 ? int.Parse(args[2]) : 100;
            var root = args.Length > 3 ? args[3] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
            bool offline = args.Length > 4 && args[4] == "offline";   // 第 5 个参数 offline 时走离线模式
            if (offline) { site.Offline = true; site.ForceRefresh = true; }

            Console.WriteLine("=== GUI 下载路径复刻测试 ===");
            Console.WriteLine("书名: " + kw + "   章数: " + n + "   离线模式: " + offline);
            Console.WriteLine("保存根目录: " + root);
            var swTotal = System.Diagnostics.Stopwatch.StartNew();

            var hits = site.Search(kw, m => Console.WriteLine("[搜索] " + m));
            var pick = hits.FirstOrDefault(h => h.Title == kw) ?? (hits.Count > 0 ? hits[0] : null);
            if (pick == null) { Console.WriteLine("没搜到书"); return; }

            var book = site.LoadBook(pick, m => Console.WriteLine("[目录] " + m));
            Console.WriteLine("目录: " + book.ToString());

            // 模拟界面上“全选”后取前 N 章
            var picked = book.Chapters.Where(c => !c.IsVolume).Take(n).ToList();
            Console.WriteLine("勾选章节数: " + picked.Count);

            string bookDir, txtPath;
            DownloadRunner.ResolvePaths(root, book.Title, out bookDir, out txtPath);
            Console.WriteLine("准备写入: " + txtPath);
            Console.WriteLine("文件已存在? " + File.Exists(txtPath));

            var runner = new DownloadRunner
            {
                Site = site,
                Book = book,
                Chapters = picked,
                RootDir = root,
                Log = m => Console.WriteLine(m),
                OnProgress = (done, total) => { if (done % 25 == 0 || done == total) Console.WriteLine(string.Format("  ...进度 {0}/{1}", done, total)); },
                IsCanceled = () => false,
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            runner.Run();
            Console.WriteLine(string.Format("\n下载阶段耗时 {0:F1}s（总耗时 {1:F0}s）",
                sw.Elapsed.TotalSeconds, swTotal.Elapsed.TotalSeconds));

            Console.WriteLine("\n=== 结果检查 ===");
            Console.WriteLine("返回的 OutputFile: " + runner.OutputFile);
            Console.WriteLine("文件是否存在: " + File.Exists(runner.OutputFile));
            if (File.Exists(runner.OutputFile))
            {
                var fi = new FileInfo(runner.OutputFile);
                var content = File.ReadAllText(runner.OutputFile, Encoding.UTF8);
                Console.WriteLine(string.Format("大小: {0:N0} 字节 ({1:N2} MB)", fi.Length, fi.Length / 1048576.0));
                Console.WriteLine("BOM: " + (File.ReadAllBytes(runner.OutputFile)[0] == 0xEF));
                Console.WriteLine("正文字符数: " + content.Length);
                Console.WriteLine("章节分隔线数: " + System.Text.RegularExpressions.Regex.Matches(content, @"-{6,}").Count);
                Console.WriteLine("含 HTML 残留: " + System.Text.RegularExpressions.Regex.IsMatch(content, "<[a-z/]"));
                Console.WriteLine("\n--- 文件开头 200 字 ---");
                Console.WriteLine(content.Substring(0, Math.Min(200, content.Length)));
                Console.WriteLine("\n--- 文件结尾 120 字 ---");
                Console.WriteLine(content.Substring(Math.Max(0, content.Length - 120)));
            }
            else
            {
                Console.WriteLine("!!! 文件没有生成，列出目录内容：");
                if (Directory.Exists(bookDir))
                    foreach (var f in Directory.GetFiles(bookDir)) Console.WriteLine("   " + f + "  " + new FileInfo(f).Length + " 字节");
                else
                    Console.WriteLine("   目录也不存在: " + bookDir);
            }
        }
    }
}
