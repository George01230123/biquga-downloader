using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
