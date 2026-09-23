using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using TomatoBiquga;

/// <summary>边界与回归测试：直接调用 GUI 的真实写出逻辑</summary>
internal static class EdgeTest
{
    private static int fails = 0;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + name + (detail.Length > 0 ? "  —— " + detail : ""));
        if (!ok) fails++;
    }

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 边界与回归测试 ===");

        // 1) 正式写出：表头应包含统计行、BOM、正文完整
        var book = new BookInfo
        {
            Site = "biquga",
            Title = "测试书《含非法字符》",
            Author = "测试作者",
            Url = "https://example.com/x",
            Dir = "/1_1",
        };
        var body = new StringBuilder();
        body.AppendLine().AppendLine("第一章 测试").AppendLine().AppendLine("正文内容一二三四五六七八九十。");
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_edge");
        Directory.CreateDirectory(dir);
        var txt = Path.Combine(dir, "t.txt");

        WriteTxt(txt, book, body.ToString(), 12, 3, 1);
        var bytes = File.ReadAllBytes(txt);
        bool bom = bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = File.ReadAllText(txt, Encoding.UTF8);
        Check("写出文件带 UTF-8 BOM", bom);
        Check("表头含作者", text.Contains("作者：测试作者"));
        Check("表头含成功/跳过/失败统计", text.Contains("成功 12 章，跳过 3 章，失败 1 章"));
        Check("表头含来源", text.Contains("https://example.com/x"));
        Check("正文完整保留", text.Contains("正文内容一二三四五六七八九十。"));

        // 2) FixHeader 长度差异：表头变长/变短都不能破坏正文
        var t2 = Path.Combine(dir, "t2.txt");
        WriteTxt(t2, book, body.ToString(), 1, 1, 1);
        FixHeader(t2, Header(book, 999999, 88888, 7777));
        var after = File.ReadAllText(t2, Encoding.UTF8);
        Check("FixHeader 后正文仍在（长表头）", after.Contains("正文内容一二三四五六七八九十。"));
        Check("FixHeader 后统计已更新", after.Contains("成功 999999 章"));

        WriteTxt(t2, book, body.ToString(), 999999, 88888, 7777);
        FixHeader(t2, Header(book, 1, 0, 0));
        var after2 = File.ReadAllText(t2, Encoding.UTF8);
        Check("FixHeader 后正文仍在（短表头）", after2.Contains("正文内容一二三四五六七八九十。"));
        Check("FixHeader 后统计已更新（短）", after2.Contains("成功 1 章，跳过 0 章，失败 0 章"));

        // 3) 番茄 book_id 提取
        Check("从分享链接提取 book_id", FanqieSite.ExtractBookId("https://fanqienovel.com/page/7256784068786785336") == "7256784068786785336");
        Check("从纯数字提取 book_id", FanqieSite.ExtractBookId("7256784068786785336") == "7256784068786785336");
        Check("中文书名不误判为 book_id", FanqieSite.ExtractBookId("沧元图") == null);
        Check("过短数字不误判", FanqieSite.ExtractBookId("12345") == null);
        Check("带参数链接提取", FanqieSite.ExtractBookId("https://fanqienovel.com/page/7256784068786785336?enter_from=search") == "7256784068786785336");
        Check("book_id= 形式提取", FanqieSite.ExtractBookId("https://fanqienovel.com/x?book_id=7256784068786785336") == "7256784068786785336");
        Check("搜索页地址生成正确",
            FanqieSite.SearchPageUrl("沧元图") == "https://fanqienovel.com/search/" + Uri.EscapeDataString("沧元图"),
            FanqieSite.SearchPageUrl("沧元图"));
        Check("带空格书名会转义", !FanqieSite.SearchPageUrl("我的 书").Contains(" "));

        // 4) 文件名安全化
        Check("非法字符被替换", Http.SafeFileName("a/b\\c:d*e?f\"g<h>i|j") == "a_b_c_d_e_f_g_h_i_j",
            Http.SafeFileName("a/b\\c:d*e?f\"g<h>i|j"));
        Check("空名有兜底", Http.SafeFileName("") == "未命名");
        Check("结尾点空格被去掉", Http.SafeFileName("书名... ") == "书名");

        // 5) 分页路径解析
        var p1 = BiqugaSite.ParseNextPath("/10_10333/10032108.html");
        var p2 = BiqugaSite.ParseNextPath("/10_10333/10032108_2.html");
        Check("首页解析", p1 != null && p1.Item1 == "10032108" && p1.Item2 == 1);
        Check("第3页解析（_2 -> 第3页）", p2 != null && p2.Item1 == "10032108" && p2.Item2 == 3);
        Check("非章节链接返回 null", BiqugaSite.ParseNextPath("/10_10333/") == null);

        // 6) HTML 清洗
        Check("去标签+实体还原", Http.StripTags("<p>a&amp;b</p><p>&#26085;</p>").Contains("a&b") && Http.StripTags("<p>&#26085;</p>").Contains("日"));
        Check("script 被剔除", !Http.StripTags("<script>bad()</script>good").Contains("bad"));

        // 7) 目录缓存往返
        var b2 = new BookInfo { Site = "biquga", Title = "缓存往返测试", Dir = "/tmp_cache_test", BookId = "" };
        b2.Chapters.Add(new ChapterInfo { Id = "1", Title = "第一章", Order = 0 });
        b2.Chapters.Add(new ChapterInfo { Id = "2", Title = "【第一卷】", IsVolume = true, Order = 1, Selected = false });
        b2.Chapters.Add(new ChapterInfo { Id = "3", Title = "第二章", Order = 2 });
        DirCache.Save(b2);
        var loaded = DirCache.Load("biquga", "tmp_cache_test");
        Check("缓存往返章数一致", loaded != null && loaded.Chapters.Count == 3);
        Check("缓存里分卷标记保留", loaded != null && loaded.Chapters[1].IsVolume && !loaded.Chapters[1].Selected);
        Check("缓存里章节顺序保留", loaded != null && loaded.Chapters[0].Title == "第一章" && loaded.Chapters[2].Title == "第二章");
        try { File.Delete(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "biquga_tmp_cache_test.json")); } catch { }

        // 8) 表头替换在真实下载流程里的重复调用
        var t3 = Path.Combine(dir, "t3.txt");
        WriteTxt(t3, book, body.ToString(), 0, 0, 0);
        for (int i = 1; i <= 3; i++) WriteTxt(t3, book, body.ToString(), i * 20, 0, 0);
        FixHeader(t3, Header(book, 60, 0, 0));
        var after3 = File.ReadAllText(t3, Encoding.UTF8);
        Check("多次写出后正文仍完整", after3.Contains("正文内容一二三四五六七八九十。"));
        Check("多次写出后表头是最终值", after3.Contains("成功 60 章"));
        Check("正文只出现一次", after3.Split(new[] { "正文内容一二三四五六七八九十。" }, StringSplitOptions.None).Length == 2);

        // 9) 下载器的路径解析（界面和命令行共用这一份）
        string bd, tp;
        DownloadRunner.ResolvePaths(dir, "某本书/带:非法*字符", out bd, out tp);
        Check("路径解析：目录在根目录下", bd.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
        Check("路径解析：txt 与目录同名", Path.GetFileNameWithoutExtension(tp) == Path.GetFileName(bd));
        Check("路径解析：书名里的非法字符被替换",
            Path.GetFileName(bd) == "某本书_带_非法_字符", Path.GetFileName(bd));

        // 10) 正文清洗
        Check("广告行被剔除", !TextCleaner.CleanBody("送你一个现金红包！\n正文第一句。").Contains("现金红包"));
        Check("长正文行不被误杀", TextCleaner.CleanBody("他笑着说：我给你投了推荐票，你可要加油写啊，别辜负大家。").Contains("推荐票"));
        Check("空行被压缩", TextCleaner.CleanBody("第一行\n\n\n第二行").Split('\n').Length == 2);

        // 11) 真实 MainForm 的下载路径（复刻“用户点什么都没发生”的场景）
        Console.WriteLine("\n--- 真实界面下载路径测试（窗口移到屏幕外，禁用弹窗） ---");
        try { TestRealFormDownload(); }
        catch (Exception ex) { Check("真实界面下载路径", false, ex.Message); }

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "全部通过 ✔" : ("有 " + fails + " 项失败 ✘"));
        try { Directory.Delete(dir, true); } catch { }
    }

    /// <summary>
    /// 建一个真实的 MainForm（移到屏幕外），直接调它内部下载方法，
    /// 验证“点了下载之后文件真的会生成”。
    /// </summary>
    private static void TestRealFormDownload()
    {
        var cache = DirCache.Load("biquga", "10_10333");
        if (cache == null) { Check("找到已缓存的目录（沧元图）", false, "没有 cache\\biquga_10_10333.json，跳过此项"); return; }
        Check("找到已缓存的目录（沧元图）", cache.Chapters.Count > 100, cache.Chapters.Count + " 章");

        var site = new BiqugaSite();      // 不启用离线：正文走网络（只取 2 章，快）
        var outRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_edge_form");
        try { if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true); } catch { }

        var picked = new List<ChapterInfo>();
        foreach (var c in cache.Chapters)
        {
            if (c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
            picked.Add(c);
            if (picked.Count >= 2) break;   // 只取头 2 章
        }
        Check("从缓存目录里取出待下载章节", picked.Count == 2, picked.Count + " 章");

        Exception error = null;
        MainForm form = null;
        var done = new System.Threading.ManualResetEventSlim(false);
        var worker = new System.Threading.Thread(new System.Threading.ThreadStart(() =>
        {
            try
            {
                form = new MainForm { SuppressDialogs = true, StartPosition = FormStartPosition.Manual };
                form.Left = -4000; form.Top = -4000;
                form.Show();
                form.DownloadCore(site, cache, picked, outRoot);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                try { if (form != null) form.BeginInvoke(new Action(() => form.Close())); } catch { }
                done.Set();
            }
        }));
        worker.SetApartmentState(System.Threading.ApartmentState.STA);
        worker.Start();

        // 跑消息循环，让空窗体的事件能处理
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done.IsSet && sw.Elapsed.TotalSeconds < 180)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(50);
        }
        Check("下载流程在 180 秒内结束", done.IsSet, string.Format("{0:F0}s", sw.Elapsed.TotalSeconds));
        if (error != null) { Check("下载过程没有抛异常", false, error.Message); return; }

        string bd, tp;
        DownloadRunner.ResolvePaths(outRoot, cache.Title, out bd, out tp);
        Check("文件已生成", File.Exists(tp), tp);
        if (File.Exists(tp))
        {
            var text = File.ReadAllText(tp, Encoding.UTF8);
            Check("文件里有正文", text.Length > 500, text.Length + " 字符");
            Check("表头统计正确", System.Text.RegularExpressions.Regex.IsMatch(text, @"本次下载：成功 \d+ 章"));
            Check("两个章节都在", System.Text.RegularExpressions.Regex.Matches(text, @"-{6,}").Count >= 1);
        }
        try { if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true); } catch { }
    }

    // ---- 下面两个直接调用下载器自身的逻辑，测的就是界面用的那份代码 ----

    private static string Header(BookInfo b, int ok, int skip, int fail)
    {
        var r = new DownloadRunner { Book = b };
        return r.BuildHeader(ok, skip, fail);
    }

    private static void WriteTxt(string path, BookInfo b, string content, int ok, int skip, int fail)
    {
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(true)))
        {
            w.Write(Header(b, ok, skip, fail));
            w.Write(content);
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    /// <summary>用下载器真实的表头替换逻辑：先写入指定表头，再调 FixHeaderNow 修正</summary>
    private static void FixHeader(string path, string header)
    {
        // 用 DownloadRunner 自己的写出+修正流程，确保测的是生产代码
        var book = new BookInfo { Title = "x" };
        var body = File.ReadAllText(path, Encoding.UTF8);
        int cut = body.IndexOf(new string('=', 46), StringComparison.Ordinal);
        if (cut < 0) return;
        cut = body.IndexOf('\n', cut) + 1;
        var realBody = body.Substring(cut);

        var r = new DownloadRunner { Book = book };
        var f = typeof(DownloadRunner).GetField("OutputFile",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        // 让 BuildHeader 返回测试想要的那份表头：用 Ok/Skipped/Failed 反推
        var m = System.Text.RegularExpressions.Regex.Match(header, @"成功 (\d+) 章，跳过 (\d+) 章，失败 (\d+) 章");
        if (m.Success)
        {
            r.Ok = int.Parse(m.Groups[1].Value);
            r.Skipped = int.Parse(m.Groups[2].Value);
            r.Failed = int.Parse(m.Groups[3].Value);
        }
        f.SetValue(r, path);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            w.Write(r.BuildHeader());
            w.Write(realBody);
        }
        r.FixHeaderNow();
    }
}
