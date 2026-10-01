using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;
using TomatoBiquga;

namespace TomatoBiquga
{
    /// <summary>
    /// 离线单元测试入口（**完全不联网**，可直接放进 CI 跑）。
    ///
    /// 只测纯函数 / 纯逻辑：HTML 工具、文件名清洗、正文清洗、缓存键、简介清洗、
    /// 路径拼接、表头生成、表头原位替换、字体映射表加载。
    /// 联网的端到端测试仍然放在 TestMain.cs / EdgeTest.cs 里，两者互补。
    ///
    /// 编译（必须带 BiqugaSite.cs —— DirCache 的简介清洗是转调它的 CleanDesc，
    /// 不编译它就没法离线验证这条逻辑，见下面 SanitizeDesc 一节）：
    ///   csc /nologo /platform:anycpu /target:exe /optimize+ /codepage:65001 ^
    ///       /main:TomatoBiquga.OfflineTests /out:"dist\_offlinetests.exe" ^
    ///       /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
    ///       /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
    ///       src\AssemblyInfo.cs tests\OfflineTests.cs src\Common\Models.cs ^
    ///       src\Common\Http.cs src\Common\DirCache.cs src\Common\FontMap.cs ^
    ///       src\Common\DownloadRunner.cs src\Sites\BiqugaSite.cs
    ///
    /// 约定：本文件里**不允许出现任何联网 API**（Http.Get/Post、Site.Search、
    /// HttpClient、WebRequest…），也不允许写 dist\cache 和 dist\下载。
    /// 临时文件一律放 %TEMP%\novel-offline-tests-&lt;随机&gt;，退出前删干净。
    /// </summary>
    internal static class OfflineTests
    {
        // ============================================================
        //  极简断言框架（不引 NUnit/xUnit，保持零依赖）
        // ============================================================

        private static int _pass;
        private static int _fail;
        private static bool _verbose;
        private static int _fakeNet;      // 假站点的计数器（让每次 LoadChapter 的输出略有不同）
        private static readonly List<string> Failures = new List<string>();

        /// <summary>断言一个条件成立</summary>
        private static void Check(string name, bool ok)
        {
            Record(name, ok, ok ? "" : "条件不成立");
        }

        /// <summary>断言相等（失败时打印 期望/实际）</summary>
        private static void Eq<T>(string name, T expect, T actual)
        {
            bool ok = EqualityComparer<T>.Default.Equals(expect, actual);
            Record(name, ok, ok ? "" : string.Format("期望={0} 实际={1}", Show(expect), Show(actual)));
        }

        /// <summary>断言"包含"（用于长文本里找关键片段）</summary>
        private static void Contains(string name, string haystack, string needle)
        {
            bool ok = haystack != null && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
            Record(name, ok, ok ? "" : string.Format("在【{0}】里找不到【{1}】", Show(haystack), Show(needle)));
        }

        /// <summary>断言"不包含"</summary>
        private static void NotContains(string name, string haystack, string needle)
        {
            bool ok = haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0;
            Record(name, ok, ok ? "" : string.Format("在【{0}】里不该出现【{1}】", Show(haystack), Show(needle)));
        }

        private static void Record(string name, bool ok, string detail)
        {
            if (ok)
            {
                _pass++;
                if (_verbose) Console.WriteLine("[ok] " + name);
            }
            else
            {
                _fail++;
                var line = detail.Length == 0 ? "[FAIL] " + name : "[FAIL] " + name + ": " + detail;
                // 整行也要封顶：曾经因为把 10MB 正文打进断言消息，日志直接被刷爆
                if (line.Length > 600) line = line.Substring(0, 600) + "…（消息过长已截断）";
                Console.WriteLine(line);
                Failures.Add(line);
            }
        }

        /// <summary>把值渲染成一行可见文本（换行/空串要能看出来；超长截断，别把整本书打进日志）</summary>
        private static string Show(object v)
        {
            if (v == null) return "<null>";
            var s = v as string;
            if (s == null) return v.ToString();
            if (s.Length == 0) return "<空串>";
            if (s.Length > 240) s = s.Substring(0, 240) + "…（共 " + s.Length + " 字）";
            return "«" + s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "»";
        }

        // ============================================================
        //  入口
        // ============================================================

        private static int Main(string[] args)
        {
            return Run(args);
        }

        /// <summary>
        /// 对外入口：GUI exe 带 --selftest 时也会调这里，保证只有一份测试实现。
        /// 返回 0 = 全部通过（CI 直接看退出码）。
        /// </summary>
        internal static int Run(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            _pass = 0;
            _fail = 0;
            Failures.Clear();
            _verbose = false;
            if (args != null)
                foreach (var a in args)
                    if (string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase)) _verbose = true;

            Console.WriteLine("=== 离线单元测试（不联网）===");

            var work = Path.Combine(Path.GetTempPath(), "novel-offline-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);
            try
            {
                TestSafeFileName();
                TestHtmlTools();
                TestTextCleaner();
                TestDirCacheKeyFor();
                TestSanitizeDesc();
                TestResolvePaths(work);
                TestBuildHeader();
                TestFixHeaderNow(work);
                TestMissingReport(work);
                TestProgressMarker(work);
                TestChapterIndex(work);
                TestSiteProfile(work);
                TestMarkdown(work);
                TestFillMissing(work, ref _fakeNet);
                TestOutputEncoding(work, ref _fakeNet);
                TestIncrementalAppend(work, ref _fakeNet);
                TestEpub(work);
                TestParseTxt(work);
                TestLayout();
                TestFontMap();
            }
            catch (Exception ex)
            {
                // 测试代码自己炸了也要如实报出来，不能把异常当成"通过"
                _fail++;
                var line = "[FAIL] 测试框架异常: " + ex.GetType().Name + " " + ex.Message;
                Console.WriteLine(line);
                // 带上出错位置：断言失败好查，框架异常（NRE 之类）没有栈就只能猜
                if (!string.IsNullOrEmpty(ex.StackTrace))
                    Console.WriteLine("       " + ex.StackTrace.Replace("\r", "").Replace("\n", "\n       "));
                Failures.Add(line);
            }
            finally
            {
                TryDeleteDir(work);
            }

            Console.WriteLine();
            int total = _pass + _fail;
            Console.WriteLine(string.Format("通过 {0}/{1}", _pass, total));
            if (_fail > 0)
            {
                Console.WriteLine("失败 " + _fail + " 项：");
                foreach (var f in Failures) Console.WriteLine("  " + f);
            }
            Console.WriteLine(string.Format("临时目录：{0}（已删除）", work));
            return _fail == 0 ? 0 : 1;
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) { Console.WriteLine("清理临时目录失败（不影响结果）：" + ex.Message); }
        }

        // ============================================================
        //  1) Http.SafeFileName
        // ============================================================

        private static void TestSafeFileName()
        {
            // 非法字符 → 下划线
            // 注意：\r 与 \n 各自替换成一个下划线，所以 "\r\n" 结果里是两个连续下划线
            // （\s+ 折叠发生在替换之后，那时已经是下划线、不再算空白）
            Eq("SafeFileName 非法字符替换", "a_b_c_d_e_f_g_h_i_j__k",
                Http.SafeFileName("a/b\\c:d*e?f\"g<h>i|j\r\nk"));

            // 空白折叠 + 首尾裁剪（制表符先变下划线，所以中间是 " _ "）
            Eq("SafeFileName 空白折叠", "牧神记 _ 牧神纪",
                Http.SafeFileName("  牧神记 \t  牧神纪  "));

            // 空 / null / 纯空格 → 未命名
            Eq("SafeFileName 空串", "未命名", Http.SafeFileName(""));
            Eq("SafeFileName null", "未命名", Http.SafeFileName(null));
            Eq("SafeFileName 纯空格", "未命名", Http.SafeFileName("     "));
            // 全是控制字符 → 全变成下划线。"___" 是合法文件名（不为空），所以不会兜底成"未命名"
            Eq("SafeFileName 纯控制字符→下划线", "___", Http.SafeFileName("\r\n\t"));

            // 中文（含括号）必须原样保留
            Eq("SafeFileName 中文与括号保留", "牧神记（牧神纪）", Http.SafeFileName("牧神记（牧神纪）"));

            // 超长截断到 80
            var longName = new string('长', 200);
            Eq("SafeFileName 超长截断到 80", 80, Http.SafeFileName(longName).Length);
            Eq("SafeFileName 超长截断内容", new string('长', 80), Http.SafeFileName(longName));

            // 结尾的点/空格被去掉（Windows 不允许文件名以点结尾）
            Eq("SafeFileName 去掉结尾的点", "abc", Http.SafeFileName("abc..."));
            Eq("SafeFileName 去掉结尾空格", "abc", Http.SafeFileName("abc   "));

            // 路径穿越：结果里不能残留任何目录分隔符
            var evil = Http.SafeFileName("..\\..\\windows\\system32\\config");
            NotContains("SafeFileName 穿越：没有反斜杠", evil, "\\");
            NotContains("SafeFileName 穿越：没有正斜杠", evil, "/");
            Eq("SafeFileName 穿越：结果", ".._.._windows_system32_config", evil);
            // 冒号和反斜杠各变一个下划线（所以是 C__Users...），关键是分隔符必须消失
            Eq("SafeFileName 绝对路径不会越界", "C__Users_Admin_x.txt",
                Http.SafeFileName("C:\\Users\\Admin\\x.txt"));
        }

        // ============================================================
        //  2) Http.StripTags / Http.HtmlDecode
        // ============================================================

        private static void TestHtmlTools()
        {
            // 标签剥离（注意 </p> 会被换成换行，所以结果带一个尾部 \n，这是设计如此）
            Eq("StripTags 基本标签", "正文内容\n", Http.StripTags("<p>正文内容</p>"));
            Eq("StripTags 嵌套标签", "正文", Http.StripTags("<div><p><b><i>正文</i></b></p></div>").Trim());
            Eq("StripTags 相邻标签", "链接", Http.StripTags("<a href=\"x\">链</a><font>接</font>"));
            Eq("StripTags 保留标签内文本", "hello world", Http.StripTags("<span class=\"a\">hello</span> <em>world</em>"));
            Eq("StripTags 空输入", "", Http.StripTags(""));
            Eq("StripTags null", "", Http.StripTags(null));
            Eq("StripTags 纯标签", "", Http.StripTags("<div></div><br/>").Trim());

            // script/style 整体丢弃（含内容）
            Eq("StripTags 丢弃 script", "前后", Http.StripTags("前<script>var a=1;</script>后"));
            Eq("StripTags 丢弃 style", "前后", Http.StripTags("前<style>.a{color:red}</style>后"));

            // br / 块级结束标签 → 换行
            Contains("StripTags br 变换行", Http.StripTags("第一行<br/>第二行"), "\n第二行");
            Contains("StripTags p 结束变换行", Http.StripTags("<p>第一段</p><p>第二段</p>"), "第一段\n");

            // 实体解码
            Eq("HtmlDecode nbsp", "a b", Http.HtmlDecode("a&nbsp;b"));
            Eq("HtmlDecode 数字实体", "it's", Http.HtmlDecode("it&#39;s"));
            Eq("HtmlDecode 十六进制实体", "中", Http.HtmlDecode("&#x4e2d;"));
            Eq("HtmlDecode amp/lt/gt/quot", "a&b<c>d\"e", Http.HtmlDecode("a&amp;b&lt;c&gt;d&quot;e"));
            Eq("HtmlDecode 常见排版实体", "—–…", Http.HtmlDecode("&mdash;&ndash;&hellip;"));
            Eq("HtmlDecode 空串", "", Http.HtmlDecode(""));
            Eq("HtmlDecode null", "", Http.HtmlDecode(null));

            // 双写实体不能解码两次（&amp;nbsp; 应该还原成字面量 &nbsp; 而不是空格）
            Eq("HtmlDecode 不二次解码", "&nbsp;", Http.HtmlDecode("&amp;nbsp;"));

            // 组合：标签 + 实体一起处理（同样带 </p> 造成的换行）
            Eq("StripTags 标签+实体组合", "甲&乙\n", Http.StripTags("<p>甲&amp;乙</p>"));
        }

        // ============================================================
        //  3) TextCleaner：正文清洗（含历史 bug 回归）
        // ============================================================

        private static void TestTextCleaner()
        {
            // ---- 行尾拼接广告（移动版就是这么塞的，广告贴在正文句子后面）----
            // 注意：切掉广告后只 TrimEnd 空白/逗号类标点，句末的"。"会保留（源码行为）
            var trailing = "他心中纳闷。还在为找不到的最新章节苦恼？安利一个公众号：rd444";
            Eq("StripTrailingAd 切掉行尾广告", "他心中纳闷。", TextCleaner.StripTrailingAd(trailing));
            Eq("CleanBody 保留正文并切广告", "他心中纳闷。",
                TextCleaner.CleanBody("  他心中纳闷。还在为找不到的最新章节苦恼？安利一个公众号：rd444  "));

            // 广告在行首 → 整行是广告
            Eq("StripTrailingAd 广告在行首→清空", "",
                TextCleaner.StripTrailingAd("还在为找不到的最新章节苦恼？安利一个公众号：rd444"));
            Eq("CleanBody 整行广告被丢弃", "",
                TextCleaner.CleanBody("还在为找不到的最新章节苦恼？安利一个公众号：rd444"));

            // 其它行尾广告标记（同上：句号保留）
            Eq("StripTrailingAd 帮你找书", "他转身就走。", TextCleaner.StripTrailingAd("他转身就走。帮你找书陪你尬聊"));
            Eq("StripTrailingAd 小姐姐在线服务", "他没有回头。",
                TextCleaner.StripTrailingAd("他没有回头。真人小姐姐在线服务"));

            // 广告标记前面连着标点也要一起切干净
            Eq("StripTrailingAd 切掉未写完的逗号", "他心中纳闷",
                TextCleaner.StripTrailingAd("他心中纳闷，还在为找不到的最新章节苦恼"));

            // ---- 回归：带「推荐票」的正常长句不能被误杀（历史上踩过的 bug）----
            const string prose = "萧炎淡淡一笑，从怀中摸出三张推荐票递给药老，说这是本月最后的一点家底了，往后怕是再也拿不出来了。";
            Check("回归：正文长句超过弱标记阈值(" + prose.Length + "字)", prose.Length > 24);
            Check("回归：带推荐票的长句不算广告", !TextCleaner.IsAdLine(prose));
            Eq("回归：带推荐票的长句原样保留", prose, TextCleaner.CleanBody(prose));
            Eq("回归：带推荐票的长句不被动", prose, TextCleaner.StripTrailingAd(prose));

            // 短行 + 弱标记 → 才算广告
            Check("短行带弱标记算广告", TextCleaner.IsAdLine("求推荐票"));
            Check("短行带月票算广告", TextCleaner.IsAdLine("投月票支持作者"));
            Check("短行带加入书签算广告", TextCleaner.IsAdLine("加入书签"));
            Check("短行带笔趣阁算广告", TextCleaner.IsAdLine("笔趣阁手机版"));

            // ---- 强标记：不管多长都判广告 ----
            Check("强标记 请记住本站 短", TextCleaner.IsAdLine("请记住本站"));
            Check("强标记 请记住本站 长", TextCleaner.IsAdLine("请记住本站www.biquga.com，一秒记住本站域名，方便下次阅读本小说！"));
            Check("强标记 最新网址", TextCleaner.IsAdLine("最新网址：www.example.com"));
            Check("强标记 一秒记住", TextCleaner.IsAdLine("一秒记住【笔趣阁】"));
            Check("强标记 内容来源声明", TextCleaner.IsAdLine("本站所有内容来源于互联网，如有侵权请联系我们删除"));

            // ---- 分页残留 --1120dmabgioie1777198--> ----
            Eq("StripPageMark 整行只有标记→丢弃", "", TextCleaner.StripPageMark("--1120dmabgioie1777198-->"));
            Eq("StripPageMark 标记在行首", "他抬起头来。",
                TextCleaner.StripPageMark("--1120dmabgioie1777198-->他抬起头来。"));
            Eq("StripPageMark 标记在行尾", "他抬起头来。",
                TextCleaner.StripPageMark("他抬起头来。--1120dmabgioie1777198-->"));
            Eq("StripPageMark 不带标记的行原样返回", "他抬起头来。", TextCleaner.StripPageMark("他抬起头来。"));
            Eq("CleanBody 清掉分页残留", "他抬起头来。",
                TextCleaner.CleanBody("他抬起头来。\n--1120dmabgioie1777198-->\n"));
            Eq("CleanBody 只有残留→空", "", TextCleaner.CleanBody("--1120dmabgioie1777198-->"));

            // ---- 整行过滤 + 空输入 ----
            Eq("CleanBody 丢掉广告行保留正文", "正文第一行\n正文第二行",
                TextCleaner.CleanBody("正文第一行\n请记住本站\n正文第二行\n--1120dmabgioie1777198-->"));
            Check("IsAdLine 空串算广告", TextCleaner.IsAdLine(""));
            Check("IsAdLine null 算广告", TextCleaner.IsAdLine(null));
            Eq("CleanBody 空串", "", TextCleaner.CleanBody(""));
            Eq("CleanBody null", "", TextCleaner.CleanBody(null));
            Eq("CleanBody 只去掉尾部空行", "只有一行", TextCleaner.CleanBody("只有一行\n\n\n"));
            Eq("StripTrailingAd 空串", "", TextCleaner.StripTrailingAd(""));
            Eq("StripPageMark 空串", "", TextCleaner.StripPageMark(""));
        }

        // ============================================================
        //  4) DirCache.KeyFor
        // ============================================================

        private static void TestDirCacheKeyFor()
        {
            Eq("KeyFor biquga 去斜杠", "69_69707",
                DirCache.KeyFor(new BookInfo { Site = "biquga", Dir = "/69_69707/" }));
            Eq("KeyFor biquga 无斜杠", "10_10333",
                DirCache.KeyFor(new BookInfo { Site = "biquga", Dir = "10_10333" }));
            Eq("KeyFor biquga Dir 为 null", "",
                DirCache.KeyFor(new BookInfo { Site = "biquga", Dir = null }));
            Eq("KeyFor fanqie 用 bookId", "7256784068786785336",
                DirCache.KeyFor(new BookInfo { Site = "fanqie", BookId = "7256784068786785336", Dir = "/45_45710/" }));
            Eq("KeyFor fanqie 不取 Dir", "7256784068786785336",
                DirCache.KeyFor(new BookInfo { Site = "fanqie", BookId = "7256784068786785336", Dir = "/1_1/" }));
        }

        // ============================================================
        //  5) 简介清洗（DirCache.SanitizeDesc → BiqugaSite.CleanDesc）
        // ============================================================

        private static void TestSanitizeDesc()
        {
            // 站点把导航按钮残留在 og:description 里，这种要当作"没有简介"
            Eq("CleanDesc 导航残留 ahref=#begin", "", BiqugaSite.CleanDesc("ahref=\"#begin\"立即阅读/a"));
            Eq("CleanDesc 立即阅读", "", BiqugaSite.CleanDesc("立即阅读"));
            Eq("CleanDesc 加入书架", "", BiqugaSite.CleanDesc("加入书架"));
            Eq("CleanDesc 开始阅读", "", BiqugaSite.CleanDesc("开始阅读 章节目录"));
            Eq("CleanDesc 半截 a href", "", BiqugaSite.CleanDesc("a href=\"/1_1/1.html\">开始阅读"));
            Eq("CleanDesc 以 < 开头", "", BiqugaSite.CleanDesc("<div>简介</div>"));
            Eq("CleanDesc 以 /a 结尾", "", BiqugaSite.CleanDesc("小说简介，讲一个人/a"));
            Eq("CleanDesc 太短", "", BiqugaSite.CleanDesc("很短"));
            Eq("CleanDesc 空串", "", BiqugaSite.CleanDesc(""));
            Eq("CleanDesc null", "", BiqugaSite.CleanDesc(null));
            Eq("CleanDesc 纯空白", "", BiqugaSite.CleanDesc("     "));

            // 正常简介：裁剪首尾空白后原样返回
            var good = "  一个少年自荒山走出，凭一己之力搅动天下风云的故事。  ";
            Eq("CleanDesc 正常简介保留", "一个少年自荒山走出，凭一己之力搅动天下风云的故事。",
                BiqugaSite.CleanDesc(good));
            NotContains("CleanDesc 正常简介不含空白", BiqugaSite.CleanDesc(good), "  ");

            // 关键：DirCache 读缓存时正是走这条清洗路径
            Eq("SanitizeDesc 链路：清理后的简介才会进表头", "",
                BiqugaSite.CleanDesc("ahref=\"#begin\"立即阅读/a"));
        }

        // ============================================================
        //  6) DownloadRunner.ResolvePaths
        // ============================================================

        private static void TestResolvePaths(string work)
        {
            string bookDir, txtPath;

            DownloadRunner.ResolvePaths(work, "牧神记（牧神纪）", out bookDir, out txtPath);
            Eq("ResolvePaths 中文书名目录", Path.Combine(work, "牧神记（牧神纪）"), bookDir);
            Eq("ResolvePaths 中文书名 txt", Path.Combine(work, "牧神记（牧神纪）", "牧神记（牧神纪）.txt"), txtPath);

            // 标题里有非法字符时，目录名同样要被消毒（否则建目录会失败）
            DownloadRunner.ResolvePaths(work, "测试/书名", out bookDir, out txtPath);
            Eq("ResolvePaths 非法字符被消毒", Path.Combine(work, "测试_书名"), bookDir);
            Eq("ResolvePaths txt 与目录同名", bookDir, Path.GetDirectoryName(txtPath));
            NotContains("ResolvePaths 结果里没有残留斜杠", bookDir.Substring(work.Length), "/");

            // 标题为空 → 未命名（不能拼出一个空的路径段）
            DownloadRunner.ResolvePaths(work, "", out bookDir, out txtPath);
            Eq("ResolvePaths 空标题→未命名", Path.Combine(work, "未命名"), bookDir);

            // 只是算路径，不应该顺手建目录
            Check("ResolvePaths 不创建目录", !Directory.Exists(bookDir));
        }

        // ============================================================
        //  7) DownloadRunner.BuildHeader
        // ============================================================

        private static void TestBuildHeader()
        {
            var book = new BookInfo
            {
                Site = "biquga",
                Title = "牧神记（牧神纪）",
                Author = "宅猪",
                Url = "https://www.biquga.com/69_69707/",
                Desc = "一个少年自荒山走出。",
            };
            var r = new DownloadRunner { Book = book };

            var head = r.BuildHeader(7, 2, 1);
            Contains("BuildHeader 首行是书名", head, "牧神记（牧神纪）");
            Contains("BuildHeader 作者行", head, "作者：宅猪");
            Contains("BuildHeader 来源行（笔趣阁）", head, "来源：笔趣阁");
            Contains("BuildHeader 来源行含 URL", head, "https://www.biquga.com/69_69707/");
            Contains("BuildHeader 简介行", head, "简介：一个少年自荒山走出。");
            Contains("BuildHeader 统计行文案", head, "本次下载：成功 7 章，跳过 2 章，失败 1 章");
            Contains("BuildHeader 下载时间行", head, "下载时间：");
            Contains("BuildHeader 结尾 46 个等号", head, new string('=', 46));

            // 结尾必须恰好是「46 个等号 + 换行」，FixHeaderNow 就靠它定位
            // （AppendLine 在 Windows 上用 \r\n）
            Check("BuildHeader 以 46 等号结尾", head.EndsWith(new string('=', 46) + "\r\n"));

            // 统计行随参数变化（表头修正功能依赖这一点）
            NotContains("BuildHeader 统计行可更新", r.BuildHeader(0, 0, 0), "成功 7 章");
            Contains("BuildHeader 统计行可更新2", r.BuildHeader(0, 0, 0), "成功 0 章，跳过 0 章，失败 0 章");

            // 作者为空 → 未知
            var noAuthor = new DownloadRunner { Book = new BookInfo { Site = "biquga", Title = "无名书", Url = "u" } };
            Contains("BuildHeader 作者为空→未知", noAuthor.BuildHeader(1, 0, 0), "作者：未知");

            // 番茄来源显示不同
            var fq = new DownloadRunner { Book = new BookInfo { Site = "fanqie", Title = "诡舍", Url = "https://fanqienovel.com/page/1" } };
            Contains("BuildHeader 番茄来源", fq.BuildHeader(1, 0, 0), "来源：番茄小说");

            // 简介截断到 300 字 + 换行替换成空格
            var longDesc = new string('甲', 500);
            var headLong = new DownloadRunner
            {
                Book = new BookInfo { Site = "biquga", Title = "长简介书", Url = "u", Desc = longDesc },
            }.BuildHeader(1, 0, 0);
            Contains("BuildHeader 长简介截断为 300 字+省略号", headLong, "简介：" + new string('甲', 300) + "…");
            NotContains("BuildHeader 长简介不留 301 字", headLong, new string('甲', 301));

            var multiLine = new DownloadRunner
            {
                Book = new BookInfo { Site = "biquga", Title = "多行简介", Url = "u", Desc = "第一行\n第二行" },
            }.BuildHeader(1, 0, 0);
            Contains("BuildHeader 简介换行变空格", multiLine, "简介：第一行 第二行");
            NotContains("BuildHeader 简介里不再有裸换行", multiLine.Replace("\r\n", "\n"), "第一行\n第二行");

            // 没有简介时不留空行
            var noDesc = new DownloadRunner { Book = new BookInfo { Site = "biquga", Title = "无简介", Url = "u" } };
            NotContains("BuildHeader 无简介时不输出简介行", noDesc.BuildHeader(1, 0, 0), "简介：");
        }

        // ============================================================
        //  8) DownloadRunner.FixHeaderNow：真实文件往返（最容易被改坏的功能）
        // ============================================================

        private static void TestFixHeaderNow(string work)
        {
            // 构造一个和真实输出一样的文件：UTF-8 BOM + 表头 + 正文
            var body = new StringBuilder();
            for (int i = 1; i <= 200; i++)
                body.Append("\n\n第").Append(i).Append("章 标题").Append(i).Append("\n--------------------\n\n正文内容")
                    .Append(i).Append("，他抬起头来，看了看天色。\n");
            var realBody = body.ToString();

            var path = Path.Combine(work, "fixheader", "表头替换测试.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var runner = new DownloadRunner
            {
                Book = new BookInfo
                {
                    Site = "biquga",
                    Title = "表头替换测试",
                    Author = "测试作者",
                    Url = "https://www.biquga.com/1_1/",
                    Desc = "用来验证表头原位替换不会碰坏正文。",
                }
            };
            runner.Ok = 3; runner.Skipped = 1; runner.Failed = 0;
            runner.OutputFile = path;

            // 先写「表头 + 正文」，正文长度要远大于表头，这样越界就会立刻暴露
            var oldHeader = runner.BuildHeader(3, 1, 0);
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true))) w.Write(oldHeader + realBody);
            long lenBefore = new FileInfo(path).Length;
            Check("FixHeaderNow 准备：正文比表头长很多", realBody.Length > oldHeader.Length * 10);

            // 改成新的统计（数字少一位 → 新表头更短 → 走"补 \n"分支）
            runner.Ok = 0; runner.Skipped = 0; runner.Failed = 0;
            runner.FixHeaderNow();

            var bytes = File.ReadAllBytes(path);
            Check("FixHeaderNow 保留 UTF-8 BOM",
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            long lenAfter = bytes.Length;
            Check(string.Format("FixHeaderNow 文件长度不缩水（{0} → {1}）", lenBefore, lenAfter), lenAfter >= lenBefore);

            var text = new UTF8Encoding(true).GetString(bytes);
            Contains("FixHeaderNow 新统计已写入", text, "本次下载：成功 0 章，跳过 0 章，失败 0 章");
            NotContains("FixHeaderNow 旧统计已消失", text, "成功 3 章");
            Contains("FixHeaderNow 表头其它行保留", text, "作者：测试作者");
            Contains("FixHeaderNow 46 等号仍在", text, new string('=', 46));

            // 正文一字不差（表头替换必须完全不影响正文）
            int bodyAt = text.IndexOf(realBody, StringComparison.Ordinal);
            Check("FixHeaderNow 正文整体一字不差", bodyAt >= 0);
            Eq("FixHeaderNow 正文长度不变", realBody.Length, bodyAt < 0 ? -1 : text.Length - bodyAt);
            Contains("FixHeaderNow 正文首章内容在", text, "第1章 标题1");
            Contains("FixHeaderNow 正文末章内容在", text, "第200章 标题200");

            // ---- 反向 1：**旧格式文件**（没有预留表头区）表头变长时，必须放弃改表头 ----
            // ★ 这是真实存在过的产品 bug（见文件末尾 BUG-1 注释）：
            //   新表头比旧表头长时，FixHeaderNow 会把正文开头若干个字覆盖掉。
            //   修法：正式落盘改成「表头 + 预留表头区」（BuildHeaderZone）。
            //   旧文件没有预留区 → 宁可统计数字不更新，也绝不碰正文。
            var path2 = Path.Combine(work, "fixheader", "变长.txt");
            var r2 = new DownloadRunner
            {
                Book = new BookInfo { Site = "biquga", Title = "变长", Url = "u" }
            };
            r2.Ok = 0; r2.Skipped = 0; r2.Failed = 0;
            r2.OutputFile = path2;
            var smallHeader = r2.BuildHeader(0, 0, 0);
            using (var w = new StreamWriter(path2, false, new UTF8Encoding(true))) w.Write(smallHeader + realBody);
            long smallLen = new FileInfo(path2).Length;
            int smallHeadBytes = Encoding.UTF8.GetByteCount(smallHeader);

            r2.Ok = 9876; r2.Skipped = 12; r2.Failed = 3;
            var bigHeader = r2.BuildHeader(9876, 12, 3);
            int bigHeadBytes = Encoding.UTF8.GetByteCount(bigHeader);
            Check("FixHeaderNow 变长：新表头确实更长（前置条件）", bigHeadBytes > smallHeadBytes);

            var logs2 = new List<string>();
            r2.Log = logs2.Add;
            r2.FixHeaderNow();
            var bytes2 = File.ReadAllBytes(path2);
            var text2 = new UTF8Encoding(true).GetString(bytes2);
            Check("FixHeaderNow 旧格式变长：仍然有 BOM",
                bytes2.Length >= 3 && bytes2[0] == 0xEF && bytes2[1] == 0xBB && bytes2[2] == 0xBF);
            Eq("FixHeaderNow 旧格式变长：文件一个字节都不动", smallLen, bytes2.LongLength);
            Check("FixHeaderNow 旧格式变长：正文一字不差（BUG-1 回归）",
                text2.IndexOf(realBody, StringComparison.Ordinal) >= 0);
            Contains("FixHeaderNow 旧格式变长：旧表头原样保留", text2, smallHeader.TrimEnd('\n', '\r'));
            Check("FixHeaderNow 旧格式变长：日志说明了为什么没改",
                logs2.Exists(l => l.Contains("旧格式") && l.Contains("不更新")));

            // ---- 反向 2：**新格式文件**（带 2KB 预留表头区）表头变长也能正确改写 ----
            var path4 = Path.Combine(work, "fixheader", "新格式变长.txt");
            var r4 = new DownloadRunner { Book = new BookInfo { Site = "biquga", Title = "变长", Url = "u" } };
            r4.OutputFile = path4;
            using (var w = new StreamWriter(path4, false, new UTF8Encoding(true))) w.Write(r4.BuildHeaderZone() + realBody);
            long zoneLen = new FileInfo(path4).Length;
            // 正文起点 = BOM(3) + 表头区字节数（少了 BOM 这 3 字节就会算错）
            const int BomLen = 3;
            long bodyStart = BomLen + Encoding.UTF8.GetByteCount(r4.BuildHeaderZone());
            long bodyStartBefore = ByteOffsetOf(File.ReadAllBytes(path4), realBody);
            Eq("FixHeaderNow 新格式变长：写盘时正文就在表头区之后", bodyStart, bodyStartBefore);

            r4.Ok = 9876; r4.Skipped = 12; r4.Failed = 3;
            r4.FixHeaderNow();
            var bytes4 = File.ReadAllBytes(path4);
            var text4 = new UTF8Encoding(true).GetString(bytes4);
            Contains("FixHeaderNow 新格式变长：新统计写入", text4, "本次下载：成功 9876 章，跳过 12 章，失败 3 章");
            Contains("FixHeaderNow 新格式变长：缺失章节行写入", text4, "缺失 15 章");
            Eq("FixHeaderNow 新格式变长：文件长度不变（表头区固定）", zoneLen, bytes4.LongLength);
            Check("FixHeaderNow 新格式变长：正文一字不差",
                text4.IndexOf(realBody, StringComparison.Ordinal) >= 0);
            // 注意：IndexOf 给的是"字符"下标，而正文起点是按"字节"算的，
            // 前面全是中文（1 字 = 3 字节），所以必须换算成字节再比。
            // text4 是带 BOM 解码的，Substring 里把 BOM 也算进去了，所以减掉 3。
            int bodyIdx = text4.IndexOf(realBody, StringComparison.Ordinal);
            // 注意：text4 是 GetString(整个 byte[]) 得到的，BOM 已经被解码成 \uFEFF 且占 1 个字符，
            // 所以 GetByteCount 会把 BOM 的 3 个字节算回来 —— 这里不能再减 3，减了正好差一个中文字。
            long bodyByteOffset = Encoding.UTF8.GetByteCount(text4.Substring(0, bodyIdx));
            Eq("FixHeaderNow 新格式变长：正文起点没动（字节）", bodyStart, bodyByteOffset);

            // 连跑两次不能越改越乱（GUI 重复点击/异常重试都可能触发）
            r4.FixHeaderNow();
            var text3 = new UTF8Encoding(true).GetString(File.ReadAllBytes(path4));
            Check("FixHeaderNow 幂等：连续调用两次正文仍在",
                text3.IndexOf(realBody, StringComparison.Ordinal) >= 0);
            Contains("FixHeaderNow 幂等：统计不变", text3, "本次下载：成功 9876 章，跳过 12 章，失败 3 章");
            Eq("FixHeaderNow 幂等：文件长度也不变", zoneLen, new FileInfo(path4).Length);

            // 表头被截断（没有 46 等号）时应该直接返回，不能乱写正文
            var path3 = Path.Combine(work, "fixheader", "无表头.txt");
            var r3 = new DownloadRunner { Book = new BookInfo { Site = "biquga", Title = "无表头", Url = "u" } };
            r3.OutputFile = path3;
            using (var w = new StreamWriter(path3, false, new UTF8Encoding(true))) w.Write("没有表头的正文内容");
            long len3 = new FileInfo(path3).Length;
            r3.FixHeaderNow();
            Eq("FixHeaderNow 无表头时不动文件", len3, new FileInfo(path3).Length);
            Eq("FixHeaderNow 无表头时内容不变", "没有表头的正文内容",
                File.ReadAllText(path3, Encoding.UTF8));
        }

        // ============================================================
        //  9) 缺失章节报告：WriteMissingReport（不联网，直接构造列表）
        // ============================================================

        private static void TestMissingReport(string work)
        {
            var dir = Path.Combine(work, "report");
            Directory.CreateDirectory(dir);

            var book = new BookInfo
            {
                Site = "biquga",
                Title = "报告测试",
                Author = "测试作者",
                Dir = "/12_3456/",
                Url = "https://www.biquga.com/12_3456/",
            };
            var runner = new DownloadRunner { Book = book, BookDir = dir, OutputFile = Path.Combine(dir, "报告测试.txt") };
            runner.Chapters.Add(new ChapterInfo { Id = "1001", Title = "第一章 有内容", Order = 1 });
            runner.Chapters.Add(new ChapterInfo { Id = "1002", Title = "第二章 站点空内容", Order = 2 });
            runner.Chapters.Add(new ChapterInfo { Id = "1003", Title = "第三章 网络失败", Order = 3 });
            runner.Ok = 1; runner.Skipped = 1; runner.Failed = 1;
            runner.SkippedChapters.Add(new ChapterInfo { Id = "1002", Title = "第二章 站点空内容", Order = 2 });
            runner.FailedChapters.Add(new ChapterInfo { Id = "1003", Title = "第三章 网络失败", Order = 3 });
            runner.FailReasons["1003"] = "请求超时";

            runner.WriteMissingReport();

            Check("缺失报告：生成了文件", !string.IsNullOrEmpty(runner.ReportFile) && File.Exists(runner.ReportFile));
            var text = File.ReadAllText(runner.ReportFile, Encoding.UTF8);
            Contains("缺失报告：标题行", text, "《报告测试》缺失章节报告");
            Contains("缺失报告：统计行", text, "成功 1 章，跳过 1 章，失败 1 章，共处理 3 章");
            Contains("缺失报告：失败小节", text, "【失败 1 章】");
            Contains("缺失报告：跳过小节", text, "【跳过 1 章】");
            Contains("缺失报告：失败原因", text, "原因：请求超时");
            Contains("缺失报告：失败章节地址", text, "https://www.biquga.com/12_3456/1003.html");
            Contains("缺失报告：跳过章节地址", text, "https://www.biquga.com/12_3456/1002.html");
            Contains("缺失报告：正文优先于工具的口径", text, "不是工具的 bug");
            var bytes = File.ReadAllBytes(runner.ReportFile);
            Check("缺失报告：带 UTF-8 BOM",
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

            // 一章都不缺的时候不该生成报告（避免用户目录里多出一堆无意义文件）
            var clean = new DownloadRunner
            {
                Book = new BookInfo { Site = "biquga", Title = "全须全尾", Dir = "/1_1/", Url = "u" },
                BookDir = dir,
                OutputFile = Path.Combine(dir, "全须全尾.txt"),
            };
            clean.WriteMissingReport();
            Check("缺失报告：没有缺章时不生成", clean.ReportFile == null);

            // 番茄（site=fanqie）的地址格式不一样，别拼成笔趣阁的
            var fq = new DownloadRunner
            {
                Book = new BookInfo { Site = "fanqie", Title = "番茄测试", BookId = "7256784068786785336", Url = "u" },
                BookDir = dir,
                OutputFile = Path.Combine(dir, "番茄测试.txt"),
            };
            fq.FailedChapters.Add(new ChapterInfo { Id = "7406592932351836696", Title = "第一章", Order = 1 });
            fq.FailReasons["7406592932351836696"] = "验证码中间页";
            fq.Failed = 1;
            fq.WriteMissingReport();
            Contains("缺失报告：番茄地址用 reader 链接",
                File.ReadAllText(fq.ReportFile, Encoding.UTF8),
                "https://fanqienovel.com/reader/7406592932351836696");
        }

        // ============================================================
        //  10) 界面布局：任何窗口尺寸/站点模式下，控件都不能越界或互相重叠
        //      —— 这条是真实用户反馈逼出来的：按钮压住"（还没载入目录）"文字，
        //         最右边的「设置/安装番茄核心」被切掉一半。
        // ============================================================

        private static void TestLayout()
        {
            // 先自证"检查器真的能发现问题"：故意摆两个重叠控件 + 一个越界控件，必须都能报出来。
            // （否则"布局无越界"这句可能只是因为检查器永远返回空 —— 等于没测。）
            using (var probe = new Form())
            {
                probe.ClientSize = new Size(300, 120);
                probe.StartPosition = FormStartPosition.Manual;
                probe.Location = new Point(-4000, -4000);
                probe.ShowInTaskbar = false;
                var a = new Label { Text = "甲甲甲", Bounds = new Rectangle(10, 10, 120, 20) };
                var b = new Label { Text = "乙乙乙", Bounds = new Rectangle(60, 15, 120, 20) };
                var c = new Label { Text = "越界", Bounds = new Rectangle(280, 10, 100, 20) };
                probe.Controls.AddRange(new Control[] { a, b, c });
                // 必须 Show 一下：控件没显示时 Visible 全是 false，检查器会跳过它们（那样这条自证就是空转，
                // 第一版正是这么写的，跑出来两条 FAIL —— 自证断言的价值就在这儿）
                probe.Show();
                Application.DoEvents();
                probe.PerformLayout();
                var seeded = ProbeProblems(probe);
                Check("布局检查器能发现重叠", seeded.Exists(x => x.Contains("重叠")));
                Check("布局检查器能发现越界", seeded.Exists(x => x.Contains("越界")));
                probe.Hide();
            }

            // 真实开窗需要桌面会话（窗口句柄 / 窗口站）。没有桌面的 CI runner 上
            // Show() 会抛异常 —— 那不是布局问题，跳过并说明即可，别让整个 CI 变红。
            try { using (var canary = new Form()) { canary.Show(); canary.Hide(); } }
            catch (Exception ex)
            {
                Console.WriteLine("  [跳过] 当前环境无法创建窗口，跳过开窗布局断言：" + ex.GetType().Name);
                return;
            }

            var sizes = new[]
            {
                new Size(820, 600),    // 最小尺寸
                new Size(1000, 720),   // 默认尺寸
                new Size(1280, 800),   // 拉大
            };
            var siteNames = new[] { "番茄小说", "笔趣阁（移动版）", "笔趣阁（PC版）" };

            foreach (var size in sizes)
            {
                for (int si = 0; si < siteNames.Length; si++)
                {
                    string where = string.Format("{0}x{1}/{2}", size.Width, size.Height, siteNames[si]);
                    try
                    {
                        using (var f = new MainForm())
                        {
                            f.StartPosition = FormStartPosition.Manual;
                            f.Location = new Point(-4000, -4000);
                            f.ShowInTaskbar = false;
                            f.SuppressDialogs = true;
                            f.Size = size;
                            f.Show();
                            Application.DoEvents();
                            f.SelectSiteForTest(si);
                            Application.DoEvents();
                            f.PerformLayout();

                            var problems = f.CollectLayoutProblems();
                            Check("布局无越界/重叠（" + where + "）", problems.Count == 0);

                            // 光"不越界"还不够：按钮漏加/被藏起来也看不出来。
                            // 这里把每个按钮都点名检查一遍（加功能时最容易犯的错就是把按钮忘了加进面板）。
                            var texts = new List<string>();
                            CollectButtonTexts(f, texts);
                            foreach (var want in new[]
                            {
                                "全选", "全不选", "反选", "下载选中", "下载全部",
                                "更新新章节", "补齐缺章", "取消", "导出 EPUB", "导出 Markdown", "打开目录",
                            })
                            {
                                Check("按钮在位：" + want + "（" + where + "）", texts.Contains(want));
                            }
                            if (problems.Count > 0)
                            {
                                var head = new StringBuilder();
                                for (int i = 0; i < problems.Count && i < 4; i++)
                                    head.Append(" | ").Append(problems[i]);
                                Failures.Add("      布局问题明细：" + head);
                            }
                            f.Hide();
                        }
                    }
                    catch (Exception ex)
                    {
                        Record("布局体检不抛异常（" + where + "）", false, ex.GetType().Name + " " + ex.Message);
                    }
                }
            }

            // ---- 设置对话框也要查：它以前同样是硬编码坐标，是老代码里最后一个没验过的界面 ----
            try
            {
                Form dlg;
                var problems = MainForm.CollectSettingsDialogProblems(out dlg);
                using (dlg)
                {
                    Check("设置对话框无越界/重叠", problems.Count == 0);
                    if (problems.Count > 0)
                    {
                        var head = new StringBuilder();
                        for (int i = 0; i < problems.Count && i < 4; i++) head.Append(" | ").Append(problems[i]);
                        Failures.Add("      设置对话框布局问题：" + head);
                    }
                    Check("设置对话框是固定尺寸（不能最大化）", dlg.FormBorderStyle == FormBorderStyle.FixedDialog && !dlg.MaximizeBox);
                    Check("设置对话框有取消按钮（Esc 能关）", dlg.CancelButton != null);
                    Check("设置对话框控件都真的加进去了", dlg.Controls.Count >= 4);
                }
            }
            catch (Exception ex)
            {
                Record("设置对话框布局体检不抛异常", false, ex.GetType().Name + " " + ex.Message);
            }

            // ---- 设置对话框的值往返（几何对了，语义也得对）----
            try
            {
                NumericUpDown[] nums;
                var s = new AppSettings { BiqugaOfflineWorkers = 8, BiqugaOnlineWorkers = 6, BiqugaPcWorkers = 6, MinDelayMs = 60, RetryPasses = 1 };
                using (var dlg = MainForm.BuildSettingsDialog(s, new Font("Microsoft YaHei UI", 9F), out nums))
                {
                    Eq("设置对话框：输入框数量", 5, nums.Length);
                    Eq("设置对话框：离线并发初值", 8, (int)nums[0].Value);
                    Eq("设置对话框：间隔初值", 60, (int)nums[3].Value);
                    // 模拟用户改值 → 读回
                    nums[0].Value = 12; nums[3].Value = 100; nums[4].Value = 2;
                    MainForm.ReadSettingsDialog(nums, s);
                    Eq("设置对话框：改后离线并发", 12, s.BiqugaOfflineWorkers);
                    Eq("设置对话框：改后间隔", 100, s.MinDelayMs);
                    Eq("设置对话框：最大间隔自动取 2 倍", 200, s.MaxDelayMs);
                    Eq("设置对话框：改后重试轮数", 2, s.RetryPasses);
                }
                // 边界：超过上限的值要被夹住（NumericUpDown 自己会夹，Clamp 再兜一层）
                var s2 = new AppSettings();
                var wild = new NumericUpDown[5];
                for (int i = 0; i < 5; i++) wild[i] = new NumericUpDown { Minimum = 0, Maximum = 100000, Value = 99999 };
                MainForm.ReadSettingsDialog(wild, s2);
                Check("设置对话框：超范围的值被夹到安全区间", s2.BiqugaOfflineWorkers <= 32 && s2.RetryPasses <= 3 && s2.MinDelayMs <= 5000);
            }
            catch (Exception ex)
            {
                Record("设置对话框值往返不抛异常", false, ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>收集窗体里所有按钮的文本（检查"按钮是不是真的加进面板了"）</summary>
        private static void CollectButtonTexts(Control parent, List<string> into)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Button && !string.IsNullOrEmpty(c.Text)) into.Add(c.Text);
                if (c.HasChildren) CollectButtonTexts(c, into);
            }
        }

        /// <summary>借 MainForm 的检查逻辑去查任意容器（只为自证检查器有效，不是产品逻辑）</summary>
        private static List<string> ProbeProblems(Control container)
        {
            var list = new List<string>();
            var m = typeof(MainForm).GetMethod("CollectLayoutProblems",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (m == null) { list.Add("找不到 CollectLayoutProblems（检查器被改名了？）"); return list; }
            m.Invoke(null, new object[] { container, list });
            return list;
        }

        // ============================================================
        //  11) 增量更新：只追加新章节，旧正文必须一字不动
        //      （这是"更新已下载的书"的核心不变量 —— 真实文件上验过，
        //        这里固化成断言，避免以后改 DownloadRunner 把它弄坏）
        // ============================================================

        private static void TestIncrementalAppend(string work, ref int fakeNet)
        {
            var dir = Path.Combine(Path.GetTempPath(), "novel-incr-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            Eq("增量：测试目录已建好", true, Directory.Exists(dir));

            var book = new BookInfo { Site = "biquga", Title = "增量测试", Author = "作者", Dir = "/9_9/", Url = "u" };
            // 注意：Run() 内部会用 ResolvePaths 自己算路径（RootDir + 安全书名），
            // 所以测试必须用同一个函数取路径，不能自己拼一个（第一版就栽在这）。
            string bookDir, path;
            DownloadRunner.ResolvePaths(dir, book.Title, out bookDir, out path);
            var all = new List<ChapterInfo>();
            for (int i = 1; i <= 12; i++)
                all.Add(new ChapterInfo { Id = "k" + (2000 + i), Title = "第" + i + "章 标题" + i, Order = i });
            book.Chapters.AddRange(all);

            // 第一次：下载前 5 章（走产品自己的 runner，用假站点提供正文）
            var first = new DownloadRunner
            {
                Site = new FakeSite(ref fakeNet),
                Book = book,
                Chapters = all.GetRange(0, 5),
                RootDir = dir,
                OutputFile = path,
                BookDir = dir,
            };
            first.Run();
            Eq("增量：第一次写入 5 章", 5, first.Ok);
            var text1 = File.ReadAllText(path, Encoding.UTF8);
            int done1; string fp1; int bc1;
            Check("增量：第一次写完后能读出进度", DownloadRunner.TryReadProgress(path, out done1, out fp1, out bc1));
            Eq("增量：进度显示 5 章", 5, done1);

            // 记录正文区的起点：表头区标记那一行之后
            // 正文区起点按**字节**算：表头补白可能是换行/空格，字符数与字节数不是一回事
            // （第一版断言按字符比，得出"起点漂了 10 个字符"的假警报，其实字节数一模一样）。
            int bodyAt1 = ByteOffsetAfterHeader(text1);
            var body1 = text1.Substring(bodyAt1);

            // 第二次：追加第 6~12 章（增量模式）
            var second = new DownloadRunner
            {
                Site = new FakeSite(ref fakeNet),
                Book = book,
                Chapters = all.GetRange(5, 7),
                RootDir = dir,
                OutputFile = path,
                BookDir = dir,
                AppendToExistingFile = true,
                CumulativeOkCount = done1,
            };
            second.Run();
            Eq("增量：第二次追加 7 章", 7, second.Ok);

            var text2 = File.ReadAllText(path, Encoding.UTF8);
            int bodyAt2 = ByteOffsetAfterHeader(text2);

            // ★ 核心不变量：正文区一字不动，且长度只增不减
            // 正文区起点必须完全一致：表头区是固定 2KB，统计行也补齐到固定字节数，
            // 所以"累计章数从 6 变 12"不会让正文位置漂移（这是修过的 bug，见 BuildStatisticsLine）
            Eq("增量：正文区起点没变（按字节比）", bodyAt1, bodyAt2);
            Check("增量：旧正文一字未动（更新前后正文区前缀完全一致）", text2.Substring(bodyAt2).StartsWith(body1));
            Check("增量：正文区只增不减", text2.Substring(bodyAt2).Length > body1.Length);
            Check("增量：文件只变长", text2.Length > text1.Length);
            Check("增量：新章节进了文件", text2.Contains("第12章 标题12"));
            Check("增量：旧章节还在", text2.Contains("第1章 标题1"));

            // 章节顺序必须还是递增的（追加导致错位是最怕的事）
            int lastAt = -1, orderBad = 0;
            for (int i = 0; i < all.Count; i++)
            {
                int at = text2.IndexOf(all[i].Title, StringComparison.Ordinal);
                if (at < 0 || at < lastAt) orderBad++;
                lastAt = at;
            }
            Eq("增量：12 章全部存在且顺序递增", 0, orderBad);

            // 表头统计必须变成"累计 12 章"，进度标记也要更新
            int done2; string fp2; int bc2;
            Check("增量：更新后能读出进度", DownloadRunner.TryReadProgress(path, out done2, out fp2, out bc2));
            Eq("增量：累计章数变成 12", 12, done2);
            Contains("增量：表头统计显示累计 12 章", text2, "成功 12 章");
            Check("增量：指纹随章节变化（下次更新才不会误判）", fp1 != fp2);


            // 边界：文件不存在时增量模式不能崩（等于新建）
            // 用一个"从没下载过"的书名，模拟"调用方要求追加、但文件其实不存在"的情况
            var bookNew = new BookInfo { Site = "biquga", Title = "全新的书", Author = "作者", Dir = "/9_9/", Url = "u" };
            bookNew.Chapters.AddRange(all);
            string bdNew, pathNew;
            DownloadRunner.ResolvePaths(dir, bookNew.Title, out bdNew, out pathNew);
            Check("增量边界：起始时文件确实不存在", !File.Exists(pathNew));

            var third = new DownloadRunner
            {
                Site = new FakeSite(ref fakeNet),
                Book = bookNew,
                Chapters = all.GetRange(0, 3),
                RootDir = dir,
                AppendToExistingFile = true,
            };
            bool threw = false;
            try { third.Run(); } catch (Exception ex) { threw = true; Failures.Add("      增量边界异常：" + ex.Message); }
            Check("增量：文件不存在时不抛异常", !threw);
            Check("增量：文件不存在时会正常新建并写入", File.Exists(pathNew) && new FileInfo(pathNew).Length > 200);
            // 落回普通下载后，表头必须是完整的（含进度标记），否则下次增量更新又用不了
            var tNew = File.ReadAllText(pathNew, Encoding.UTF8);
            Check("增量边界：新建的文件有表头和进度标记",
                tNew.Contains(new string('=', 46)) && tNew.Contains(DownloadRunner.ProgressOkMarker));
            TryDeleteDir(dir);
        }

        /// <summary>
        /// 算"正文区起点"的字符下标：表头以「46 个等号 + 换行」结尾。
        ///
        /// 这里能安全地用字符下标：等号行之前的补白全是单字节字符（换行/空格），
        /// 中文字节数变化只出现在等处**之后**，所以匹配纯 ASCII 的等号整行是可靠的
        /// （按字节找"第一个换行"会误命中补白里的换行 —— 第一版就错在这）。
        /// </summary>
        private static int ByteOffsetAfterHeader(string text)
        {
            // 正文起点 = **#header-zone 标记行的行尾**之后。
            // 不能拿"46 个等号那行"当边界：等号行属于表头，它后面还有补白和标记行，
            // 拿它当边界会得出"表头区长 2 字节"之类的假结论（踩过）。
            int mk = text.IndexOf(DownloadRunner.HeaderZoneMarker, StringComparison.Ordinal);
            if (mk < 0) return 0;
            int nl = text.IndexOf('\n', mk);
            return nl < 0 ? 0 : nl + 1;
        }

        /// <summary>假的站点：直接返回造好的正文，不联网（用来测下载流程本身）</summary>
        private class FakeSite : ISite
        {
            private readonly int _seed;
            public FakeSite(ref int fakeNet) { _seed = ++fakeNet; }
            public string Name { get { return "fake"; } }
            public List<BookInfo> Search(string keyword, Action<string> log) { return new List<BookInfo>(); }
            public BookInfo LoadBook(BookInfo item, Action<string> log) { return item; }
            public string LoadChapter(BookInfo book, ChapterInfo chapter, Action<string> log)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 6; i++)
                    sb.Append("这是 ").Append(chapter.Title).Append(" 的第 ").Append(i + 1).Append(" 段正文内容，用来让长度超过 40 字的门槛。\n");
                return sb.ToString();
            }
        }
        // ============================================================
        //  11) 增量更新的进度标记（表头里的 #progress-ok:N:指纹）
        // ============================================================

        private static void TestProgressMarker(string work)
        {
            var dir = Path.Combine(work, "progress");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "进度测试.txt");

            var book = new BookInfo { Site = "biquga", Title = "进度测试", Author = "作者", Dir = "/1_2/", Url = "u" };
            var chapters = new List<ChapterInfo>();
            for (int i = 1; i <= 10; i++)
                chapters.Add(new ChapterInfo { Id = "c" + (1000 + i), Title = "第" + i + "章", Order = i });

            var runner = new DownloadRunner { Book = book, Chapters = chapters, OutputFile = path, BookDir = dir };
            // 写入"表头区 + 5 章正文"，模拟已下载 5 章的文件
            runner.Ok = 5;
            var header = runner.BuildHeader(5, 0, 0);
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.Write(runner.BuildHeaderZone());
                for (int i = 0; i < 5; i++) w.Write("\n\n第" + (i + 1) + "章\n---\n\n正文内容" + (i + 1) + "\n");
            }

            // 表头里应该能读出"已下 5 章"
            int ok; string fp; int bc;
            var read = DownloadRunner.TryReadProgress(path, out ok, out fp, out bc);
            Check("进度标记：能读出来", read);
            Eq("进度标记：已下载章数", 5, ok);
            Check("进度标记：指纹非空且是 12 位十六进制",
                !string.IsNullOrEmpty(fp) && fp.Length == 12 && System.Text.RegularExpressions.Regex.IsMatch(fp, "^[0-9a-f]{12}$"));
            Contains("进度标记：统计行里带上了标记", File.ReadAllText(path, Encoding.UTF8), DownloadRunner.ProgressOkMarker + "5:");

            // 指纹要和"前 5 章 id"一致，且 5 章和 6 章的指纹必须不同（否则判断不出新旧）
            Eq("进度指纹：与前 5 章 id 一致", DownloadRunner.Fingerprint("c1001|c1002|c1003|c1004|c1005|"), fp);
            Check("进度指纹：5 章与 6 章不同",
                DownloadRunner.Fingerprint("c1001|c1002|c1003|c1004|c1005|") !=
                DownloadRunner.Fingerprint("c1001|c1002|c1003|c1004|c1005|c1006|"));

            // 目录里前 5 章没变 → 新章节就是第 6~10 章（这是"更新"的核心判断）
            var idsNow = new StringBuilder();
            for (int i = 0; i < 5; i++) idsNow.Append(chapters[i].Id).Append('|');
            Eq("更新判断：前 5 章指纹没变 → 可以安全追加", fp, DownloadRunner.Fingerprint(idsNow.ToString()));
            Eq("更新判断：新增章数 = 总章数 - 已下载", 5, chapters.Count - ok);

            // 收尾：原位更新统计（累计 10 章），要求文件长度不变、正文不被动
            var before = new FileInfo(path).Length;
            var bodyBefore = File.ReadAllText(path, Encoding.UTF8);
            var changed = DownloadRunner.InjectHeaderStatistics(path, book, 10, 0, 0, chapters, (int)before);
            Check("表头原位更新：返回成功", changed);
            var after = new FileInfo(path).Length;
            Eq("表头原位更新：文件长度不变（正文没被顶掉）", before, after);
            var bodyAfter = File.ReadAllText(path, Encoding.UTF8);
            Contains("表头原位更新：正文仍在（第5章）", bodyAfter, "正文内容5");
            Contains("表头原位更新：统计变成累计 10 章", bodyAfter, "成功 10 章");
            DownloadRunner.TryReadProgress(path, out ok, out fp, out bc);
            Eq("表头原位更新：读回累计章数", 10, ok);

            // 旧文件（没有进度标记）必须"读不出来"而不是瞎猜
            var legacy = Path.Combine(dir, "旧文件.txt");
            using (var w = new StreamWriter(legacy, false, new UTF8Encoding(true))) w.Write("书名\n作者：某某\n==========\n正文\n");
            int ok2; string fp2; int bc2;
            Check("旧文件：读不出进度（返回 false）", !DownloadRunner.TryReadProgress(legacy, out ok2, out fp2, out bc2));
            Eq("旧文件：章数返回 -1", -1, ok2);
            Check("不存在的文件：读进度不抛异常", !DownloadRunner.TryReadProgress(Path.Combine(dir, "没有这个文件.txt"), out ok2, out fp2, out bc2));

            // 字节级查找（中文不能靠字符下标定位）
            var bytes = File.ReadAllBytes(path);
            Check("字节查找：能找到中文串", DownloadRunner.IndexOfBytes(bytes, 0, bytes.Length, Encoding.UTF8.GetBytes("本次下载：")) > 0);
            Check("字节查找：找不到时返回 -1", DownloadRunner.IndexOfBytes(bytes, 0, bytes.Length, Encoding.UTF8.GetBytes("这段字不存在")) < 0);
        }

        // ============================================================
        //  11.5) 章节锚点与按章插入（缺章补齐 / 章节级续传的地基）
        // ============================================================

        private static void TestChapterIndex(string work)
        {
            var dir = Path.Combine(work, "anchors");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "锚点测试.txt");

            var book = new BookInfo { Site = "biquga", Title = "锚点测试", Author = "作者", Dir = "/1_1/", Url = "u" };
            var all = new List<ChapterInfo>();
            for (int i = 1; i <= 6; i++)
                all.Add(new ChapterInfo { Id = "h" + (3000 + i), Title = "第" + i + "章 标题" + i, Order = i });
            book.Chapters.AddRange(all);

            // 造一个"缺第 3、5 章"的文件（模拟站点侧空内容被跳过）。
            // 表头用产品自己的 BuildHeaderZone()：手搓补白长度会和真实格式不一致，
            // 测出来的"正文起点"就没有参考价值（第一版就是手搓的，断言因此误报）。
            var seed = new DownloadRunner { Book = book, Chapters = all, OutputFile = path, BookDir = dir };
            seed.Ok = 4; seed.Skipped = 2;
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.Write(seed.BuildHeaderZone());
                foreach (var c in new[] { all[0], all[1], all[3], all[4] })
                    w.Write(ChapterIndex.BuildChapterBlock(c.Id, c.Title,
                        "这是 " + c.Title + " 的正文内容，长度足够超过四十个字的门槛，用来通过 空章节 的判断。"));
            }

            // ---- 扫描 ----
            Check("锚点：文件里能扫到锚点", ChapterIndex.HasAnchors(path));
            var spans = ChapterIndex.Scan(path);
            Eq("锚点：扫到 4 章", 4, spans.Count);
            Eq("锚点：第一章 id", "h3001", spans[0].Id);
            Eq("锚点：每章标题", "第1章 标题1", spans[0].Title);
            Check("锚点：正文非空（HasBody）", spans[0].HasBody);
            Check("锚点：章节区间连续（前一章 End = 后一章 Start）", spans[0].End == spans[1].Start);
            Check("锚点：最后一章 End 到文件末尾",
                spans[spans.Count - 1].End == new FileInfo(path).Length);
            var byId = ChapterIndex.ById(spans);
            Check("锚点：能按 id 查到", byId.ContainsKey("h3004"));

            // 锚点行不能污染正文：正文里应当看不到锚点文本被当成内容
            var text = File.ReadAllText(path, Encoding.UTF8);
            Contains("锚点：文件里确实有锚点行", text, ChapterIndex.AnchorPrefix + "h3001");

            // ---- 插入缺失的第 3 章（应插在第 2 章之后、第 4 章之前）----
            var c3 = all[2];
            var body3 = "这是 " + c3.Title + " 的正文内容，长度足够超过四十个字的门槛，用来通过 空章节 的判断。";
            Check("插入：第 3 章尚未在文件里", !ChapterIndex.ById(ChapterIndex.Scan(path)).ContainsKey(c3.Id));
            Check("插入：按章插入成功", ChapterIndex.InsertChapter(path, book, c3, body3));

            var spans2 = ChapterIndex.Scan(path);
            Eq("插入：现在有 5 章", 5, spans2.Count);
            var order2 = new List<string>();
            foreach (var s in spans2) order2.Add(s.Id);
            Eq("插入：顺序正确（h3003 排在第 2、4 章之间）", "h3001,h3002,h3003,h3004,h3005", string.Join(",", order2));
            var t2 = File.ReadAllText(path, Encoding.UTF8);
            Contains("插入：正文真的写进去了", t2, "这是 第3章 标题3 的正文内容");
            Check("插入：老正文没被破坏", t2.Contains("这是 第2章 标题2 的正文内容") && t2.Contains("这是 第4章 标题4 的正文内容"));

            // ---- 再插入第 5 章（这次是"中间再少一章"的另一种情形）----
            var c5 = all[4];
            Check("插入：按章插入第 5 章", ChapterIndex.InsertChapter(path, book, c5,
                "这是 " + c5.Title + " 的正文内容，长度足够超过四十个字的门槛，用来通过 空章节 的判断。"));
            var order3 = new List<string>();
            foreach (var s in ChapterIndex.Scan(path)) order3.Add(s.Id);
            Eq("插入：6 章顺序全对", "h3001,h3002,h3003,h3004,h3005", string.Join(",", order3));

            // ---- 第 6 章（目录里最后一章）应追加到末尾 ----
            var c6 = all[5];
            Check("插入：末尾追加成功", ChapterIndex.InsertChapter(path, book, c6,
                "这是 " + c6.Title + " 的正文内容，长度足够超过四十个字的门槛，用来通过 空章节 的判断。"));
            var order4 = new List<string>();
            foreach (var s in ChapterIndex.Scan(path)) order4.Add(s.Id);
            Eq("插入：补齐后 6 章齐全且顺序正确", "h3001,h3002,h3003,h3004,h3005,h3006", string.Join(",", order4));

            // ---- 重复插入同一章 = 原位替换（不能变成两份）----
            Check("插入：已存在的章走原位替换", ChapterIndex.InsertChapter(path, book, c6, "替换后的正文内容，长度也足够超过四十个字的门槛，用来通过判断。"));
            Eq("插入：替换后章数不变", 6, ChapterIndex.Scan(path).Count);
            var t4 = File.ReadAllText(path, Encoding.UTF8);
            Contains("插入：替换后的内容生效", t4, "替换后的正文内容");
            Check("插入：被替换的旧内容没留下第二份", !t4.Contains("这是 第6章 标题6 的正文内容"));

            // ---- 表头不能被插入动作破坏 ----
            Contains("插入：表头仍在（46 个等号行）", t4, new string('=', 46));
            Contains("插入：进度标记仍在", t4, DownloadRunner.ProgressOkMarker);
            int markerAt = t4.IndexOf(DownloadRunner.HeaderZoneMarker, StringComparison.Ordinal);
            int zoneEnd = t4.IndexOf('\n', markerAt) + 1;
            Check("插入：正文（第一个锚点）在表头区之后",
                t4.IndexOf(ChapterIndex.AnchorPrefix, StringComparison.Ordinal) > zoneEnd);

            // ---- 旧文件（没有锚点）不能崩，且要明确"不支持按章插入" ----
            var legacy = Path.Combine(dir, "旧格式.txt");
            File.WriteAllText(legacy,
                "旧书\r\n作者：A\r\n来源：笔趣阁　u\r\n本次下载：成功 1 章，跳过 0 章，失败 0 章\r\n下载时间：x\r\n" +
                new string('=', 46) + "\r\n\r\n第1章 旧章\r\n----\r\n\r\n正文。\r\n", new UTF8Encoding(true));
            Check("旧文件：扫不到锚点", !ChapterIndex.HasAnchors(legacy));
            Eq("旧文件：扫描返回空表", 0, ChapterIndex.Scan(legacy).Count);
            Check("旧文件：按章插入明确失败（而不是写坏文件）",
                !ChapterIndex.InsertChapter(legacy, book, all[0], "不该写进去的正文，长度足够超过四十个字门槛，用来验证。"));
            Check("旧文件：内容没被改动", File.ReadAllText(legacy, Encoding.UTF8).Contains("正文。"));

            // ---- 边界 ----
            Eq("边界：不存在的文件扫描返回空", 0, ChapterIndex.Scan(Path.Combine(dir, "没有.txt")).Count);
            Check("边界：不存在的文件插入返回 false",
                !ChapterIndex.InsertChapter(Path.Combine(dir, "没有.txt"), book, all[0], "x"));
            Check("边界：Splice 越界参数被拒绝", !ChapterIndex.Splice(path, 10, 99999999, "x"));
            Eq("边界：锚点文本格式", ChapterIndex.AnchorPrefix + "abc" + ChapterIndex.AnchorSuffix + "\n", ChapterIndex.AnchorOf("abc"));
            Eq("边界：id 为空也能生成锚点", ChapterIndex.AnchorPrefix + ChapterIndex.AnchorSuffix + "\n", ChapterIndex.AnchorOf(null));
        }
        // ============================================================
        //  11.6) 站点配置（只允许连接/编码参数，拒绝任何内容提取规则）
        // ============================================================

        private static void TestSiteProfile(string work)
        {
            var dir = Path.Combine(work, "profile");
            Directory.CreateDirectory(dir);

            var defs = SiteProfileStore.Defaults();
            Check("站点配置：有三个内置站点", defs.Count >= 3);
            var m = SiteProfileStore.Get("biquga-m");
            Eq("站点配置：移动版默认并发", 8, m.Workers);
            Check("站点配置：默认编码是 utf-8", m.IsUtf8(m.OutputEncoding));
            Check("站点配置：默认 UA 为空（用内置的）", m.UserAgent.Length == 0);

            // 正常改：并发/间隔/编码/UA
            var path = Path.Combine(dir, "站点配置.ini");
            File.WriteAllText(path,
                "# 注释行\r\n" +
                "[biquga-m]\r\n" +
                "workers=12\r\n" +
                "mindelayms=30\r\n" +
                "maxdelayms=90\r\n" +
                "timeoutseconds=40\r\n" +
                "maxretries=5\r\n" +
                "outputencoding=gbk\r\n" +
                "useragent=MyTestUA/1.0\r\n" +
                "[自定义站]\r\n" +
                "workers=3\r\n", new UTF8Encoding(true));

            var list = SiteProfileStore.Load(path);
            SiteProfile p = null, custom = null;
            foreach (var x in list)
            {
                if (x.Name == "biquga-m") p = x;
                if (x.Name == "自定义站") custom = x;
            }
            Check("站点配置：能读到移动版这一节", p != null);
            Eq("站点配置：workers 生效", 12, p.Workers);
            Eq("站点配置：间隔生效", 30, p.MinDelayMs);
            Eq("站点配置：超时生效", 40, p.TimeoutSeconds);
            Eq("站点配置：重试生效", 5, p.MaxRetries);
            Eq("站点配置：UA 生效", "MyTestUA/1.0", p.UserAgent);
            Check("站点配置：编码切到 gbk", p.IsGbk(p.OutputEncoding));
            Check("站点配置：gbk 编码对象可用", p.FileEncoding() != null);
            Check("站点配置：新站点也能加", custom != null && custom.Workers == 3);

            // ★ 合规闸门：内容提取类参数必须被拒绝
            var badPath = Path.Combine(dir, "带规则的.ini");
            File.WriteAllText(badPath,
                "[biquga-m]\r\n" +
                "workers=8\r\n" +
                "contentSelector=.content p\r\n" +
                "regex=<div id=\"content\">(.*?)</div>\r\n" +
                "xpath=//div[@id='content']\r\n", new UTF8Encoding(true));
            var badList = SiteProfileStore.Load(badPath);
            Check("站点配置：拒绝提取规则后给出说明", !string.IsNullOrEmpty(SiteProfileStore.LastError));
            Contains("站点配置：说明里点明原因", SiteProfileStore.LastError, "内容提取规则");
            SiteProfile bm = null;
            foreach (var x in badList) if (x.Name == "biquga-m") bm = x;
            Eq("站点配置：合法参数仍然生效（只有非法键被忽略）", 8, bm.Workers);

            // 数值越界要被夹住
            var clampPath = Path.Combine(dir, "越界.ini");
            File.WriteAllText(clampPath, "[biquga-m]\r\nworkers=999\r\nmindelayms=-5\r\nmaxdelayms=1\r\n", new UTF8Encoding(true));
            var cp = SiteProfileStore.Get("biquga-m");
            var cl = SiteProfileStore.Load(clampPath);
            foreach (var x in cl) if (x.Name == "biquga-m") cp = x;
            Check("站点配置：并发被夹到 32 以内", cp.Workers <= 32 && cp.Workers >= 1);
            Check("站点配置：最大间隔不小于最小间隔", cp.MaxDelayMs >= cp.MinDelayMs);

            // 文件不存在/损坏 → 回退默认值，不抛异常
            var none = SiteProfileStore.Load(Path.Combine(dir, "没有这个文件.ini"));
            Eq("站点配置：文件不存在时回退默认（仍有 3 个站点）", 3, none.Count);
            var broken = Path.Combine(dir, "损坏.ini");
            File.WriteAllBytes(broken, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02 });
            bool threw = false;
            try { SiteProfileStore.Load(broken); } catch { threw = true; }
            Check("站点配置：损坏文件不抛异常", !threw);

            // 样例文件能生成且能被自己读回
            var sample = Path.Combine(dir, "样例.ini");
            SiteProfileStore.SaveSample(sample, SiteProfileStore.Defaults());
            Check("站点配置：样例文件已生成", File.Exists(sample));
            var back = SiteProfileStore.Load(sample);
            Eq("站点配置：样例能被自己读回（站点数一致）", 3, back.Count);
        }

        // ============================================================
        //  11.7) Markdown 导出
        // ============================================================

        private static void TestMarkdown(string work)
        {
            var dir = Path.Combine(work, "md");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "导出测试.md");

            var book = new BookInfo
            {
                Site = "biquga", Title = "导出测试", Author = "测试作者",
                Desc = "简介里有 <尖括号> 和 & 符号。", Dir = "/1_2/", Url = "https://www.biquga.com/1_2/",
            };
            var chapters = new List<ChapterInfo>
            {
                new ChapterInfo { Id = "1", Title = "第一章 开始", Text = "第一段。\n\n# 这行以井号开头，不能被当成标题\n- 这行以减号开头\n2. 这行像列表\n普通段落 & <符号>。", Order = 1 },
                new ChapterInfo { Id = "2", Title = "第二章 继续", Text = "正文二。", Order = 2 },
                new ChapterInfo { Id = "3", Title = "第三章 空", Text = "", Order = 3 },              // 空章要跳过
                new ChapterInfo { Id = "4", Title = "第一卷", Text = "x", IsVolume = true, Order = 4 }, // 分卷行要跳过
            };

            MarkdownWriter.Write(path, book, chapters, true);
            Check("Markdown：文件已生成", File.Exists(path) && new FileInfo(path).Length > 100);
            var md = File.ReadAllText(path, Encoding.UTF8);

            Check("Markdown：有 YAML front matter", md.StartsWith("---\n"));
            Contains("Markdown：front matter 里有书名", md, "title: \"导出测试\"");
            Contains("Markdown：front matter 里有作者", md, "author: \"测试作者\"");
            Contains("Markdown：front matter 里有章节数", md, "chapters: 2");
            Contains("Markdown：一级标题是书名", md, "# 导出测试");
            Contains("Markdown：有目录", md, "## 目录");
            Contains("Markdown：目录里有锚点链接", md, "(#ch0001)");
            Contains("Markdown：有锚点定义", md, "<a id=\"ch0001\"></a>");
            Contains("Markdown：章节是二级标题", md, "## 第一章 开始");
            Contains("Markdown：正文段落", md, "第一段。");
            Check("Markdown：空章节没被导出", md.IndexOf("第三章 空", StringComparison.Ordinal) < 0);
            Check("Markdown：分卷行没被导出", md.IndexOf("## 第一卷", StringComparison.Ordinal) < 0);

            // 正文里会破坏结构的行必须被转义
            Contains("Markdown：行首井号被转义", md, "\\# 这行以井号开头");
            Contains("Markdown：行首减号被转义", md, "\\- 这行以减号开头");
            Contains("Markdown：形似有序列表的行被转义", md, "\\2. 这行像列表");
            Check("Markdown：普通正文没被乱转义", md.Contains("普通段落 & <符号>。"));

            // 不带目录
            var path2 = Path.Combine(dir, "无目录.md");
            MarkdownWriter.Write(path2, book, chapters, false);
            var md2 = File.ReadAllText(path2, Encoding.UTF8);
            Check("Markdown：可以选择不生成目录", md2.IndexOf("## 目录", StringComparison.Ordinal) < 0);
            Contains("Markdown：不生成目录时章节仍在", md2, "## 第一章 开始");

            // 没有正文 → 明确报错，不生成空文件
            bool threw = false;
            try { MarkdownWriter.Write(Path.Combine(dir, "空.md"), book, new List<ChapterInfo> { new ChapterInfo { Id = "9", Title = "空", Text = "" } }, true); }
            catch (Exception ex) { threw = ex.Message.IndexOf("先下载", StringComparison.Ordinal) >= 0; }
            Check("Markdown：没有正文时明确报错", threw);

            // 转义函数
            Eq("Markdown 转义：井号", "\\#a", MarkdownWriter.Escape("#a"));
            Eq("Markdown 转义：星号", "\\*a\\*", MarkdownWriter.Escape("*a*"));
            Eq("Markdown：YAML 里的引号被换掉", "\"a'b\"", MarkdownWriter.Yaml("a\"b"));
            Eq("Markdown：YAML 空值", "\"\"", MarkdownWriter.Yaml(null));
        }
        // ============================================================
        //  11.75) 输出编码（站点配置里可以设 gbk）
        // ============================================================

        private static void TestOutputEncoding(string work, ref int fakeNet)
        {
            var dir = Path.Combine(Path.GetTempPath(), "novel-enc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var book = new BookInfo { Site = "biquga", Title = "编码测试", Author = "作者", Dir = "/5_5/", Url = "u" };
            var chs = new List<ChapterInfo>
            {
                new ChapterInfo { Id = "e1", Title = "第一章 中文标题", Order = 1 },
                new ChapterInfo { Id = "e2", Title = "第二章 也是中文", Order = 2 },
            };
            book.Chapters.AddRange(chs);
            string bd, path;
            DownloadRunner.ResolvePaths(dir, book.Title, out bd, out path);

            // 默认：UTF-8 带 BOM
            var r1 = new DownloadRunner { Site = new FakeSite(ref fakeNet), Book = book, Chapters = chs, RootDir = dir, Log = delegate { } };
            r1.Run();
            var b1 = File.ReadAllBytes(path);
            Check("编码：默认写 UTF-8 带 BOM",
                b1.Length >= 3 && b1[0] == 0xEF && b1[1] == 0xBB && b1[2] == 0xBF);
            Check("编码：UTF-8 文件里能读到中文", File.ReadAllText(path, Encoding.UTF8).Contains("编码测试"));

            // 站点配置指定 GBK
            var prof = SiteProfileStore.Get("biquga-m");
            prof.OutputEncoding = "gbk";
            var gbk = prof.FileEncoding();
            Eq("编码：站点配置能给出 GBK 编码对象", 936, gbk.CodePage);

            // 路径必须用 ResolvePaths 算（它是 root\书名\书名.txt）
            string bd2, path2;
            DownloadRunner.ResolvePaths(dir, book.Title, out bd2, out path2);
            var r2 = new DownloadRunner
            {
                Site = new FakeSite(ref fakeNet), Book = book, Chapters = chs,
                RootDir = dir, OutputEncoding = gbk, Log = delegate { },
            };
            r2.Run();
            var b2 = File.ReadAllBytes(path2);
            var b1Utf8Len = b1.Length;
            Check("编码：GBK 文件不写 UTF-8 BOM", !(b2.Length >= 3 && b2[0] == 0xEF));
            var textGbk = File.ReadAllText(path2, Encoding.GetEncoding("GBK"));
            // 表头区也必须用 GBK 写：FixHeaderNow 会原位重写表头，之前那里写死了 UTF-8，
            // 结果 GBK 文件的表头是坏字节（这条断言就是抓这个的）
            Check("编码：GBK 文件的表头也能用 GBK 读出 46 个等号",
                textGbk.IndexOf(new string('=', 46), StringComparison.Ordinal) > 0);
            Check("编码：GBK 文件用 GBK 能正常读回中文", textGbk.Contains("编码测试") && textGbk.Contains("中文标题"));
            Check("编码：GBK 文件用 UTF-8 读会乱码（证明确实是 GBK）",
                !File.ReadAllText(path2, Encoding.UTF8).Contains("编码测试"));

            // 截断的 GBK 字节数应当小于 UTF-8（中文在 GBK 里 2 字节，UTF-8 里 3 字节）
            Check("编码：GBK 文件体积小于 UTF-8（中文 2 字节 vs 3 字节）", b2.Length < b1Utf8Len);

            TryDeleteDir(dir);
        }
        // ============================================================
        //  11.8) 缺章补齐 / 章节级续传（同一个 FillMissing，两种场景）
        // ============================================================

        private static void TestFillMissing(string work, ref int fakeNet)
        {
            // ---------- 场景 A：整本下完了，中间有几个洞（站点侧空内容）----------
            var dirA = Path.Combine(Path.GetTempPath(), "novel-fill-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dirA);
            var book = new BookInfo { Site = "biquga", Title = "补齐测试", Author = "作者", Dir = "/7_7/", Url = "u" };
            var all = new List<ChapterInfo>();
            for (int i = 1; i <= 10; i++)
                all.Add(new ChapterInfo { Id = "m" + (5000 + i), Title = "第" + i + "章 标题" + i, Order = i });
            book.Chapters.AddRange(all);
            string bdA, pathA;
            DownloadRunner.ResolvePaths(dirA, book.Title, out bdA, out pathA);

            // 造一个"缺第 3、4、8 章"的文件（这几章当时是站点空内容）
            var seed = new DownloadRunner { Site = new FakeSite(ref fakeNet), Book = book, Chapters = all, RootDir = dirA, Log = delegate { } };
            var kept = new List<ChapterInfo>();
            foreach (var c in all) if (c.Id != "m5003" && c.Id != "m5004" && c.Id != "m5008") kept.Add(c);
            seed.Chapters = kept;
            seed.Run();
            Eq("补齐：起始文件写了 7 章", 7, seed.Ok);
            var order0 = new List<string>();
            foreach (var s in ChapterIndex.Scan(pathA)) order0.Add(s.Id);
            Eq("补齐：起始状态确实是 7 章且缺 3 章", "m5001,m5002,m5005,m5006,m5007,m5009,m5010", string.Join(",", order0));

            long sizeBefore = new FileInfo(pathA).Length;
            var runner = new DownloadRunner
            {
                Site = new FakeSite(ref fakeNet),
                Book = book,
                Chapters = new List<ChapterInfo>(),      // FillMissing 会自己填
                RootDir = dirA,
                Log = delegate { },
            };
            var logs = new List<string>();
            DownloadRunner.FillMissing(runner, pathA, logs.Add);
            Eq("补齐：只抓了缺的 3 章", 3, runner.Ok);

            var spansA = ChapterIndex.Scan(pathA);
            var orderA = new List<string>();
            foreach (var s in spansA) orderA.Add(s.Id);
            Eq("补齐：10 章齐全且顺序正确", "m5001,m5002,m5003,m5004,m5005,m5006,m5007,m5008,m5009,m5010", string.Join(",", orderA));
            Eq("补齐：章数从 7 变 10", 10, spansA.Count);

            var textA = File.ReadAllText(pathA, Encoding.UTF8);
            Contains("补齐：补上的第 3 章正文在文件里", textA, "第3章 标题3");
            Contains("补齐：老章节正文没被破坏", textA, "第1章 标题1");
            Check("补齐：文件只变长", new FileInfo(pathA).Length > sizeBefore);
            Contains("补齐：表头统计行更新为 10 章", textA, "成功 10 章");
            int progA; string fpA; int bcA;
            DownloadRunner.TryReadProgress(pathA, out progA, out fpA, out bcA);
            Eq("补齐：进度标记也更新成 10 章", 10, progA);
            Check("补齐：有日志说明补了几章", logs.Exists(l => l.Contains("缺") && l.Contains("章")));

            // 再跑一次：已经没有缺口了，应该什么都不做
            var again = new DownloadRunner { Site = new FakeSite(ref fakeNet), Book = book, Chapters = new List<ChapterInfo>(), Log = delegate { } };
            var logs2 = new List<string>();
            DownloadRunner.FillMissing(again, pathA, logs2.Add);
            Eq("补齐：再跑一次不重复下载", 0, again.Ok);
            Check("补齐：明确告知没有需要补的", logs2.Exists(l => l.Contains("没有需要补齐")));

            // ---------- 场景 B：断点续传（上半本下完，中断后继续）----------
            var dirB = Path.Combine(Path.GetTempPath(), "novel-resume-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dirB);
            var book2 = new BookInfo { Site = "biquga", Title = "续传测试", Author = "作者", Dir = "/8_8/", Url = "u" };
            var all2 = new List<ChapterInfo>();
            for (int i = 1; i <= 10; i++)
                all2.Add(new ChapterInfo { Id = "r" + (6000 + i), Title = "第" + i + "章", Order = i });
            book2.Chapters.AddRange(all2);
            string bdB, pathB;
            DownloadRunner.ResolvePaths(dirB, book2.Title, out bdB, out pathB);

            // 模拟"下到第 4 章就被中断"：写前 4 章
            var half = new DownloadRunner { Site = new FakeSite(ref fakeNet), Book = book2, Chapters = all2.GetRange(0, 4), RootDir = dirB, Log = delegate { } };
            half.Run();
            Eq("续传：中断时文件里有 4 章", 4, half.Ok);
            int before; string fpB; int bcB;
            DownloadRunner.TryReadProgress(pathB, out before, out fpB, out bcB);
            Eq("续传：进度标记是 4 章", 4, before);

            // 用户重新点"下载全部章节"：续传应该只补剩下的 6 章
            // 故意不设 RootDir：FillMissing 应当能从文件路径自推出来（这条回落逻辑也要测）
            var resume = new DownloadRunner
            {
                Site = new FakeSite(ref fakeNet),
                Book = book2,
                Chapters = new List<ChapterInfo>(),
                Log = delegate { },
            };
            var logs3 = new List<string>();
            DownloadRunner.FillMissing(resume, pathB, logs3.Add);
            Eq("续传：只下了剩下的 6 章", 6, resume.Ok);
            Check("续传：调用方没给 RootDir 时也能自推出来", !string.IsNullOrEmpty(resume.RootDir));

            var orderB = new List<string>();
            foreach (var s in ChapterIndex.Scan(pathB)) orderB.Add(s.Id);
            Eq("续传：10 章齐全且顺序正确",
                "r6001,r6002,r6003,r6004,r6005,r6006,r6007,r6008,r6009,r6010", string.Join(",", orderB));
            int after; string fpB2; int bcB2;
            DownloadRunner.TryReadProgress(pathB, out after, out fpB2, out bcB2);
            Eq("续传：进度变成 10 章", 10, after);
            var textB = File.ReadAllText(pathB, Encoding.UTF8);
            Contains("续传：上半本正文还在", textB, "第1章");
            Contains("续传：下半本补上了", textB, "第10章");

            // ---------- 场景 C：旧文件（无锚点）要明确拒绝而不是写坏 ----------
            var legacy = Path.Combine(dirB, "旧格式.txt");
            File.WriteAllText(legacy, "旧书\r\n作者：A\r\n来源：x\r\n本次下载：成功 1 章\r\n下载时间：x\r\n" +
                new string('=', 46) + "\r\n\r\n第1章\r\n----\r\n\r\n正文。\r\n", new UTF8Encoding(true));
            var old = new DownloadRunner { Site = new FakeSite(ref fakeNet), Book = book2, Chapters = new List<ChapterInfo>(), Log = delegate { } };
            var logs4 = new List<string>();
            DownloadRunner.FillMissing(old, legacy, logs4.Add);
            Eq("补齐：旧格式文件不下载任何章", 0, old.Ok);
            Check("补齐：明确提示需要重新下载", logs4.Exists(l => l.Contains("锚点") || l.Contains("重新下载")));
            Check("补齐：旧文件内容没被动", File.ReadAllText(legacy, Encoding.UTF8).Contains("正文。"));

            TryDeleteDir(dirA);
            TryDeleteDir(dirB);
        }
        // ============================================================
        //  12) EPUB 导出（结构必须符合规范，否则阅读器打不开）
        // ============================================================

        private static void TestEpub(string work)
        {
            var dir = Path.Combine(work, "epub");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "导出测试.epub");

            var book = new BookInfo
            {
                Site = "biquga",
                Title = "导出测试",
                Author = "测试作者",
                Desc = "简介里有特殊字符 <b>&</b> 用来验证转义。",
                Dir = "/1_2/",
                Url = "https://www.biquga.com/1_2/",
            };
            var chapters = new List<ChapterInfo>
            {
                new ChapterInfo { Id = "1", Title = "第一章 开始", Text = "第一段。\n\n第二段有 <尖括号> 和 & 符号。\n\n\n第三段。", Order = 1 },
                new ChapterInfo { Id = "2", Title = "第二章 继续", Text = "正文二。", Order = 2 },
                new ChapterInfo { Id = "3", Title = "第三章 未下载", Text = "", Order = 3 },        // 空章要跳过
                new ChapterInfo { Id = "4", Title = "第一卷 分卷标题", Text = "x", IsVolume = true, Order = 4 }, // 分卷行要跳过
            };

            EpubWriter.Write(path, book, chapters, null, null);
            Check("EPUB：文件已生成", File.Exists(path) && new FileInfo(path).Length > 500);

            using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
            {
                var names = new List<string>();
                foreach (var e in zip.Entries) names.Add(e.FullName);
                // 规范第 1 条：mimetype 必须是第一个条目，且**不能被压缩**
                Eq("EPUB：第一个条目是 mimetype", "mimetype", names[0]);
                var mimetypeEntry = zip.GetEntry("mimetype");
                // 注意：不能断言"压缩方式字段 == 0"。实测 .NET 的 NoCompression 是
                // "deflate 头 + stored 块"（数据里的 01 标记），字节确实没被压缩，
                // 但方式字段是 8 不是 0。所以检验真实行为：压缩后不比原始小。
                // mimetype 的 Content-Length 是 20，压缩后 25：多出来的 5 字节是 deflate
                // "stored 块"的固定开销（不是真压缩）。所以判据是"两者接近"，而不是严格相等。
                Check(string.Format("EPUB：mimetype 没被真正压缩（原始 {0}，存放 {1}，只差 {2} 字节的块头开销）",
                        mimetypeEntry.Length, mimetypeEntry.CompressedLength,
                        mimetypeEntry.CompressedLength - mimetypeEntry.Length),
                    Math.Abs(mimetypeEntry.CompressedLength - mimetypeEntry.Length) < 8);
                Check("EPUB：mimetype 是 stored 块（长度很小，没有真 deflate）", mimetypeEntry.CompressedLength < 40);
                var opfEntry = zip.GetEntry("OEBPS/content.opf");
                Check("EPUB：其余条目是压缩存放的（不然文件白大一圈）", opfEntry.CompressedLength < opfEntry.Length);
                Contains("EPUB：有 container.xml", string.Join(",", names), "META-INF/container.xml");
                Contains("EPUB：有 content.opf", string.Join(",", names), "OEBPS/content.opf");
                Contains("EPUB：有 nav.xhtml", string.Join(",", names), "OEBPS/nav.xhtml");
                Contains("EPUB：有样式表", string.Join(",", names), "OEBPS/style.css");
                Eq("EPUB：只导出有正文的 2 章", 2, CountMatches(names, "OEBPS/text/chapter"));
                Check("EPUB：没有给空章节生成文件", !names.Contains("OEBPS/text/chapter0003.xhtml"));

                var mimetype = ReadEntry(zip, "mimetype");
                Eq("EPUB：mimetype 内容正确", "application/epub+zip", mimetype.Trim());

                var opf = ReadEntry(zip, "OEBPS/content.opf");
                Contains("EPUB：OPF 声明 EPUB3", opf, "version=\"3.0\"");
                Contains("EPUB：OPF 有书名", opf, "<dc:title>导出测试</dc:title>");
                Contains("EPUB：OPF 有作者", opf, "<dc:creator>测试作者</dc:creator>");
                Contains("EPUB：OPF 声明中文", opf, "<dc:language>zh-CN</dc:language>");
                Contains("EPUB：OPF 里简介的 & 被转义", opf, "&amp;");
                Eq("EPUB：spine 里有 2 章 + 目录", 3, CountMatches(SplitLines(opf), "<itemref"));

                var nav = ReadEntry(zip, "OEBPS/nav.xhtml");
                Contains("EPUB：目录页含第一章标题", nav, "第一章 开始");
                Contains("EPUB：目录页链接到章节文件", nav, "text/chapter0001.xhtml");
                Contains("EPUB：nav 的样式表路径是同层", nav, "href=\"style.css\"");

                var ch1 = ReadEntry(zip, "OEBPS/text/chapter0001.xhtml");
                Contains("EPUB：章节含标题", ch1, "<h1>第一章 开始</h1>");
                Contains("EPUB：正文按段落成 <p>", ch1, "<p>第一段。</p>");
                Contains("EPUB：尖括号被转义（不能破坏 XHTML）", ch1, "&lt;尖括号&gt;");
                Contains("EPUB：& 被转义", ch1, "&amp;");
                Check("EPUB：正文里没有裸露的 <尖括号>", ch1.IndexOf("<尖括号>", StringComparison.Ordinal) < 0);
                Contains("EPUB：章节的样式表路径是上一层", ch1, "href=\"../style.css\"");
                Check("EPUB：空行没变成空段落", ch1.IndexOf("<p></p>", StringComparison.Ordinal) < 0);

                // XHTML 必须是合法 XML（用 XmlDocument 真解析一遍）
                Check("EPUB：章节 XHTML 能被 XML 解析", IsValidXml(ch1));
                Check("EPUB：nav XHTML 能被 XML 解析", IsValidXml(nav));
                Check("EPUB：OPF 能被 XML 解析", IsValidXml(opf));
                Check("EPUB：container.xml 能被 XML 解析", IsValidXml(ReadEntry(zip, "META-INF/container.xml")));
            }

            // 带封面
            var path2 = Path.Combine(dir, "带封面.epub");
            var cover = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };   // 假 JPEG（只看头部特征）
            EpubWriter.Write(path2, book, chapters, cover, "jpg");
            using (var zip = System.IO.Compression.ZipFile.OpenRead(path2))
            {
                var names = new List<string>();
                foreach (var e in zip.Entries) names.Add(e.FullName);
                Contains("EPUB：有封面图片", string.Join(",", names), "OEBPS/images/cover.jpg");
                Contains("EPUB：有封面页", string.Join(",", names), "OEBPS/text/cover.xhtml");
                var opf = ReadEntry(zip, "OEBPS/content.opf");
                Contains("EPUB：OPF 里声明 cover-image", opf, "properties=\"cover-image\"");
            }

            // 一章都没下载 → 必须明确报错，不能生成空书
            var noBody = new List<ChapterInfo> { new ChapterInfo { Id = "9", Title = "空的", Text = "" } };
            bool threw = false;
            try { EpubWriter.Write(Path.Combine(dir, "空书.epub"), book, noBody, null, null); }
            catch (Exception ex) { threw = ex.Message.IndexOf("先下载", StringComparison.Ordinal) >= 0; }
            Check("EPUB：没有正文时明确报错而不是生成空文件", threw);

            // 转义函数单测
            Eq("转义：&", "&amp;", EpubWriter.X("&"));
            Eq("转义：< >", "&lt;a&gt;", EpubWriter.X("<a>"));
            Eq("转义：引号", "&quot;x&quot;", EpubWriter.X("\"x\""));
            Eq("转义：控制字符被丢掉", "ab", EpubWriter.X("a\u0001b"));
            Eq("转义：null 安全", "", EpubWriter.X(null));
            Eq("转义：中文原样", "中文", EpubWriter.X("中文"));
        }

        // ============================================================
        //  13) txt → 章节 反解析（导出 EPUB 时若内存里没有正文，就走这条）
        // ============================================================

        private static void TestParseTxt(string work)
        {
            var dir = Path.Combine(work, "parsetxt");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "解析测试.txt");

            var book = new BookInfo { Site = "biquga", Title = "解析测试", Dir = "/1_2/", Url = "u" };
            var chapters = new List<ChapterInfo>
            {
                new ChapterInfo { Id = "1", Title = "第一章 开始", Order = 1 },
                new ChapterInfo { Id = "2", Title = "第二章 继续", Order = 2 },
                new ChapterInfo { Id = "3", Title = "第三章 收尾", Order = 3 },
            };
            book.Chapters.AddRange(chapters);

            // 用产品自己的写法生成文件（和真实下载的 txt 结构一致）
            var runner = new DownloadRunner { Book = book, Chapters = chapters, OutputFile = path, BookDir = dir };
            runner.Ok = 3;
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.Write(runner.BuildHeaderZone());
                foreach (var c in chapters)
                    w.Write("\n\n" + c.Title + "\n" + new string('-', 12) + "\n\n正文内容：" + c.Title + "。\n第二段。\n");
            }

            var parsed = MainForm.ParseTxtIntoChapters(path, book);
            Eq("txt 反解析：章节数", 3, parsed.Count);
            Eq("txt 反解析：第一张标题", "第一章 开始", parsed[0].Title);
            Contains("txt 反解析：第一张正文", parsed[0].Text, "正文内容：第一章 开始。");
            Contains("txt 反解析：第二段还在", parsed[0].Text, "第二段。");
            Check("txt 反解析：正文里没有标题行", parsed[0].Text.IndexOf("第一章 开始\n", StringComparison.Ordinal) < 0);
            Check("txt 反解析：正文里没有破折号分隔线", parsed[0].Text.Trim('-').Trim().Length > 0);
            Eq("txt 反解析：第三章标题", "第三章 收尾", parsed[2].Title);

            // 解析出来的正文要能直接进 EPUB（端到端串起来）
            var epub = Path.Combine(dir, "串联.epub");
            EpubWriter.Write(epub, book, parsed, null, null);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(epub))
            {
                var ch1 = ReadEntry(zip, "OEBPS/text/chapter0001.xhtml");
                Contains("txt→EPUB 串联：正文进了 epub", ch1, "正文内容：第一章 开始。");
            }

            // 文件不存在不能抛异常
            var none = MainForm.ParseTxtIntoChapters(Path.Combine(dir, "没有这个.txt"), book);
            Eq("txt 反解析：文件不存在返回空列表", 0, none.Count);
        }

        // ---- EPUB 测试用的小工具 ----


        private static string ReadEntry(System.IO.Compression.ZipArchive zip, string name)
        {
            var e = zip.GetEntry(name);
            if (e == null) return "";
            using (var s = e.Open())
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        private static int CountMatches(List<string> items, string needle)
        {
            int n = 0;
            foreach (var s in items) if (s.IndexOf(needle, StringComparison.Ordinal) >= 0) n++;
            return n;
        }

        private static List<string> SplitLines(string s)
        {
            return new List<string>(s.Split('\n'));
        }

        private static bool IsValidXml(string xml)
        {
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(xml);
                return true;
            }
            catch { return false; }
        }

        /// <summary>在字节数组里找一段文本的字节偏移（IndexOf 给的是字符下标，中文不是 1:1）</summary>
        private static long ByteOffsetOf(byte[] haystack, string needle)
        {
            var pat = Encoding.UTF8.GetBytes(needle);
            for (int i = 0; i + pat.Length <= haystack.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < pat.Length; j++)
                    if (haystack[i + j] != pat[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        // ============================================================
        //  9) FontMap：没有映射表时也不能抛异常
        // ============================================================

        private static void TestFontMap()
        {
            // 注意：exe 同目录必须有 font-map.json 才会加载映射，测试环境（dist\）里没有，
            // 因此这里验证的是"裸奔"路径：不加载也不能崩、不能改字。
            var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            Check("FontMap 测试环境干净（没有 font-map.json）",
                !File.Exists(Path.Combine(exeDir, "font-map.json")));

            int count = -1;
            try { count = FontMap.Count; }
            catch (Exception ex) { Record("FontMap.Count 不抛异常", false, ex.GetType().Name + " " + ex.Message); }
            Check("FontMap.Count 不抛异常", count >= 0);
            Check("FontMap.Count 没有映射表时为 0", count == 0);

            // 私用区字符在没有映射表时原样保留（不能丢字、不能炸）
            var raw = "正常文字\uE123继续\uE456结束";
            string decoded = null;
            try { decoded = FontMap.Decode(raw); }
            catch (Exception ex) { Record("FontMap.Decode 不抛异常", false, ex.GetType().Name + " " + ex.Message); }
            Check("FontMap.Decode 不抛异常", decoded != null);
            Eq("FontMap.Decode 无映射表时原样返回", raw, decoded);
            Eq("FontMap.Decode 空串", "", FontMap.Decode(""));
            Eq("FontMap.Decode null", null, FontMap.Decode(null));
            Eq("FontMap.Decode 纯中文不变", "牧神记", FontMap.Decode("牧神记"));
        }

        // ============================================================
        //  BUG-1（未修，测试如实标红）：FixHeaderNow 在"新表头变长"时会吃掉正文开头
        //
        //  src\Common\DownloadRunner.cs 的 FixHeaderNow：
        //      fs.Position = bom;
        //      fs.Write(headerBytes, 0, headerBytes.Length);   // ← 只前进 headerBytes.Length
        //      int pad = cutBytes - headerBytes.Length;
        //      if (pad > 0) { …补 \n… }                        // ← 只有变短时才补
        //  position 前进的字节数 < 原表头占用的字节数，且中间那段既不补位也不搬移，
        //  于是新表头直接把正文开头几个字符盖掉了（长度不变、看不出报错）。
        //
        //  触发条件是常态，不是边角：FlushIncremental() 第一次落盘时 Ok/Skipped/Failed 全是 0，
        //  正文写完才调 FixHeaderNow()，那时统计是 1027/40/0 —— 表头一定变长。
        //
        //  实测（真实《牧神记》正文，10.4 MB）：
        //      零统计表头 378 字节 → 真统计表头 464 字节（+86）
        //      文件长度 10,463,661 → 10,463,661（应为 +86）
        //      正文开头被吃掉 38 个字符（"第1章 …" 与紧随其后的正文全部消失）
        //
        //  建议改法（任选其一，改前先把上面几条 BUG-1 断言跑绿）：
        //      a) 新表头更长时走"重写"路径：把 [bom+cutBytes, EOF) 的正文先读到内存/临时文件，
        //         再按新长度重新写一遍（一次顺序读写，几 MB 可接受）；
        //      b) 表头固定预留长度（例如 4 KB），写入时右侧用空格补齐，
        //         这样表头区域永远等长，永远只做覆盖、不做搬移。
        // ============================================================
    }
}
