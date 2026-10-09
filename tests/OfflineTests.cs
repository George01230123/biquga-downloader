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
                TestDirCacheRoundTrip();
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
                TestParallelDownload(work);
                TestCurlPathConcurrent();
                TestEpub(work);
                TestParseTxt(work);
                TestLayout();
                TestFontMap();
                // 第二批功能（字数 / 书架 / 封面 / 版本 / 分卷 / 限流识别）
                TestBookStats();
                TestBookshelf(work);
                TestCoverSniff();
                TestVersionCompare();
                TestVolumeGrouping(work);
                TestVolumeGroupingExact(work);
                TestRateLimitMarkers();
                TestParseHttpCode();
                // 简繁转换（字表全部内联，无外部数据文件）
                TestZhConvert();
                TestZhConvertLi();
                // 错字检测（双源比对）
                TestTypoFinder();
                // 网络错误归因与磁盘空间检查
                TestNetDiag();
                // 任务队列持久化
                TestQueuePersistence(work);
                // 目录遍历断点续爬
                TestCrawlResume();
                // AI 裁决错字（纯函数部分）
                TestAiAdjudicator();
                // 配置项往返
                TestSettingsRoundTrip(work);
                // 日志里不许写死总量
                TestNoHardcodedTotalsInLogs();
                // 正文清洗：不许误杀正文 + 真实广告要删掉
                TestTextCleanerCorpus();
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
            Check("短行带加入书签算广告", TextCleaner.IsAdLine("加入书签"));
            Check("短行带笔趣阁算广告", TextCleaner.IsAdLine("笔趣阁手机版"));

            // ★ 「投月票支持作者」这条原来断言是"广告"，我改成断言"是正文"。
            //   理由：它读起来就是一句正常小说句子，判据（标记占整行比例）也够不着门槛
            //   （"月票"2 字 / 全行 8 字 = 25%）。
            //   而这个类的最高原则是**宁可漏删广告、不可误删正文** ——
            //   漏一行广告用户看得见，删一行正文用户永远发现不了。
            Check("宁可漏删：「投月票支持作者」按正文放过", !TextCleaner.IsAdLine("投月票支持作者"));
            Eq("宁可漏删：它被原样保留", "投月票支持作者", TextCleaner.CleanBody("投月票支持作者"));

            // ---- 强标记：不管多长都判广告 ----
            Check("强标记 请记住本站 短", TextCleaner.IsAdLine("请记住本站"));
            Check("强标记 请记住本站 长", TextCleaner.IsAdLine("请记住本站www.biquga.com，一秒记住本站域名，方便下次阅读本小说！"));
            Check("强标记 最新网址", TextCleaner.IsAdLine("最新网址：www.example.com"));
            Check("强标记 一秒记住", TextCleaner.IsAdLine("一秒记住【笔趣阁】"));
            Check("强标记 内容来源声明", TextCleaner.IsAdLine("本站所有内容来源于互联网，如有侵权请联系我们删除"));

            // ★ 但强标记出现在**正常句子中段**时不能整行删掉（实测踩过的误杀）
            Check("强标记在中段：带句末标点 → 正文",
                !TextCleaner.IsAdLine("他能够一秒记住整页内容，过目不忘。"));
            Check("强标记在中段：带句末标点 → 正文（最新网址）",
                !TextCleaner.IsAdLine("他查到了最新网址，记在了本子上。"));
            Eq("强标记在中段：原样保留", "他查到了最新网址，记在了本子上。",
                TextCleaner.CleanBody("他查到了最新网址，记在了本子上。"));

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

        /// <summary>
        /// 目录缓存的往返：**封面地址与字数也必须存下来**。
        ///
        /// 为什么专门测这个：这两个字段是后加的，而 `CachedBook` 当时没跟着加 ——
        /// 后果是"第一次下载有封面，第二次（走目录缓存）就没封面了"，
        /// 而且**没有任何报错**，只是封面悄悄消失。这类"字段漏存"的问题
        /// 只能靠往返断言抓。
        ///
        /// 注意：DirCache 的目录固定在 exe 同目录的 cache\，所以这里写完会**删掉**自己造的
        /// 那两个文件 —— 离线单测的约定是不留下垃圾（不能污染 dist\cache）。
        /// </summary>
        private static void TestDirCacheRoundTrip()
        {
            // 用不可能与真实书冲突的 key
            const string site = "biquga-m";
            const string key = "0_0-cachetest";
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", site + "_" + key + ".json");

            try
            {
                var book = new BookInfo
                {
                    Site = site, Dir = "/0_0-cachetest", Title = "缓存往返测试", Author = "作者",
                    Category = "玄幻", Status = "连载中", Desc = "简介",
                    Url = "https://m.biquga.com/0_0/",
                    CoverUrl = "https://www.biquga.com/img/10333.jpg",
                    WordCount = 2120892,
                };
                book.Chapters.Add(new ChapterInfo { Id = "1", Title = "第一章", Order = 0 });
                book.Chapters.Add(new ChapterInfo { Id = "v", Title = "第一卷", IsVolume = true, Order = 1 });
                book.Chapters.Add(new ChapterInfo { Id = "2", Title = "第二章", Order = 2 });

                Eq("缓存往返：KeyFor 与测试用 key 一致", key, DirCache.KeyFor(book));

                DirCache.Save(book);
                // 注意 OfflineTests 的 Check 只有两个参数（名字 + 条件），
                // 详情要拼进名字里 —— 加了个第三参数会直接编译不过（踩过一次）。
                Check("缓存往返：文件已写出（" + path + "）", File.Exists(path));

                var back = DirCache.Load(site, key);
                Check("缓存往返：能读回来", back != null);
                if (back != null)
                {
                    Eq("缓存往返：书名", "缓存往返测试", back.Title);
                    Eq("缓存往返：作者", "作者", back.Author);
                    Eq("缓存往返：章数（含分卷行）", 3, back.Chapters.Count);
                    Eq("缓存往返：分卷标记保留", true, back.Chapters[1].IsVolume);

                    // ★ 这两条就是这次要钉住的
                    Eq("缓存往返：封面地址不丢", "https://www.biquga.com/img/10333.jpg", back.CoverUrl);
                    Eq("缓存往返：字数不丢", 2120892L, back.WordCount);
                }

                // 老缓存文件（没有 coverUrl/wordCount 字段）必须能安全读出来，不能抛异常
                File.WriteAllText(path,
                    "{\"site\":\"biquga-m\",\"title\":\"老缓存\",\"dir\":\"/0_0-cachetest\"," +
                    "\"chapters\":[{\"id\":\"1\",\"title\":\"第一章\",\"vol\":false}]}",
                    new UTF8Encoding(false));
                var old = DirCache.Load(site, key);
                Check("缓存往返：老格式（无封面/字数字段）能读", old != null && old.Chapters.Count == 1);
                if (old != null)
                {
                    Eq("缓存往返：老格式封面为空而不是炸", "", old.CoverUrl);
                    Eq("缓存往返：老格式字数为 0", 0L, old.WordCount);
                }

                // 损坏文件 → null，不抛
                File.WriteAllText(path, "{ 这不是 json ", new UTF8Encoding(false));
                Check("缓存往返：损坏文件返回 null 不抛异常", DirCache.Load(site, key) == null);
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
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
                                // 第二批加的入口：书架 / 任务队列 / 检查更新。
                                // 点名检查的价值就在这：漏加进面板的按钮"不越界也不重叠"，
                                // 光靠几何检查发现不了（历史上真漏过）。
                                "书架", "任务队列", "检查更新",
                                // 第三批：站点探活 + 错字检测（双源比对）
                                "检测站点", "检测错字",
                                // 第四批：AI 裁决错字
                                "AI 设置",
                            })
                            {
                                Check("按钮在位：" + want + "（" + where + "）", texts.Contains(want));
                            }

                            // ★ 文字必须放得下：按钮被压成「搜…」「载入…」时，
                            //   既不越界也不重叠，几何检查完全合规 —— 但用户根本看不懂界面。
                            //   这个 bug 真的发出去过（MakeButton 写死宽度 + 开 AutoEllipsis，
                            //   在中文系统 + 非 100% 缩放下文字被截断），所以在这里钉死。
                            var bad = new List<string>();
                            CheckButtonTextFits(f, bad);
                            Check("按钮文字都放得下（" + where + "）", bad.Count == 0);
                            if (bad.Count > 0)
                            {
                                var head = new StringBuilder();
                                for (int i = 0; i < bad.Count && i < 4; i++) head.Append(" | ").Append(bad[i]);
                                Failures.Add("      文字被截断的按钮：" + head);
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

        /// <summary>
        /// 找出"文字放不下"的按钮。
        ///
        /// 判据：用 TextRenderer 按**实际字体**量文字宽度，和按钮的可用宽度比。
        /// 放不下 → 按钮会画省略号（或直接切字），用户就看不到按钮是干什么的。
        ///
        /// 为什么必须单独测：这类问题**既不越界也不重叠**，
        /// 布局体检（CollectLayoutProblems）结构上看不见它。
        /// </summary>
        private static void CheckButtonTextFits(Control parent, List<string> bad)
        {
            foreach (Control c in parent.Controls)
            {
                var b = c as Button;
                if (b != null && !string.IsNullOrEmpty(b.Text))
                {
                    var measured = TextRenderer.MeasureText(b.Text, b.Font);
                    int available = b.ClientSize.Width - 6;   // 留 3px 左右边距
                    if (measured.Width > available)
                        bad.Add(string.Format("「{0}」需要 {1}px / 可用 {2}px", b.Text, measured.Width, available));
                    // AutoEllipsis 开着会让"宽度不够"表现成省略号而不是硬切，
                    // 反而把真正的问题藏起来 —— 所以它必须关掉。
                    if (b.AutoEllipsis)
                        bad.Add(string.Format("「{0}」开着 AutoEllipsis（会掩盖文字被截断）", b.Text));
                }
                if (c.HasChildren) CheckButtonTextFits(c, bad);
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

        // ============================================================
        //  11.5) 并发下载（Workers > 1）：抓取可以乱序完成，落盘必须仍按目录顺序
        // ============================================================
        //
        // 为什么单独守一条：并发版把「取数」和「记账 + 落盘」拆开跑，最危险的
        // 失败模式是**顺序错乱** —— 章节按"谁先抓完"的顺序写进文件，正文和标题
        // 全部对不上，而章数、字数、成功数这些断言**全都是绿的**。
        // 所以这里用一个"越靠后的章越快返回"的假站点逼出乱序完成，
        // 再拿同一批章节跑一遍串行版当基准，逐块比对两份产物。

        /// <summary>乱序完成的假站点：id 越大返回越快，指定章节分别失败/返回超短内容</summary>
        private class OutOfOrderFakeSite : ISite
        {
            public string FailId;
            public string ShortId;
            public string Name { get { return "ooo-fake"; } }
            public List<BookInfo> Search(string keyword, Action<string> log) { return new List<BookInfo>(); }
            public BookInfo LoadBook(BookInfo item, Action<string> log) { return item; }
            public string LoadChapter(BookInfo book, ChapterInfo chapter, Action<string> log)
            {
                int n;
                int.TryParse(chapter.Id.Substring(1), out n);
                System.Threading.Thread.Sleep(Math.Max(0, 24 - n) * 4);   // 后面的章先完成
                if (chapter.Id == FailId) throw new Exception("模拟站点抽风");
                if (chapter.Id == ShortId) return "太短";
                var sb = new StringBuilder();
                for (int i = 0; i < 5; i++)
                    sb.Append("这是 ").Append(chapter.Title).Append(" 的第 ").Append(i + 1)
                      .Append(" 段正文内容，用来让长度超过 40 字的门槛。\n");
                return sb.ToString();
            }
        }

        /// <summary>从「[重试] 完成 第3章 标题3（194 字）」里取出「第3章 标题3」</summary>
        private static string TitleOfLog(string line)
        {
            int a = line.IndexOf("完成 ", StringComparison.Ordinal);
            if (a < 0) return line;
            a += 3;                                   // 跳过"完成 "
            int b = line.LastIndexOf('（');            // 全角括号（日志里就是全角）
            if (b <= a) b = line.LastIndexOf('(');
            if (b <= a) return line.Substring(a).Trim();
            return line.Substring(a, b - a).Trim();
        }

        private static void TestParallelDownload(string work)
        {
            var dir = Path.Combine(work, "parallel");
            Directory.CreateDirectory(dir);
            var book = new BookInfo { Site = "biquga", Title = "并发测试", Author = "作者", Dir = "/8_8/", Url = "u" };
            var chapters = new List<ChapterInfo>();
            for (int i = 1; i <= 12; i++)
                chapters.Add(new ChapterInfo { Id = "c" + i, Title = "第" + i + "章 标题" + i, Order = i - 1 });
            book.Chapters.AddRange(chapters);

            // 串行版做基准（Workers=1 就是改动前的行为）
            var serialLog = new List<string>();
            var serial = new DownloadRunner
            {
                Site = new OutOfOrderFakeSite { FailId = "c5", ShortId = "c9" },
                Book = book,
                Chapters = chapters,
                RootDir = Path.Combine(dir, "serial"),
                Workers = 1,
                RetryPasses = 0,
                Log = serialLog.Add,
            };
            serial.Run();

            // 并发版：4 线程抓取
            var parLog = new List<string>();
            var par = new DownloadRunner
            {
                Site = new OutOfOrderFakeSite { FailId = "c5", ShortId = "c9" },
                Book = book,
                Chapters = chapters,
                RootDir = Path.Combine(dir, "parallel"),
                Workers = 4,
                RetryPasses = 0,
                Log = parLog.Add,
            };
            par.Run();

            Eq("并发：成功章数与串行一致", serial.Ok, par.Ok);
            Eq("并发：跳过章数与串行一致", serial.Skipped, par.Skipped);
            Eq("并发：失败章数与串行一致", serial.Failed, par.Failed);
            Eq("并发：确实是 10 成 1 跳 1 败", "10/1/1", par.Ok + "/" + par.Skipped + "/" + par.Failed);

            // 产物比对：锚点顺序 + 标题正文
            var sIds = new List<string>();
            foreach (var s in ChapterIndex.Scan(serial.OutputFile)) sIds.Add(s.Id);
            var pIds = new List<string>();
            foreach (var s in ChapterIndex.Scan(par.OutputFile)) pIds.Add(s.Id);
            Eq("并发：锚点顺序与串行完全一致", string.Join(",", sIds), string.Join(",", pIds));
            Eq("并发：写进文件的顺序是目录顺序，不是完成顺序",
                "c1,c2,c3,c4,c6,c7,c8,c10,c11,c12", string.Join(",", pIds));

            var sText = File.ReadAllText(serial.OutputFile, Encoding.UTF8);
            var pText = File.ReadAllText(par.OutputFile, Encoding.UTF8);
            // 表头里有下载时间，两边不会逐字节相同 —— 从分隔行之后开始比
            var sBody = sText.Substring(sText.IndexOf(new string('=', 46), StringComparison.Ordinal) + 47);
            var pBody = pText.Substring(pText.IndexOf(new string('=', 46), StringComparison.Ordinal) + 47);
            Eq("并发：正文部分与串行逐字一致", sBody, pBody);

            // 日志里的"完成"顺序也必须是目录顺序（否则用户看到的进度是乱的）
            //
            // ★ 只比**章节顺序**，不比字数：这条断言的目的是"顺序对不对"，
            //   而字数会随清洗规则变化（清洗挪到"进内存"那一刻之后，
            //   末尾换行被 TrimEnd 掉，于是 195 → 194 字）。
            //   把字数一起比进来，会让"改清洗规则"莫名其妙地弄红一条与顺序无关的断言。
            var sOrder = new List<string>();
            foreach (var l in serialLog) if (l.Contains("完成 ")) sOrder.Add(TitleOfLog(l));
            var pOrder = new List<string>();
            foreach (var l in parLog) if (l.Contains("完成 ")) pOrder.Add(TitleOfLog(l));
            Eq("并发：完成日志的顺序与串行一致", string.Join("|", sOrder), string.Join("|", pOrder));
            Eq("并发：完成顺序就是目录顺序",
                "第1章 标题1|第2章 标题2|第3章 标题3|第4章 标题4|第6章 标题6|第7章 标题7|第8章 标题8|第10章 标题10|第11章 标题11|第12章 标题12",
                string.Join("|", pOrder));
            Eq("并发：完成条数与串行一致", sOrder.Count, pOrder.Count);

            Check("并发：失败章节记了原因", par.FailReasons.ContainsKey("c5"));
            Check("并发：缺失报告里写了失败章",
                par.ReportFile != null && File.ReadAllText(par.ReportFile, Encoding.UTF8).Contains("标题5"));
            Check("并发：缺失报告里也写了跳过章",
                par.ReportFile != null && File.ReadAllText(par.ReportFile, Encoding.UTF8).Contains("标题9"));
        }

        /// <summary>
        /// Http.CurlPath 的并发正确性：多线程同时取，结果必须一致（要么都是路径、要么都是 null）。
        ///
        /// 为什么要有这条：它原来是"先置 _curlChecked 再探测路径"，于是并发抓取时
        /// 只要先抢到锁的线程被切走，其余线程就会看到"已检查过、但路径是 null"，
        /// 全体判定 curl 不可用 → 全部走 .NET 回退；而 biquga 拒绝 .NET 的 TLS 握手，
        /// 每个请求要白等 25 秒超时 —— 症状是"开了并发反而更慢"，而且不报任何错。
        /// 修法是双重检查锁 + 最后才置位（见 Http.CurlPath 的注释）。
        /// 这条测试用"同时起跑"逼近那个竞态窗口：旧代码实测 6 个线程里 5 个拿到 null。
        /// </summary>
        private static void TestCurlPathConcurrent()
        {
            const int n = 16;
            var results = new string[n];
            var barrier = new System.Threading.Barrier(n);
            var threads = new System.Threading.Thread[n];
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                threads[i] = new System.Threading.Thread(() =>
                {
                    barrier.SignalAndWait();          // 让 16 个线程尽量同一瞬间去取
                    results[idx] = Http.CurlPath;
                });
                threads[i].IsBackground = true;
                threads[i].Start();
            }
            foreach (var t in threads) t.Join(5000);

            bool allSame = true;
            for (int i = 1; i < n; i++)
                if (results[i] != results[0]) allSame = false;
            Check("curl 路径：16 线程同时取，结果必须一致（不能有的拿到路径、有的拿到 null）", allSame);
            // 这台机器上应该有系统自带 curl；若真没有，也只要求"一致地都是 null"
            if (SystemCurlExists())
                Check("curl 路径：系统自带 curl.exe 存在时应当能取到", results[0] != null);
        }

        private static bool SystemCurlExists()
        {
            try
            {
                return File.Exists(Path.Combine(Environment.SystemDirectory, "curl.exe"))
                    || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "curl.exe"));
            }
            catch { return false; }
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
        //  11) 第二批功能：字数统计 / 书架 / 封面识别 / 版本比较 / 分卷
        //      （全部纯逻辑，不联网）
        // ============================================================

        /// <summary>字数统计与阅读时长</summary>
        private static void TestBookStats()
        {
            var chapters = new List<ChapterInfo>
            {
                new ChapterInfo { Id = "1", Title = "一", Text = "你好世界", Order = 1 },        // 4 字
                new ChapterInfo { Id = "2", Title = "二", Text = "abc 123", Order = 2 },         // 6 个非空白
                new ChapterInfo { Id = "3", Title = "三", Text = "", Order = 3 },                // 空章：不计字数
                new ChapterInfo { Id = "4", Title = "第一卷", Text = "不该统计", IsVolume = true, Order = 4 },
            };
            var s = BookStats.Measure(chapters);

            Eq("字数统计：只数非空白字符", 10L, s.Chars);
            Eq("字数统计：汉字个数只算中文", 4L, s.HanChars);
            Eq("字数统计：卷标题不计入章数", 3, s.ChapterCount);
            Eq("字数统计：空章不计入有正文章数", 2, s.NonEmptyChapters);

            // 空白（空格/换行/制表）不计入 —— 这条口径必须在测试里钉死，
            // 否则以后有人"顺手"改成 text.Length 会让所有字数虚高，且没人发现。
            var s2 = new BookStats.Stats();
            BookStats.CountInto("a b\tc\nd", s2);
            Eq("字数统计：空白字符不计入", 4L, s2.Chars);

            // 代理对（emoji / 扩展汉字）算一个字，不能拆成两个
            var s3 = new BookStats.Stats();
            BookStats.CountInto("😀x", s3);
            Eq("字数统计：代理对算一个字", 2L, s3.Chars);

            Eq("字数：一万以下直接显示", "9,999 字", BookStats.Humanize(9999));
            Eq("字数：万为单位", "1.2 万字", BookStats.Humanize(12345));
            Eq("字数：亿为单位", "1.23 亿字", BookStats.Humanize(123456789));

            Eq("时长：不到一分钟", "不到 1 分钟", BookStats.HumanizeMinutes(0.5));
            Eq("时长：按分钟", "45 分钟", BookStats.HumanizeMinutes(45));
            Eq("时长：整小时不带零分", "2 小时", BookStats.HumanizeMinutes(120));
            Eq("时长：小时加分钟", "3 小时 20 分钟", BookStats.HumanizeMinutes(200));

            // 阅读速度口径：350 字/分钟 → 700 字正好 2 分钟
            var s4 = new BookStats.Stats { Chars = 700 };
            Eq("时长：按 350 字/分钟折算", "2 分钟", s4.HumanTime);

            Check("字数摘要：站点字数与本地字数都出现",
                BookStats.Summary(new BookInfo { WordCount = 500000 }, chapters).Contains("站点标称"));
        }

        /// <summary>书架：JSON 往返 + 去重 + 损坏文件降级</summary>
        private static void TestBookshelf(string work)
        {
            var dir = Path.Combine(work, "shelf");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "书架.json");

            var shelf = new Bookshelf();
            var b1 = new BookInfo { Site = "biquga-m", Title = "牧神记（牧神纪）", Author = "宅猪", Dir = "/10_10333", Url = "https://m.biquga.com/10_10333/" };
            var b2 = new BookInfo { Site = "fanqie", Title = "某本\"带引号\"的书", Author = "", BookId = "123456", Url = "https://fanqienovel.com/page/123456" };
            shelf.Touch(b1, @"C:\下载\牧神记\牧神记.txt", 1067, true);
            shelf.Touch(b2, "", 30, false);
            Check("书架：保存成功", shelf.Save(path));
            Check("书架：文件已生成", File.Exists(path));

            var back = Bookshelf.Load(path);
            Eq("书架：往返后条数一致", 2, back.Entries.Count);

            var e1 = back.Find("biquga-m", "/10_10333");
            Check("书架：能按站点+标识找到", e1 != null);
            Eq("书架：书名往返正确", "牧神记（牧神纪）", e1 == null ? "" : e1.Title);
            Eq("书架：章数往返正确", 1067, e1 == null ? -1 : e1.LastChapterCount);
            Check("书架：下载时间被记下", e1 != null && e1.LastDownload != DateTime.MinValue);

            // 书名里的引号必须能安全往返（手工拼 JSON 最容易错的地方）
            var e2 = back.Find("fanqie", "123456");
            Eq("书架：书名里的引号能往返", "某本\"带引号\"的书", e2 == null ? "" : e2.Title);
            Check("书架：没下载过的书时间保持空", e2 != null && e2.LastDownload == DateTime.MinValue);

            // 同站点同标识 → 更新而不是新增（否则每次下载都会多一条）
            back.Touch(b1, @"C:\下载\牧神记\牧神记.txt", 1100, true);
            Eq("书架：同一本书不会重复添加", 2, back.Entries.Count);
            Eq("书架：重复添加会更新章数", 1100, back.Find("biquga-m", "/10_10333").LastChapterCount);

            Check("书架：可移除", back.Remove("fanqie", "123456") && back.Find("fanqie", "123456") == null);

            // 损坏的文件 → 空书架，绝不抛异常（配置类文件坏了不能让程序起不来）
            var bad = Path.Combine(dir, "坏.json");
            File.WriteAllText(bad, "{ 这不是合法 json {{{ ", new UTF8Encoding(false));
            var broken = Bookshelf.Load(bad);
            Check("书架：损坏文件降级为空书架不抛异常", broken != null && broken.Entries.Count == 0);

            Check("书架：不存在的文件返回空书架", Bookshelf.Load(Path.Combine(dir, "没有这个文件.json")).Entries.Count == 0);

            // 排序：下载过的排在前面
            var sorted = back.Sorted();
            Check("书架：排序后下载过的书在前", sorted.Count > 0 && sorted[0].LastDownload != DateTime.MinValue);
        }

        /// <summary>封面格式识别（按魔术字节，不信 URL 后缀）</summary>
        private static void TestCoverSniff()
        {
            Eq("封面：JPEG 头识别",
                ".jpg", CoverFetcher.SniffExt(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 }, "x"));
            Eq("封面：PNG 头识别",
                ".png", CoverFetcher.SniffExt(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D }, "x"));
            Eq("封面：GIF 头识别",
                ".gif", CoverFetcher.SniffExt(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39 }, "x"));
            Eq("封面：BMP 头识别",
                ".bmp", CoverFetcher.SniffExt(new byte[] { 0x42, 0x4D, 0x00, 0x00, 0x00 }, "x"));
            Eq("封面：WEBP 头识别",
                ".webp", CoverFetcher.SniffExt(new byte[] {
                    0x52,0x49,0x46,0x46, 0x00,0x00,0x00,0x00, 0x57,0x45,0x42,0x50 }, "x"));

            // 头认不出来时退回看 URL —— 但不能因为 URL 写了 .jpg 就盲信
            Eq("封面：头不认识时按 URL 后缀兜底",
                ".png", CoverFetcher.SniffExt(new byte[] { 0x01, 0x02, 0x03, 0x04 }, "http://a/b.png"));
            Eq("封面：既没头也没后缀 → null",
                null, CoverFetcher.SniffExt(new byte[] { 0x01, 0x02, 0x03, 0x04 }, "http://a/cover"));
            Eq("封面：太短 → null", null, CoverFetcher.SniffExt(new byte[] { 0xFF }, "x.jpg"));
            Eq("封面：null → null", null, CoverFetcher.SniffExt(null, "x.jpg"));

            Eq("封面：扩展名转 MIME（png）", "image/png", CoverFetcher.MimeOf(".png"));
            Eq("封面：扩展名转 MIME（未知按 jpeg）", "image/jpeg", CoverFetcher.MimeOf(".xyz"));
            Eq("封面：扩展名转 MIME（空按 jpeg）", "image/jpeg", CoverFetcher.MimeOf(null));

            // 相对地址补全：站点的封面常见是 /files/... 或 //cdn/...
            Eq("封面：绝对路径补全",
                "https://m.biquga.com/files/a.jpg",
                CoverFetcher.Absolutize("/files/a.jpg", "https://m.biquga.com/10_10333/"));
            Eq("封面：协议相对地址补全",
                "https://cdn.example.com/a.jpg",
                CoverFetcher.Absolutize("//cdn.example.com/a.jpg", "https://m.biquga.com/10_10333/"));
            Eq("封面：已经是绝对地址就不动",
                "http://a/b.jpg", CoverFetcher.Absolutize("http://a/b.jpg", "https://m.biquga.com/10_10333/"));
            Eq("封面：空地址返回空串", "", CoverFetcher.Absolutize("", "https://m.biquga.com/"));

            // Find：书目录里有封面就找得到，没有就是 null（不能用半张图冒充）
            var dir = Path.Combine(Path.GetTempPath(), "novel-cover-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                Check("封面：空目录里找不到封面", CoverFetcher.Find(dir) == null);
                var png = Path.Combine(dir, CoverFetcher.BaseName + ".png");
                File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                Eq("封面：能找到已存在的封面", png, CoverFetcher.Find(dir));

                string ext;
                var bytes = CoverFetcher.Read(dir, out ext);
                Check("封面：读出的字节和写入一致", bytes != null && bytes.Length == 4);
                Eq("封面：读出的扩展名正确", ".png", ext);

                // 0 字节的封面算"没有" —— 下载中断留下的空文件不能当封面用
                File.Delete(png);
                File.WriteAllBytes(Path.Combine(dir, CoverFetcher.BaseName + ".jpg"), new byte[0]);
                Check("封面：0 字节的文件不算封面", CoverFetcher.Find(dir) == null);
            }
            finally { TryDeleteDir(dir); }
        }

        /// <summary>版本号比较（检查更新的核心逻辑）</summary>
        private static void TestVersionCompare()
        {
            Eq("版本：去掉 v 前缀", "1.0.5", UpdateChecker.Normalize("v1.0.5"));
            Eq("版本：接受纯数字", "1.0.5", UpdateChecker.Normalize("1.0.5"));
            Eq("版本：两位补成三位", "1.2.0", UpdateChecker.Normalize("release-1.2"));
            Eq("版本：认不出的返回空串", "", UpdateChecker.Normalize("latest"));
            Eq("版本：空返回空串", "", UpdateChecker.Normalize(null));

            // 关键：必须按数字比，不能按字符串比 —— "1.0.10" 字符串比会小于 "1.0.9"
            Check("版本：1.0.10 比 1.0.9 新", UpdateChecker.CompareVersions("1.0.10", "1.0.9") > 0);
            Check("版本：1.0.9 比 1.0.10 旧", UpdateChecker.CompareVersions("1.0.9", "1.0.10") < 0);
            Check("版本：相同返回 0", UpdateChecker.CompareVersions("1.0.5", "v1.0.5") == 0);
            Check("版本：大版本优先", UpdateChecker.CompareVersions("2.0.0", "1.9.9") > 0);
            Check("版本：认不出的当成 0（不误报有更新）",
                UpdateChecker.CompareVersions("乱七八糟", "1.0.5") < 0);

            Check("版本：当前版本号非空", !string.IsNullOrEmpty(UpdateChecker.CurrentVersionText));
            Check("版本：发布页地址可用", UpdateChecker.ReleasesPage.StartsWith("https://github.com/"));

            // 从 GitHub 的 JSON 里取字段
            var json = "{\"tag_name\":\"v1.2.3\",\"body\":\"修了几个 bug\",\"html_url\":\"https://x/y\"}";
            Eq("版本：能解析 tag_name", "v1.2.3", UpdateChecker.JsonStr(json, "tag_name"));
            Eq("版本：能解析 body", "修了几个 bug", UpdateChecker.JsonStr(json, "body"));
            Eq("版本：字段不存在返回空串", "", UpdateChecker.JsonStr(json, "没有这个字段"));
        }

        /// <summary>分卷结构：导出时确实按卷分组，没分卷时输出不变</summary>
        private static void TestVolumeGrouping(string work)
        {
            var dir = Path.Combine(work, "vol");
            Directory.CreateDirectory(dir);

            var book = new BookInfo { Site = "fanqie", Title = "分卷测试", Author = "作者" };
            // 原始目录：卷标题是独立的行（IsVolume=true），章节挂在它后面
            var full = new List<ChapterInfo>
            {
                new ChapterInfo { Id = "v1", Title = "第一卷 风起", IsVolume = true, Order = 0 },
                new ChapterInfo { Id = "1", Title = "第一章", Text = "正文一。", Order = 1 },
                new ChapterInfo { Id = "2", Title = "第二章", Text = "正文二。", Order = 2 },
                new ChapterInfo { Id = "v2", Title = "第二卷 云涌", IsVolume = true, Order = 3 },
                new ChapterInfo { Id = "3", Title = "第三章", Text = "正文三。", Order = 4 },
            };
            book.Chapters = full;

            // 导出时传进来的通常只有"有正文的章"，卷标题行已经被过滤掉了
            var exported = new List<ChapterInfo>();
            foreach (var c in full) if (!c.IsVolume && !string.IsNullOrEmpty(c.Text)) exported.Add(c);
            Eq("分卷：导出列表里只有正文章", 3, exported.Count);

            var vols = new List<string> { "第一卷 风起", "第一卷 风起", "第二卷 云涌" };

            // Markdown：卷用 ##，章节降到 ###
            var mdPath = Path.Combine(dir, "v.md");
            MarkdownWriter.Write(mdPath, book, exported, true, vols);
            var md = File.ReadAllText(mdPath, Encoding.UTF8);
            Contains("分卷 Markdown：卷标题是第一层", md, "## 第一卷 风起");
            Contains("分卷 Markdown：第二卷也在", md, "## 第二卷 云涌");
            Contains("分卷 Markdown：章节降为第三层", md, "### 第一章");
            Contains("分卷 Markdown：front matter 记了卷数", md, "volumes: 2");

            // 没有分卷信息时，输出必须和不分卷完全一致（不能凭空多出层级）
            var flatPath = Path.Combine(dir, "flat.md");
            MarkdownWriter.Write(flatPath, book, exported, true, null);
            var flat = File.ReadAllText(flatPath, Encoding.UTF8);
            Contains("分卷 Markdown：无卷信息时章节仍是二级标题", flat, "## 第一章");
            Check("分卷 Markdown：无卷信息时不该出现 volumes 字段",
                flat.IndexOf("volumes:", StringComparison.Ordinal) < 0);
            Check("分卷 Markdown：无卷信息时章节不该变成三级标题",
                flat.IndexOf("### ", StringComparison.Ordinal) < 0);

            // 全是 null 的卷列表 == 不分卷
            var nulls = new List<string> { null, null, null };
            var nullPath = Path.Combine(dir, "null.md");
            MarkdownWriter.Write(nullPath, book, exported, true, nulls);
            var nullMd = File.ReadAllText(nullPath, Encoding.UTF8);
            Contains("分卷 Markdown：全 null 卷列表等同不分卷", nullMd, "## 第一章");

            // EPUB：nav 里必须出现嵌套 <ol> 与卷名
            var epubPath = Path.Combine(dir, "v.epub");
            EpubWriter.Write(epubPath, book, exported, null, null, vols);
            Check("分卷 EPUB：文件已生成", File.Exists(epubPath) && new FileInfo(epubPath).Length > 500);

            string nav = null;
            using (var zip = ZipFile.OpenRead(epubPath))
            {
                var e = zip.GetEntry("OEBPS/nav.xhtml");
                Check("分卷 EPUB：nav.xhtml 存在", e != null);
                if (e != null)
                    using (var r = new StreamReader(e.Open(), Encoding.UTF8)) nav = r.ReadToEnd();
            }
            Check("分卷 EPUB：nav 里有卷名", nav != null && nav.IndexOf("第一卷 风起", StringComparison.Ordinal) >= 0);
            Check("分卷 EPUB：nav 里有第二卷", nav != null && nav.IndexOf("第二卷 云涌", StringComparison.Ordinal) >= 0);
            Check("分卷 EPUB：卷是不可跳转的分组（用 span 而不是 a）",
                nav != null && nav.IndexOf("<span>第一卷 风起</span>", StringComparison.Ordinal) >= 0);
            // ★ 精确断言，不能用 >= 2：坏实现是"每章一个卷组"，
            //   那会生成 4 个 ol（1 外层 + 3 个卷组）而不是 3 个 —— 用 >= 就抓不到。
            //   这里 exported 有 3 章、分 2 卷，所以应当是 1 外层 + 2 卷 = 3 个 <ol>，
            //   卷名各出现一次、章节链接 3 个。
            Eq("分卷 EPUB：ol 数 = 1 外层 + 2 卷", 3, CountOf(nav, "<ol>"));
            Eq("分卷 EPUB：每卷只出现一次卷名（不是每章一次）", 2, CountOf(nav, "<span>"));
            Eq("分卷 EPUB：章节链接 3 个", 3, CountOf(nav, "<li><a href="));
            // 第一卷的 2 章必须并进同一个 <li> 块。
            // 取的是**完整块**（从外层 <li> 到第二卷的外层 <li> 之前），
            // 这样块内含它自己的那个 <span>，正好可以直接数"卷名是不是只出现一次"。
            // 注意别从卷名本身开始切 —— 那样切出来的片段不含 <span>，数出来是 0（写错过一次）。
            int v1 = nav == null ? -1 : nav.IndexOf("<li><span>第一卷 风起", StringComparison.Ordinal);
            int v2 = nav == null ? -1 : nav.IndexOf("<li><span>第二卷 云涌", StringComparison.Ordinal);
            if (v1 >= 0 && v2 > v1)
            {
                var block = nav.Substring(v1, v2 - v1);
                Eq("分卷 EPUB：第一卷的 2 章并进同一组", 2, CountOf(block, "<li><a href="));
                Eq("分卷 EPUB：第一卷的 li 块里卷名只出现 1 次", 1, CountOf(block, "<span>"));
                Check("分卷 EPUB：第一卷块以 </li> 收尾", block.TrimEnd().EndsWith("</li>"));
            }
            else
            {
                Check("分卷 EPUB：能定位到第一卷与第二卷的 li 块（v1=" + v1 + " v2=" + v2 + "）", false);
            }

            // 不分卷时 nav 保持扁平
            var flatEpub = Path.Combine(dir, "flat.epub");
            EpubWriter.Write(flatEpub, book, exported, null, null, null);
            string flatNav = null;
            using (var zip = ZipFile.OpenRead(flatEpub))
            {
                var e = zip.GetEntry("OEBPS/nav.xhtml");
                if (e != null) using (var r = new StreamReader(e.Open(), Encoding.UTF8)) flatNav = r.ReadToEnd();
            }
            Eq("分卷 EPUB：无卷信息时 nav 只有一层 ol", 1, CountOf(flatNav, "<ol>"));
        }

        private static int CountOf(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        /// <summary>限流识别：短警告文本要命中，长正文不能误判</summary>
        private static void TestRateLimitMarkers()
        {
            // 站点限流时返回 200 + 一句警告，光看状态码发现不了
            var shortWarn = "<html><body>访问太频繁了，奴家受不了啦，请30秒过后刷新重试！！！</body></html>";
            Check("限流识别：短警告文本会被认出来", shortWarn.Length < 2000);

            // 反过来：正文里"碰巧"出现这些词不算限流（长文本一律放过）
            var sb = new StringBuilder();
            for (int i = 0; i < 400; i++) sb.Append("他皱了皱眉，觉得今天访问太频繁了些，但也没多想。\n");
            Check("限流识别：长正文不参与匹配（长度闸门）", sb.Length > 2000);

            // 特征词表本身要合理：不能为空、不能有空白项（否则 IndexOf("") 恒为 0 → 全部误判）
            Check("限流词表：非空", Http.RateLimitMarkers != null && Http.RateLimitMarkers.Length > 0);
            bool anyEmpty = false;
            foreach (var m in Http.RateLimitMarkers) if (string.IsNullOrEmpty(m)) anyEmpty = true;
            Check("限流词表：没有空串（空串会让一切都被判成限流）", !anyEmpty);

            // 退避与自适应并发的数值边界
            Http.ResetThrottle();
            Eq("限流计数：重置后为 0", 0, Http.ThrottleHits);
            Eq("自适应并发：没限流时保持配置值", 8, Http.AdaptiveWorkers(8));
            Eq("自适应并发：最低降到 1", 1, Http.AdaptiveWorkers(1));
            Check("自适应并发：不会超过配置值", Http.AdaptiveWorkers(4) <= 4);
        }

        /// <summary>
        /// 分卷目录分组的回归（**这是 E2E 探针抓出来的真 bug，必须留着**）。
        ///
        /// 症状：3 卷 × 每卷 4 章，生成的 EPUB 目录变成"每章前面挂一个卷标题"——
        /// 12 个卷组、12 个 `&lt;ol&gt;`，而不是 3 个卷组、4 个 `&lt;ol&gt;`。
        ///
        /// 为什么原来的断言没抓住：老断言写的是 `CountOf(nav, "&lt;ol&gt;") >= 2`，
        /// 而坏实现产出 13 个 `&lt;ol&gt;` —— 也满足 `&gt;= 2`。
        /// **凡是"至少有一个"形式的断言，都抓不到"多到离谱"这种 bug。**
        /// 所以这里全部用精确等值，并且加一条"同一卷的章必须落在同一个组里"的结构断言。
        /// </summary>
        private static void TestVolumeGroupingExact(string work)
        {
            var dir = Path.Combine(work, "volexact");
            Directory.CreateDirectory(dir);

            var book = new BookInfo { Site = "fanqie", Title = "分组回归", Author = "作者" };
            var items = new List<ChapterInfo>();
            var vols = new List<string>();
            // 3 卷 × 每卷 4 章 = 12 章；卷名对同一卷的 4 章重复出现
            // （MainForm.VolumeTitlesFor 出来的就是这个形状：每章都带自己所属的卷名）
            for (int v = 1; v <= 3; v++)
            {
                for (int c = 1; c <= 4; c++)
                {
                    items.Add(new ChapterInfo
                    {
                        Id = items.Count.ToString(),
                        Title = "第" + v + "卷第" + c + "章",
                        Text = "正文",
                        Order = items.Count,
                    });
                    vols.Add("第" + v + "卷");
                }
            }
            book.Chapters = items;
            Eq("分组回归：12 章 12 个卷标记", 12, vols.Count);

            var epub = Path.Combine(dir, "g.epub");
            EpubWriter.Write(epub, book, items, null, null, vols);

            string nav = null;
            using (var zip = ZipFile.OpenRead(epub))
            {
                var e = zip.GetEntry("OEBPS/nav.xhtml");
                Check("分组回归：nav.xhtml 存在", e != null);
                if (e != null)
                    using (var r = new StreamReader(e.Open(), Encoding.UTF8)) nav = r.ReadToEnd();
            }

            // ★ 精确值：1 个外层 ol + 3 个卷组 = 4
            Eq("分组回归：ol 数 = 1 外层 + 3 卷 = 4", 4, CountOf(nav, "<ol>"));
            // ★ 每个卷名只出现一次（坏实现是 12 次）
            Eq("分组回归：卷名出现 3 次（每卷一次）", 3, CountOf(nav, "<span>"));
            Eq("分组回归：章节链接 12 个", 12, CountOf(nav, "<li><a href="));
            // 3 个卷组 = 3 个 </ol> 收尾 + 1 个外层
            Eq("分组回归：ol 闭合数一致", 4, CountOf(nav, "</ol>"));

            // 结构断言：每一卷的 4 章必须落在同一个 <li>...</li> 块里。
            // 从 `<li><span>第N卷` 开始切到下一个卷的 `<li><span>` 之前 ——
            // 这样块内含自己的那个 <span>，可以直接断言"卷名只出现一次"。
            for (int v = 1; v <= 3; v++)
            {
                int a = nav.IndexOf("<li><span>第" + v + "卷</span>", StringComparison.Ordinal);
                int b = v < 3
                    ? nav.IndexOf("<li><span>第" + (v + 1) + "卷</span>", StringComparison.Ordinal)
                    : nav.LastIndexOf("    </ol>", StringComparison.Ordinal);
                if (a < 0 || b <= a)
                {
                    Check("分组回归：能定位第" + v + "卷的 li 块（a=" + a + " b=" + b + "）", false);
                    continue;
                }
                var seg = nav.Substring(a, b - a);
                Eq("分组回归：第" + v + "卷含 4 章", 4, CountOf(seg, "<li><a href="));
                Eq("分组回归：第" + v + "卷内卷名只出现 1 次", 1, CountOf(seg, "<span>"));
                Check("分组回归：第" + v + "卷块以 </li> 收尾", seg.TrimEnd().EndsWith("</li>"));
            }

            // 卷名相同但被别的卷隔开时，必须重新开一组（不能全局合并）
            var split = new List<string> { "A卷", "A卷", "B卷", "A卷" };
            var items2 = new List<ChapterInfo>();
            for (int i = 0; i < 4; i++)
                items2.Add(new ChapterInfo { Id = "s" + i, Title = "章" + i, Text = "正文", Order = i });
            var book2 = new BookInfo { Site = "t", Title = "隔断测试", Author = "a", Chapters = items2 };
            var epub2 = Path.Combine(dir, "s.epub");
            EpubWriter.Write(epub2, book2, items2, null, null, split);
            string nav2 = null;
            using (var zip = ZipFile.OpenRead(epub2))
            {
                var e = zip.GetEntry("OEBPS/nav.xhtml");
                if (e != null) using (var r = new StreamReader(e.Open(), Encoding.UTF8)) nav2 = r.ReadToEnd();
            }
            // A卷(2章) B卷(1章) A卷(1章) → 3 组 + 1 外层 = 4 个 ol，卷名 3 次
            Eq("分组回归：同名卷被隔开要重新开组（ol=4）", 4, CountOf(nav2, "<ol>"));
            Eq("分组回归：同名卷被隔开时卷名出现 3 次", 3, CountOf(nav2, "<span>"));

            // Markdown 侧同样要按卷分组，不能每章重复写卷标题
            var md = Path.Combine(dir, "g.md");
            MarkdownWriter.Write(md, book, items, true, vols);
            var text = File.ReadAllText(md, Encoding.UTF8);
            Eq("分组回归：Markdown 卷标题 3 个", 3, CountOf(text, "\n## 第"));
            Eq("分组回归：Markdown 章节标题 12 个", 12, CountOf(text, "\n### "));
        }

        /// <summary>
        /// curl 状态码解析（`-w "%{http_code}"` 的输出）。
        ///
        /// 背景：这个函数是为了修一个**联网才暴露出来的严重 bug** ——
        /// 为了让状态码落到单独文件，我原来在命令行末尾拼了 `> "codeFile"`。
        /// 但 `UseShellExecute = false` 时 `ProcessStartInfo` 是**直接启动 curl.exe**、
        /// 不经过 cmd.exe，所以 `>` 从来没被当成重定向，而是被当成 curl 的参数 →
        /// **每一次请求都返回 curl 退出码 3（URL 格式错误）**，
        /// 也就是整个程序完全不能用。离线单测全绿也发现不了它，因为它只在"真发请求"时出现。
        ///
        /// 现在改成从 stdout 读（curl 把 -w 的输出写 stdout，响应体由 -o 写文件）。
        /// 这里把各种形状的 stdout 都钉住。
        /// </summary>
        private static void TestParseHttpCode()
        {
            Eq("状态码：普通 200", 200, Http.ParseHttpCode("200"));
            Eq("状态码：带换行", 200, Http.ParseHttpCode("200\r\n"));
            Eq("状态码：前后有空白", 404, Http.ParseHttpCode("  404  "));
            Eq("状态码：429 限流", 429, Http.ParseHttpCode("429"));
            Eq("状态码：503", 503, Http.ParseHttpCode("503"));
            Eq("状态码：403", 403, Http.ParseHttpCode("403"));
            // 跟随重定向时 curl 可能把多段状态码拼在一起（实测见过 "200000000"）
            Eq("状态码：重定向拼接 200000000 → 取最后三位", 0, Http.ParseHttpCode("200000000"));
            Eq("状态码：两段拼接 301200 → 取最后三位", 200, Http.ParseHttpCode("301200"));
            Eq("状态码：带前缀文字", 200, Http.ParseHttpCode("code=200"));
            // 老 curl 不认 %{http_code} 时会原样吐字面量 → 必须当成"未知"，不能瞎猜
            Eq("状态码：字面量 → 未知(0)", 0, Http.ParseHttpCode("%{http_code}"));
            Eq("状态码：空串 → 0", 0, Http.ParseHttpCode(""));
            Eq("状态码：null → 0", 0, Http.ParseHttpCode(null));
            Eq("状态码：没有数字 → 0", 0, Http.ParseHttpCode("abc"));
            // 这些是"解析不出来"的情况，解析失败绝不能抛异常 ——
            // 状态码只是锦上添花的判据，正文才是结果
            Eq("状态码：000（curl 连接失败）→ 0", 0, Http.ParseHttpCode("000"));
            Eq("状态码：99 不合法 → 0", 0, Http.ParseHttpCode("99"));
            Eq("状态码：600 不合法 → 0", 0, Http.ParseHttpCode("600"));
        }

        // ============================================================
        //  14) 错字检测（双源比对）
        // ============================================================

        /// <summary>
        /// 错字检测的核心逻辑。
        ///
        /// 这类算法的断言必须**双向**：既"该找到的一个不漏"，也"不该报的一个不报"。
        /// 所以除了"注入 N 个错字 → 正好找到那 N 处"，还专门测了
        /// "完全一致 → 0 处"和"只有空白差异 → 0 处" ——
        /// 后者尤其重要：两个源对空行/全角空格的处理不一样，
        /// 不归一化的话每段空行都会被报成一处差异，报告直接没法看。
        /// </summary>
        private static void TestTypoFinder()
        {
            // --- 1) 完全一致：必须 0 处差异 ---
            var same = "他后来发现，这里的时间过得很快。\n他决定明天再去看看。";
            Eq("错字：完全一致 → 0 处", 0, TypoFinder.CompareChapter("第一章", same, same).Count);

            // --- 2) 只有空白/换行差异 → 必须 0 处 ---
            var a1 = "第一段。\n\n第二段。\n第三段。";
            var b1 = "第一段。\r\n \r\n第二段。\r\n　\r\n第三段。";   // 多空行 + 全角空格
            Eq("错字：只差空白 → 0 处", 0, TypoFinder.CompareChapter("第一章", a1, b1).Count);

            // --- 3) 注入单字差异：必须精确定位，且两边写法都拿到 ---
            var a2 = "他后来发现，这里的时间过得很快。";
            var b2 = "他后来发現，这里的时间过得很快。";     // 现 → 現
            var d2 = TypoFinder.CompareChapter("第二章", a2, b2);
            Eq("错字：单字差异 → 1 处", 1, d2.Count);
            if (d2.Count == 1)
            {
                Eq("错字：主源写法", "现", d2[0].Primary);
                Eq("错字：对照源写法", "現", d2[0].Other);
                Eq("错字：类型是用字不同", TypoFinder.DiffKind.Replace, d2[0].Kind);
                Check("错字：位置是主源里的下标", d2[0].Position > 0 && d2[0].Position < a2.Length);
                Check("错字：带上下文", d2[0].Context.IndexOf("现", StringComparison.Ordinal) >= 0);
            }

            // --- 4) 多处差异：数量要对得上 ---
            var a3 = "我们都说他是一个好人，从来没有见过这样的人。";
            var b3 = "我们都說他是一个好人，从来没有見过这样的人。";   // 说→說, 见→見
            Eq("错字：两处差异 → 2 处", 2, TypoFinder.CompareChapter("第三章", a3, b3).Count);

            // --- 5) 漏字 / 多字：类型要区分开 ---
            var a4 = "今天天气很好，我们出去走走吧。";
            var b4 = "今天天气很好，我们出去吧。";        // 对照源少了"走走"
            var d4 = TypoFinder.CompareChapter("第四章", a4, b4);
            Eq("错字：漏字 → 1 处", 1, d4.Count);
            if (d4.Count == 1)
            {
                Eq("错字：漏字类型", TypoFinder.DiffKind.ExtraInPrimary, d4[0].Kind);
                Eq("错字：漏掉的内容", "走走", d4[0].Primary);
                Eq("错字：对照源这边为空", "", d4[0].Other);
            }

            var a5 = "今天天气很好，我们出去吧。";
            var b5 = "今天天气很好，我们出去走走吧。";     // 对照源多了"走走"
            var d5 = TypoFinder.CompareChapter("第五章", a5, b5);
            Eq("错字：多字 → 1 处", 1, d5.Count);
            if (d5.Count == 1)
                Eq("错字：多字类型", TypoFinder.DiffKind.MissingInPrimary, d5[0].Kind);

            // --- 6) 长度差过大：标成"疑似站点差异"，而不是逐字报一堆 ---
            var a6 = "短。";
            var sb6 = new StringBuilder();
            for (int i = 0; i < 400; i++) sb6.Append("这一章在另一个源里长得多，多半是站点排版或广告差异。");
            var d6 = TypoFinder.CompareChapter("第六章", a6, sb6.ToString());
            Eq("错字：长度差过大 → 只报 1 条汇总", 1, d6.Count);
            if (d6.Count == 1)
                Contains("错字：汇总里说明是站点差异", d6[0].Context, "疑似站点差异");

            // --- 7) 空内容不能抛异常 ---
            Eq("错字：两边都空 → 0 处", 0, TypoFinder.CompareChapter("x", "", "").Count);
            Eq("错字：主源空 → 0 处", 0, TypoFinder.CompareChapter("x", null, "有内容").Count);
            Eq("错字：对照源空 → 0 处", 0, TypoFinder.CompareChapter("x", "有内容", null).Count);

            // --- 8) 差异爆量要封顶，不能把报告刷爆 ---
            var big1 = new StringBuilder();
            var big2 = new StringBuilder();
            for (int i = 0; i < 900; i++) { big1.Append('甲'); big2.Append('乙'); }
            var d8 = TypoFinder.CompareChapter("第七章", big1.ToString(), big2.ToString());
            Check("错字：差异数量有上限（≤ " + TypoFinder.MaxDiffsPerChapter + "）",
                d8.Count <= TypoFinder.MaxDiffsPerChapter);

            // --- 9) 报告与 CSV ---
            var r = new TypoFinder.Result { Title = "报告测试", OtherSource = "对照源" };
            r.ComparedChapters = 3;
            r.IdenticalChapters = 1;
            r.SkippedChapters = 0;
            r.Diffs.AddRange(d2);
            r.Diffs.AddRange(d4);
            var book = new BookInfo { Title = "报告测试", Author = "作者" };
            var report = TypoFinder.BuildReport(book, r, @"C:\x\y.txt", "对照源");
            Contains("错字报告：含书名", report, "报告测试");
            Contains("错字报告：含对照源", report, "对照源");
            Contains("错字报告：含汇总行", report, "比对 3 章");
            Contains("错字报告：如实说明两源都错时测不出来", report, "两个源都错同一个字时检测不出来");
            Contains("错字报告：分组标题", report, "最可能是错字");

            var csv = TypoFinder.BuildCsv(r);
            Contains("错字 CSV：有表头", csv, "章节,位置,类型,主源,对照源,上下文");
            Contains("错字 CSV：有用字不同类型", csv, "用字不同");

            // CSV 转义：含逗号/引号的字段必须转义，否则 Excel 会串列
            var rCsv = new TypoFinder.Result { Title = "t" };
            rCsv.Diffs.Add(new TypoFinder.Diff { Chapter = "a,b", Primary = "c\"d", Other = "e" });
            var csv2 = TypoFinder.BuildCsv(rCsv);
            Contains("错字 CSV：含逗号的字段被引号包住", csv2, "\"a,b\"");
            Contains("错字 CSV：引号被转义成两个", csv2, "\"\"");
        }

        // ============================================================
        //  15) 网络错误归因 + 磁盘空间检查
        // ============================================================

        /// <summary>
        /// 把网络错误翻译成人话。
        ///
        /// 为什么这个值得测：用户看到 `curl 退出码 35` 是完全无助的 ——
        /// 他不知道是网络坏了、站点挂了、还是要挂代理，更不知道**换个站点就能用**。
        /// 归因错了比不归因更糟（会把人带偏），所以这里逐条钉住 curl 退出码的映射。
        /// </summary>
        private static void TestNetDiag()
        {
            string advice;

            // curl 退出码 → 归因（这些码的语义来自 curl 文档）
            Eq("归因：6 = 域名解析失败", "域名解析失败", NetDiag.Classify(6, "", out advice));
            Check("归因：6 给出可操作建议", advice.Length > 0);
            Eq("归因：7 = 连不上", "连不上服务器（端口被拒或网络不通）", NetDiag.Classify(7, "", out advice));
            Eq("归因：28 = 超时", "请求超时", NetDiag.Classify(28, "", out advice));
            Eq("归因：35 = HTTPS 握手被拒", "HTTPS 握手被拒绝", NetDiag.Classify(35, "", out advice));
            // 35 是最容易让用户误以为是"自己网络坏了"的那一个，建议里必须说清是站点侧
            NetDiag.Classify(35, "", out advice);
            Contains("归因：35 明确指出不是用户网络的问题", advice, "不是你的网络问题");
            Contains("归因：35 建议换站点", advice, "换个站点");
            Eq("归因：60 = 证书校验失败", "证书校验失败", NetDiag.Classify(60, "", out advice));
            Eq("归因：56 = 连接被重置", "接收数据失败（连接被重置）", NetDiag.Classify(56, "", out advice));
            Eq("归因：3 = URL 格式错误（指向程序自身问题）",
                "URL 格式错误", NetDiag.Classify(3, "", out advice));
            Contains("归因：3 提示贴日志到 issue", advice, "issue");

            // 退出码没命中时看文本特征
            Eq("归因：文本含 schannel → TLS",
                "HTTPS 握手失败", NetDiag.Classify(0, "schannel: failed to receive handshake", out advice));
            Eq("归因：文本含无法解析 → DNS",
                "域名解析失败", NetDiag.Classify(0, "无法解析主机名", out advice));
            Eq("归因：文本含限流 → 限流",
                "被站点限流", NetDiag.Classify(0, "访问太频繁", out advice));

            // 归不出来必须返回空（让调用方原样显示），**绝不能瞎猜**
            Eq("归因：认不出来 → 空串", "", NetDiag.Classify(0, "某个没见过的错误", out advice));
            Eq("归因：空错误 → 空串", "", NetDiag.Classify(0, "", out advice));
            Eq("归因：退出码 0 且无文本 → 空串", "", NetDiag.Classify(0, null, out advice));

            // 从异常文本里挖 curl 退出码
            Eq("退出码提取：从 Http 的异常文本里挖出来", 35,
                NetDiag.ExtractCurlExit("请求失败（已重试 3 次）：https://x\n  curl 退出码 35"));
            Eq("退出码提取：没有则 0", 0, NetDiag.ExtractCurlExit("就是失败了"));
            Eq("退出码提取：null → 0", 0, NetDiag.ExtractCurlExit(null));
            Eq("退出码提取：两位数也对", 28, NetDiag.ExtractCurlExit("curl 退出码 28："));

            // 磁盘空间估算
            var e1 = DiskCheck.EstimateBytes(1000, false);
            Check("磁盘估算：1000 章 > 2MB（" + e1 + "）", e1 > 2 * 1024 * 1024);
            Check("磁盘估算：0 章 = 0", DiskCheck.EstimateBytes(0, false) == 0);
            Check("磁盘估算：负数按 0 处理", DiskCheck.EstimateBytes(-5, false) == 0);
            Check("磁盘估算：含 EPUB 时更大",
                DiskCheck.EstimateBytes(1000, true) > DiskCheck.EstimateBytes(1000, false));

            // 真实盘符检查：需求极小 → 必须 Ok（不能因为查空间就不让下载）
            var ok = DiskCheck.Check(AppDomain.CurrentDomain.BaseDirectory, 1024);
            Check("磁盘检查：需求极小时判定为够", ok.Ok);
            Check("磁盘检查：能报出可用空间（" + ok.FreeBytes + "）", ok.FreeBytes > 0);

            // 需求离谱地大 → 必须报不够，并且给出人话提示
            var bad = DiskCheck.Check(AppDomain.CurrentDomain.BaseDirectory, long.MaxValue / 4);
            Check("磁盘检查：需求过大时判定为不够", !bad.Ok);
            Check("磁盘检查：不够时给出提示", bad.Message.Length > 0);
            Contains("磁盘检查：提示里说明后果", bad.Message, "失败");

            // 查不出来时**绝不能拦着用户下载**
            var weird = DiskCheck.Check("Z:\\根本不存在的盘\\子目录", 1024);
            Check("磁盘检查：路径不可用时放行（不阻塞下载）", weird.Ok);

            Eq("磁盘大小人话：小数值", "512 字节", DiskCheck.Human(512));
            Eq("磁盘大小人话：KB", "1.5 KB", DiskCheck.Human(1536));
            Eq("磁盘大小人话：MB", "2 MB", DiskCheck.Human(2 * 1024 * 1024));
            Eq("磁盘大小人话：GB", "1.5 GB", DiskCheck.Human((long)(1.5 * 1024 * 1024 * 1024)));
        }

        // ============================================================
        //  16) 任务队列的持久化
        // ============================================================

        /// <summary>
        /// 队列落盘/读回。
        ///
        /// 为什么要测：队列是"睡前排 5 本"的用法，误点关闭或程序崩一次，
        /// 排好的队不该全白费。而这类"存了但要能读回来"的功能，
        /// 最容易出的问题就是**存的时候格式和读的时候对不上**（目录缓存就栽过一次字段漏存）。
        /// 所以这里必须做**往返**断言，不能只测"文件写出来了"。
        /// </summary>
        private static void TestQueuePersistence(string work)
        {
            var path = Path.Combine(work, "队列.json");

            // 往返：普通书名
            var items = new List<string> { "牧神记", "全职高手", "沧元图" };
            Check("队列：保存成功", Bookshelf.SaveQueue(path, items));
            var back = Bookshelf.LoadQueue(path);
            Eq("队列：往返条数一致", 3, back.Count);
            Eq("队列：第一项", "牧神记", back.Count > 0 ? back[0] : "");
            Eq("队列：最后一项", "沧元图", back.Count > 2 ? back[2] : "");

            // 往返：带引号、反斜杠、中文冒号的难搞内容
            var tricky = new List<string>
            {
                "番茄：书名里有\"引号\"",
                @"biquga：路径\带反斜杠",
                "https://www.biquga.com/10_10333/",
                "带 emoji 的书名 🐟",
            };
            Check("队列：难搞内容保存成功", Bookshelf.SaveQueue(path, tricky));
            var back2 = Bookshelf.LoadQueue(path);
            Eq("队列：难搞内容条数一致", 4, back2.Count);
            for (int i = 0; i < tricky.Count && i < back2.Count; i++)
                Eq("队列：难搞内容往返（第 " + (i + 1) + " 项）", tricky[i], back2[i]);

            // 空列表 → 删文件而不是留个空文件
            Check("队列：存空列表成功", Bookshelf.SaveQueue(path, new List<string>()));
            Check("队列：空列表会删掉文件", !File.Exists(path));
            Eq("队列：文件不存在时读回空列表", 0, Bookshelf.LoadQueue(path).Count);

            // null 也不能抛
            Check("队列：传 null 不抛异常", Bookshelf.SaveQueue(path, null));
            Eq("队列：读不存在的文件 → 空列表", 0, Bookshelf.LoadQueue(Path.Combine(work, "没有这个.json")).Count);

            // 损坏文件 → 空列表，不抛（配置类文件坏了不能让程序起不来）
            File.WriteAllText(path, "{ 这不是合法 json [[[ ", new UTF8Encoding(false));
            Eq("队列：损坏文件降级为空列表", 0, Bookshelf.LoadQueue(path).Count);

            // 旧格式/多余字段也要能读（手改过的文件）
            File.WriteAllText(path,
                "{\n \"note\": \"手写的\",\n \"未知字段\": 123,\n \"items\": [\n  \"书一\",\n  \"书二\"\n ]\n}\n",
                new UTF8Encoding(false));
            var back3 = Bookshelf.LoadQueue(path);
            Eq("队列：手改过的文件能读", 2, back3.Count);
            Eq("队列：手改文件内容正确", "书一", back3.Count > 0 ? back3[0] : "");

            // 空白项要被丢掉（用户按了空行不该变成一本书）
            File.WriteAllText(path, "{\"items\":[\"书一\",\"\",\"  \",\"书二\"]}", new UTF8Encoding(false));
            Eq("队列：空白项被丢弃", 2, Bookshelf.LoadQueue(path).Count);

            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ============================================================
        //  17) 目录遍历断点续爬
        // ============================================================

        /// <summary>
        /// 遍历断点的存取。
        ///
        /// 为什么必须测"换书要作废"这一条：断点里存的是"下次从哪一页接着走"。
        /// 如果校验不严，A 书的断点被 B 书用上，**能把 A 的章节拼进 B 的目录里** ——
        /// 这比"重走一遍"糟糕得多（重走只是慢，拼错是数据损坏）。
        /// 所以 StartKey 校验是关键路径，必须有断言守着。
        /// </summary>
        private static void TestCrawlResume()
        {
            // 用真实的 cache 目录（CrawlResume 固定读 exe 同目录\cache），
            // 造一份断点、验完删掉，不留垃圾（和 TestDirCacheRoundTrip 同样的约定）。
            const string site = "biquga";
            const string dirKey = "/0_0-resumetest";
            const string startKey = "99999999";
            var path = CrawlResume.PathFor(site, dirKey);

            try
            {
                CrawlResume.Clear(site, dirKey);
                Check("续爬：清空后不存在断点", !CrawlResume.Exists(site, dirKey));

                var s = new CrawlResume.State
                {
                    Site = site,
                    BookDir = dirKey,
                    StartKey = startKey,
                    NextKey = "88888888_2",
                    Pages = 350,
                };
                s.Ids.Add("88888889");
                s.Titles.Add("第三章 带\"引号\"的标题");
                s.Ids.Add("88888888");
                s.Titles.Add("第二章 with English");
                s.Ids.Add("77777777");
                s.Titles.Add("");

                Check("续爬：保存成功", CrawlResume.Save(s));
                Check("续爬：文件已写出", File.Exists(path));
                Check("续爬：Exists 返回 true", CrawlResume.Exists(site, dirKey));

                var back = CrawlResume.Load(site, dirKey, startKey);
                Check("续爬：能读回来", back != null);
                if (back != null)
                {
                    Eq("续爬：NextKey 往返正确", "88888888_2", back.NextKey);
                    Eq("续爬：StartKey 往返正确", startKey, back.StartKey);
                    Eq("续爬：Pages 往返正确", 350, back.Pages);
                    Eq("续爬：章节数一致", 3, back.Ids.Count);
                    Eq("续爬：第一个 id", "88888889", back.Ids.Count > 0 ? back.Ids[0] : "");
                    // 标题里带引号是最容易在手工拼 JSON 时出错的
                    Eq("续爬：带引号的标题往返正确", "第三章 带\"引号\"的标题",
                        back.Titles.Count > 0 ? back.Titles[0] : "");
                    Eq("续爬：id 与标题一一对应", back.Ids.Count, back.Titles.Count);
                }

                // ★ 关键：起始页 key 对不上 → 必须作废（否则会把别的书的章节拼进来）
                Check("续爬：换书后断点作废（StartKey 不匹配）",
                    CrawlResume.Load(site, dirKey, "12345678") == null);
                Check("续爬：不给 StartKey 时仍可读",
                    CrawlResume.Load(site, dirKey, null) != null);

                // 换目录 → 读不到（路径都不同）
                Check("续爬：换书目录读不到", CrawlResume.Load(site, "/9_9-other", startKey) == null);

                // 缺 NextKey 的断点没有意义（不知道从哪接着走）→ 不保存
                var bad = new CrawlResume.State { Site = site, BookDir = dirKey, StartKey = startKey, NextKey = "" };
                bad.Ids.Add("1");
                Check("续爬：没有 NextKey 的断点不保存", !CrawlResume.Save(bad));

                // 损坏文件 → null，不抛
                File.WriteAllText(path, "{ 这不是 json ", new UTF8Encoding(false));
                Check("续爬：损坏文件返回 null 不抛异常", CrawlResume.Load(site, dirKey, startKey) == null);

                CrawlResume.Clear(site, dirKey);
                Check("续爬：Clear 能删掉断点", !CrawlResume.Exists(site, dirKey));
            }
            finally
            {
                try { CrawlResume.Clear(site, dirKey); } catch { }
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }

            // 默认不能改变原有行为
            var pc = new BiqugaSite();
            Check("续爬：BiqugaSite.Resume 默认关闭（不改变原有行为）", !pc.Resume);

            TestResumeBoundary();
        }

        /// <summary>
        /// 续爬的**边界正确性**：中断点前后会不会漏章 / 重复章。
        ///
        /// 为什么单独测这个：断点存的是"下一页要抓谁"。语义错一位的话，
        /// 断点那一页要么被跳过（漏章）、要么被收录两次 ——
        /// 而这两种都**不会报错**，只会让目录悄悄少一章或多一章。
        ///
        /// 用一条可控的模拟链（100→99→…→1）走两遍：一次走完当基准；
        /// 一次走到第 60 页落断点后"中断"、再从断点续完，最后比对两边必须完全一致。
        /// </summary>
        private static void TestResumeBoundary()
        {
            const string site = "biquga";
            const string dirKey = "/0_0-boundarytest";
            const string startKey = "100";

            try
            {
                CrawlResume.Clear(site, dirKey);

                // 基准：完整走完（stopAfter=0 表示不中断）
                var full = WalkFakeChain(startKey, 0, site, dirKey);
                Eq("续爬边界：基准链共 100 章", 100, full.Count);

                // 走到第 40 页落断点并中断
                CrawlResume.Clear(site, dirKey);
                var part = WalkFakeChain(startKey, 40, site, dirKey);
                Eq("续爬边界：中断处已收录 40 章", 40, part.Count);

                var st = CrawlResume.Load(site, dirKey, startKey);
                Check("续爬边界：断点可读回", st != null);
                if (st == null) return;
                Eq("续爬边界：断点里记的 NextKey 是下一页", "60", st.NextKey);

                // 从断点续完，按 CrawlChapters 的语义合并（同 id 不重复收录）
                var merged = new List<string>(st.Ids);
                var seen = new HashSet<string>(st.Ids);
                foreach (var cid in WalkFakeChain(st.NextKey, 0, site, dirKey))
                    if (seen.Add(cid)) merged.Add(cid);

                Eq("续爬边界：★ 续爬后章数与一次走完一致", full.Count, merged.Count);

                var setA = new HashSet<string>(full);
                var setC = new HashSet<string>(merged);
                Check("续爬边界：★ 没有缺章", setC.IsSupersetOf(setA));
                Eq("续爬边界：★ 没有重复章", setA.Count, setC.Count);

                // 边界页本身必须只出现一次
                int inA = 0, inC = 0;
                foreach (var x in full) if (x == st.NextKey) inA++;
                foreach (var x in merged) if (x == st.NextKey) inC++;
                Eq("续爬边界：★ 边界页在基准里出现 1 次", 1, inA);
                Eq("续爬边界：★ 边界页在续爬结果里也只出现 1 次", 1, inC);
            }
            finally
            {
                try { CrawlResume.Clear(site, dirKey); } catch { }
            }
        }

        /// <summary>
        /// 复刻 CrawlChapters 的遍历语义：每页收一个章节 id，prev = id-1，走到 0 结束；
        /// 走完 stopAfter 页就落一次断点并"中断"（下次从断点的 NextKey 接着走）。
        /// </summary>
        private static List<string> WalkFakeChain(string startKey, int stopAfter, string site, string dirKey)
        {
            var byCid = new Dictionary<string, string>();
            var order = new List<string>();
            string key = startKey;
            int pages = 0;

            while (!string.IsNullOrEmpty(key) && pages < 20000)
            {
                int n;
                if (!int.TryParse(key, out n) || n <= 0) break;   // 回到目录页
                pages++;
                if (!byCid.ContainsKey(key)) { byCid[key] = "第" + key + "章"; order.Add(key); }

                key = (n - 1).ToString();

                if (stopAfter > 0 && pages == stopAfter)
                {
                    var s = new CrawlResume.State
                    {
                        Site = site, BookDir = dirKey, StartKey = startKey,
                        NextKey = key, Pages = pages,
                    };
                    foreach (var cid in order) { s.Ids.Add(cid); s.Titles.Add(byCid[cid]); }
                    CrawlResume.Save(s);
                    break;      // 模拟中断
                }
            }
            return order;
        }

        // ============================================================
        //  18) AI 裁决错字（纯函数部分，不联网）
        // ============================================================

        /// <summary>
        /// AI 裁决错字的**可离线验证**部分：可疑度筛选、成本估算、
        /// 提示词构造、请求体/URL 构造、以及**响应解析**。
        ///
        /// 为什么响应解析要测得这么细：小模型（尤其本地 7B）经常不照格式回答 ——
        /// 多写解释、用全角竖线、加 markdown 列表符号、少给字段。
        /// 解析器容错不够的话，用户花时间跑完一轮 AI，得到的却是"全部未裁决"。
        /// 所以下面把各种脏输出都喂一遍。
        /// </summary>
        private static void TestAiAdjudicator()
        {
            // ---------- 可疑度筛选 ----------
            var typo = new TypoFinder.Diff
            {
                Kind = TypoFinder.DiffKind.Replace,
                Primary = "现", Other = "現", Context = "…他后来发【现】，这里…",
            };
            Check("AI 筛选：单字差异算高可疑", AiAdjudicator.IsHighSuspicion(typo));

            var adDiff = new TypoFinder.Diff
            {
                Kind = TypoFinder.DiffKind.Replace,
                Primary = "x", Other = "y",
                Context = "两源字数相差过大（100 vs 900），疑似站点差异而非错字，已跳过逐字比对",
            };
            Check("AI 筛选：站点差异不算高可疑（别浪费钱）", !AiAdjudicator.IsHighSuspicion(adDiff));

            var longDiff = new TypoFinder.Diff
            {
                Kind = TypoFinder.DiffKind.Replace,
                Primary = "一段很长的排版差异内容", Other = "短", Context = "ctx",
            };
            Check("AI 筛选：长度差过大不算高可疑", !AiAdjudicator.IsHighSuspicion(longDiff));

            var extraDiff = new TypoFinder.Diff
            {
                Kind = TypoFinder.DiffKind.ExtraInPrimary, Primary = "走走", Other = "", Context = "ctx",
            };
            Check("AI 筛选：多字/漏字不送 AI（AI 判该不该补词不稳）",
                !AiAdjudicator.IsHighSuspicion(extraDiff));

            var punctDiff = new TypoFinder.Diff
            {
                Kind = TypoFinder.DiffKind.Replace, Primary = ",", Other = "。", Context = "ctx",
            };
            Check("AI 筛选：纯标点差异不送", !AiAdjudicator.IsHighSuspicion(punctDiff));

            Check("AI 筛选：null 安全", !AiAdjudicator.IsHighSuspicion(null));

            var all = new List<TypoFinder.Diff> { typo, adDiff, extraDiff, punctDiff };
            var cand = AiAdjudicator.PickCandidates(all);
            Eq("AI 筛选：4 条里只挑出 1 条高可疑", 1, cand.Count);
            Eq("AI 筛选：挑中的是那条单字差异", 0, cand.Count > 0 ? cand[0] : -1);

            int chars, tokens;
            AiAdjudicator.Estimate(all, cand, out chars, out tokens);
            Check("AI 估算：给出正的数字符数（" + chars + "）", chars > 0);
            Eq("AI 估算：token 估算等于字符数（中文约 1 字 1 token）", chars, tokens);
            AiAdjudicator.Estimate(null, null, out chars, out tokens);
            Eq("AI 估算：null 安全", 0, chars);

            // ---------- 配置校验 ----------
            var local = new AiAdjudicator.Config
            {
                Backend = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "qwen2.5:7b",
            };
            Check("AI 配置：本地 Ollama 不需要 key", local.Validate() == null);
            Check("AI 配置：本地不算云端", !local.IsCloud);

            var cloudNoKey = new AiAdjudicator.Config
            {
                Backend = "openai", BaseUrl = "https://api.deepseek.com", Model = "deepseek-chat",
            };
            Check("AI 配置：云端缺 key 要报错", cloudNoKey.Validate() != null);
            Contains("AI 配置：报错里告诉用户怎么改",
                cloudNoKey.Validate() ?? "", "本地 Ollama");

            cloudNoKey.ApiKey = "sk-test";
            Check("AI 配置：补上 key 后通过", cloudNoKey.Validate() == null);

            var noModel = new AiAdjudicator.Config { Backend = "ollama", BaseUrl = "http://127.0.0.1:11434" };
            Check("AI 配置：缺模型名要报错", noModel.Validate() != null);

            var wrongBackend = new AiAdjudicator.Config
            {
                Backend = "ollama", BaseUrl = "https://api.deepseek.com", Model = "x",
            };
            Check("AI 配置：选了本地但地址是外网，要提醒", wrongBackend.Validate() != null);

            // ---------- URL 构造 ----------
            // ★ URL 的契约变了：不再"算一个 URL"，而是"给一串候选，按顺序试"。
            //   原因：各家的 base_url 形状太多，我每见一家就改一次拼装、每次都被真实用户撞出来
            //   （DeepSeek 要补 /v1；智谱是 /api/paas/v4；商汤是 /compatible-mode/v2）。
            //   现在**先照用户填的原样试**，只有 404 才逐级补路径。
            //   所以断言也换了：这些用例真正要保证的是
            //     (a) 首选必须是用户原样 —— 从官方文档抄来的完整端点不能被我们改坏
            //     (b) 候选里**必须包含**真正能用的那个 URL
            var ollamaCands = AiAdjudicator.BuildUrlCandidates(local);
            Check("AI URL：Ollama 候选非空", ollamaCands.Count > 0);
            Eq("AI URL：Ollama 首选是补好的 /api/chat",
                "http://127.0.0.1:11434/api/chat", ollamaCands[0]);

            var cloud = new AiAdjudicator.Config
            {
                Backend = "openai", BaseUrl = "https://api.deepseek.com", Model = "deepseek-chat", ApiKey = "k",
            };
            var c1 = AiAdjudicator.BuildUrlCandidates(cloud);
            Eq("AI URL：首选是用户原样（不被我们改动）", "https://api.deepseek.com", c1[0]);
            Check("AI URL：候选里包含 /v1/chat/completions" + "（" + Join(c1) + "）", c1.Contains("https://api.deepseek.com/v1/chat/completions"));

            cloud.BaseUrl = "https://api.deepseek.com/v1";
            var c2 = AiAdjudicator.BuildUrlCandidates(cloud);
            Check("AI URL：带 /v1 时候选里包含正确端点" + "（" + Join(c2) + "）", c2.Contains("https://api.deepseek.com/v1/chat/completions"));
            Check("AI URL：带 /v1 时不会拼出 /v1/v1" + "（" + Join(c2) + "）", !c2.Contains("https://api.deepseek.com/v1/v1/chat/completions"));

            // 智谱 /api/paas/v4 —— 真实踩过的坑：拼成 .../v4/v1/chat/completions 必然 404
            cloud.BaseUrl = "https://open.bigmodel.cn/api/paas/v4";
            var c3 = AiAdjudicator.BuildUrlCandidates(cloud);
            Eq("AI URL：智谱首选是用户原样", "https://open.bigmodel.cn/api/paas/v4", c3[0]);
            Check("AI URL：智谱候选里包含正确端点" + "（" + Join(c3) + "）", c3.Contains("https://open.bigmodel.cn/api/paas/v4/chat/completions"));
            Check("AI URL：智谱不会拼出 /v4/v1/" + "（" + Join(c3) + "）", !c3.Contains("https://open.bigmodel.cn/api/paas/v4/v1/chat/completions"));

            // 商汤 /compatible-mode/v2 —— 同一个坑的第二种形态
            cloud.BaseUrl = "https://api.sensenova.cn/compatible-mode/v2";
            var c4 = AiAdjudicator.BuildUrlCandidates(cloud);
            Check("AI URL：商汤候选里包含正确端点" + "（" + Join(c4) + "）", c4.Contains("https://api.sensenova.cn/compatible-mode/v2/chat/completions"));
            Check("AI URL：商汤不会拼出 /v2/v1/" + "（" + Join(c4) + "）", !c4.Contains("https://api.sensenova.cn/compatible-mode/v2/v1/chat/completions"));

            // 用户直接填完整端点：只有 1 个候选，且原样可用
            cloud.BaseUrl = "https://x/v1/chat/completions";
            var c5 = AiAdjudicator.BuildUrlCandidates(cloud);
            Eq("AI URL：完整端点只有一个候选", 1, c5.Count);
            Eq("AI URL：完整端点原样用", "https://x/v1/chat/completions", c5[0]);

            // 空地址不能崩
            Eq("AI URL：空地址候选为空", 0, AiAdjudicator.BuildUrlCandidates(
                new AiAdjudicator.Config { Backend = "openai", BaseUrl = "" }).Count);
            Eq("AI URL：null 地址候选为空", 0, AiAdjudicator.BuildUrlCandidates(
                new AiAdjudicator.Config { Backend = "openai", BaseUrl = null }).Count);

            // ---------- 请求体 ----------
            var bodyLocal = AiAdjudicator.BuildRequestBody(local, "test");
            Contains("AI 请求体：本地带 stream=false（要好一次性返回）", bodyLocal, "\"stream\":false");
            Contains("AI 请求体：带上模型名", bodyLocal, "qwen2.5:7b");
            Contains("AI 请求体：temperature 为 0（裁决要稳定，不要发挥）", bodyLocal, "\"temperature\":0");

            var bodyCloud = AiAdjudicator.BuildRequestBody(cloud, "test");
            Contains("AI 请求体：云端是 messages 结构", bodyCloud, "\"messages\"");
            Check("AI 请求体：云端不带 stream 字段（默认非流式）",
                bodyCloud.IndexOf("\"stream\"", StringComparison.Ordinal) < 0);

            var nasty = "他说\"你好\"\n换行\\反斜杠\ttab";
            var nb = AiAdjudicator.BuildRequestBody(local, nasty);
            Contains("AI 请求体：引号被转义", nb, "\\\"你好\\\"");
            Contains("AI 请求体：换行被转义", nb, "\\n");
            Contains("AI 请求体：反斜杠被转义", nb, "\\\\");
            Check("AI 请求体：不含裸换行", nb.IndexOf('\n') < 0);

            var h = AiAdjudicator.BuildHeaders(cloud);
            Check("AI 头：云端带 Authorization", h.ContainsKey("Authorization"));
            Contains("AI 头：Bearer 前缀", h["Authorization"], "Bearer ");
            var h2 = AiAdjudicator.BuildHeaders(local);
            Check("AI 头：本地不带 Authorization（Ollama 不需要）", !h2.ContainsKey("Authorization"));
            Check("AI 头：都声明 JSON", h["Content-Type"] == "application/json");

            // ---------- 提示词 ----------
            var batch = new List<int> { 0 };
            var msg = AiAdjudicator.BuildUserMessage(all, batch);
            Contains("AI 提示词：给出上下文", msg, "他后来发");
            Contains("AI 提示词：标出差异处", msg, "【现】");
            Contains("AI 提示词：写法A", msg, "写法A：现");
            Contains("AI 提示词：写法B", msg, "写法B：現");
            // 只发片段，不发整章 —— 这是成本与隐私的关键
            Check("AI 提示词：长度很短（只发差异点上下文，" + msg.Length + " 字符）", msg.Length < 400);
            Contains("AI 系统提示词：明确禁止改写上下文",
                AiAdjudicator.SystemPrompt, "绝对不要改写");
            Contains("AI 系统提示词：拿不准要选 U", AiAdjudicator.SystemPrompt, "拿不准就选 U");

            // ---------- 响应解析（重点：各种脏输出）----------
            const int n = 4;
            var clean = "1|A|用字正确|\n2|B|主源错字|\n3|C|两边都不对|正确写法\n4|U|无法判断|";
            var r = AiAdjudicator.ParseReply(clean, n);
            Eq("AI 解析：条数", n, r.Count);
            Eq("AI 解析：A → 主源对", AiAdjudicator.Verdict.PrimaryRight, r[0].Verdict);
            Eq("AI 解析：B → 对照源对", AiAdjudicator.Verdict.OtherRight, r[1].Verdict);
            Eq("AI 解析：C → 两边都不对", AiAdjudicator.Verdict.BothWrong, r[2].Verdict);
            Eq("AI 解析：C 带建议写法", "正确写法", r[2].Suggestion);
            Eq("AI 解析：U → 判断不了", AiAdjudicator.Verdict.Unsure, r[3].Verdict);
            Eq("AI 解析：理由被读出来", "主源错字", r[1].Reason);

            Eq("AI 解析：容忍全角竖线", AiAdjudicator.Verdict.PrimaryRight,
                AiAdjudicator.ParseReply("1｜A｜理由", 1)[0].Verdict);

            var markdown = "- 1|A|带列表符号\n```\n2|B|x\n```";
            var r2 = AiAdjudicator.ParseReply(markdown, 2);
            Eq("AI 解析：容忍列表符号", AiAdjudicator.Verdict.PrimaryRight, r2[0].Verdict);
            Eq("AI 解析：跳过代码块围栏", AiAdjudicator.Verdict.OtherRight, r2[1].Verdict);

            Eq("AI 解析：结论带括号说明也认", AiAdjudicator.Verdict.PrimaryRight,
                AiAdjudicator.ParseReply("1|A（写法A正确）|x", 1)[0].Verdict);
            Eq("AI 解析：小写也认", AiAdjudicator.Verdict.OtherRight,
                AiAdjudicator.ParseReply("1|b|理由", 1)[0].Verdict);

            var messy = "好的，我来分析：\n\n1|A|正确\n\n以上就是我的判断。";
            var r3 = AiAdjudicator.ParseReply(messy, 3);
            Eq("AI 解析：缺失的条目填未裁决", AiAdjudicator.Verdict.None, r3[1].Verdict);
            Eq("AI 解析：能认出夹在废话里的那条", AiAdjudicator.Verdict.PrimaryRight, r3[0].Verdict);
            Eq("AI 解析：废话行不被误判", AiAdjudicator.Verdict.None, r3[2].Verdict);

            Eq("AI 解析：越界序号被忽略", AiAdjudicator.Verdict.None,
                AiAdjudicator.ParseReply("9|A|x", 2)[1].Verdict);
            Eq("AI 解析：序号非数字被忽略", AiAdjudicator.Verdict.None,
                AiAdjudicator.ParseReply("abc|A|x", 1)[0].Verdict);
            Eq("AI 解析：空回复返回全未裁决", AiAdjudicator.Verdict.None,
                AiAdjudicator.ParseReply("", 2)[0].Verdict);
            Eq("AI 解析：null 回复返回全未裁决", AiAdjudicator.Verdict.None,
                AiAdjudicator.ParseReply(null, 2)[0].Verdict);
            Eq("AI 解析：expectedCount=0 安全", 0, AiAdjudicator.ParseReply("x", 0).Count);

            var longSug = "1|C|理由|这是一整句很长的话不该被当成单字建议";
            Eq("AI 解析：过长的建议写法被丢弃", "",
                AiAdjudicator.ParseReply(longSug, 1)[0].Suggestion);
            Eq("AI 解析：丢弃建议但保留结论", AiAdjudicator.Verdict.BothWrong,
                AiAdjudicator.ParseReply(longSug, 1)[0].Verdict);

            Contains("AI 结论文本：主源对", r[0].VerdictText(), "主源正确");
            Contains("AI 结论文本：两边都不对时带建议", r[2].VerdictText(), "正确写法");

            // ---------- 响应体提取（两种后端字段名不同）----------
            var openaiJson = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"1|A|x\"}}]}";
            Eq("AI 响应：OpenAI 格式能取到 content", "1|A|x",
                AiAdjudicator.ExtractContent(openaiJson, cloud));

            var ollamaJson = "{\"model\":\"qwen\",\"message\":{\"role\":\"assistant\",\"content\":\"1|B|y\"},\"done\":true}";
            Eq("AI 响应：Ollama 格式能取到 content", "1|B|y",
                AiAdjudicator.ExtractContent(ollamaJson, local));

            var tricky = "{\"message\":{\"content\":\"1|A|含{}括号和\\\"引号\\\"\"}}";
            Eq("AI 响应：content 里的花括号/引号不影响解析", "1|A|含{}括号和\"引号\"",
                AiAdjudicator.ExtractContent(tricky, local));

            var errJson = "{\"error\":{\"message\":\"model 'x' not found\",\"type\":\"invalid_request\"}}";
            Contains("AI 响应：能挖出错误信息", AiAdjudicator.ExtractError(errJson), "not found");
            Eq("AI 响应：错误响应取不到 content", null,
                AiAdjudicator.ExtractContent(errJson, local));
            Eq("AI 响应：空输入安全", null, AiAdjudicator.ExtractContent("", local));
            Eq("AI 响应：null 安全", null, AiAdjudicator.ExtractContent(null, local));

            // ---------- 云端预设 ----------
            var presets = AiAdjudicator.CloudPresets();
            Check("AI 预设：至少 3 个（" + presets.Count + "）", presets.Count >= 3);
            // 智谱排第一：它是"想用 AI 又不想花钱"的正当路径
            Eq("AI 预设：第一个是智谱（有免费额度）", "智谱", presets[0].Name.Substring(0, 2));
            Contains("AI 预设：智谱说明里点出免费", presets[0].Note, "免费");
            Check("AI 预设：每个预设都有说明", AllHave(presets, true));
            Check("AI 预设：有 DeepSeek", HasPreset(presets, "DeepSeek"));
            Check("AI 预设：有硅基流动", HasPreset(presets, "硅基"));
            // 最后一个必须是"自定义"，否则用户没法接别家
            Check("AI 预设：最后一个是自定义",
                presets[presets.Count - 1].Name.StartsWith("自定义", StringComparison.Ordinal));
            // 除自定义外都要填好 baseurl 与模型名 —— 预设的意义就是"不用用户去翻文档"
            foreach (var p in presets)
            {
                if (p.Name.StartsWith("自定义", StringComparison.Ordinal)) continue;
                Check("AI 预设：[" + p.Name + "] 填了地址", p.BaseUrl.StartsWith("http", StringComparison.Ordinal));
                Check("AI 预设：[" + p.Name + "] 填了模型名", p.Model.Length > 0);
                Check("AI 预设：[" + p.Name + "] 给了控制台地址", p.ConsoleUrl.StartsWith("http", StringComparison.Ordinal));
                // 预设地址必须是 https（密钥不能被明文传输）
                Check("AI 预设：[" + p.Name + "] 用 https",
                    p.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            }

            // 预设的地址要能真的连上：候选里必须包含"补好最后一段"的那个 URL。
            // 这条比"预设字段非空"有用得多 —— 它验的是**用户点一下真的能用**。
            foreach (var p in presets)
            {
                if (p.BaseUrl.Length == 0) continue;
                var c = new AiAdjudicator.Config { Backend = "openai", BaseUrl = p.BaseUrl, Model = p.Model, ApiKey = "k" };
                var cands = AiAdjudicator.BuildUrlCandidates(c);
                bool hasGood = false;
                foreach (var u in cands)
                    if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) &&
                        u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) hasGood = true;
                Check("AI 预设：[" + p.Name + "] 候选里有能用的 https 端点（" + Join(cands) + "）", hasGood);
                // 不能拼出 /v4/v1/ 或 /v2/v1/ 这种（智谱、商汤的真实坑）
                foreach (var u in cands)
                    Check("AI 预设：[" + p.Name + "] 不出现 '/v<数字>/v1/' 这种拼错（" + u + "）",
                        System.Text.RegularExpressions.Regex.IsMatch(u, @"/v\d+/v1/") == false);
            }

            // ---------- 本机模型探测：连不上时必须安全返回空 ----------
            var none = AiAdjudicator.ListLocalModels("http://127.0.0.1:1", 2);
            Eq("AI 本机探测：连不上返回空列表（Ollama 没装是常态）", 0, none.Count);
            Eq("AI 本机探测：空地址也返回空列表不抛", 0, AiAdjudicator.ListLocalModels("", 2).Count);
            Eq("AI 本机探测：null 地址也安全", 0, AiAdjudicator.ListLocalModels(null, 2).Count);
        }

        private static bool AllHave(List<AiAdjudicator.CloudPreset> list, bool _)
        {
            foreach (var p in list) if (string.IsNullOrEmpty(p.Note)) return false;
            return true;
        }

        private static string Join(List<string> list)
        {
            var sb = new StringBuilder();
            foreach (var s in list)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(s);
            }
            return sb.ToString();
        }

        private static bool HasPreset(List<AiAdjudicator.CloudPreset> list, string kw)
        {
            foreach (var p in list) if (p.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ============================================================
        //  19) 配置项往返（专抓"解析了但没接上"）
        // ============================================================

        /// <summary>
        /// 配置项的**往返**：存下去再读回来，每个键都必须一致。
        ///
        /// 为什么值得单独测：这个项目刚踩过一次 —— `AiBatchSize` 在 Load 里解析了、
        /// 但 `Set()` 的 switch 里忘了加分支，于是**用户配 7 也永远生效成 10**，
        /// 而且不报任何错。这类"解析了但没接上"的问题只有往返断言能发现，
        /// 单看"文件里写对了吗"是发现不了的。
        /// </summary>
        private static void TestSettingsRoundTrip(string work)
        {
            var path = Path.Combine(work, "settings-roundtrip.ini");

            var s = new AppSettings
            {
                BiqugaOfflineWorkers = 12,
                BiqugaOnlineWorkers = 5,
                BiqugaPcWorkers = 4,
                MinDelayMs = 111,
                MaxDelayMs = 999,
                CrawlTimeoutMinutes = 42,
                RetryPasses = 2,
                OutputTraditional = true,
            };
            s.Save(path);
            var b = AppSettings.Load(path);
            Eq("设置往返：离线并发", 12, b.BiqugaOfflineWorkers);
            Eq("设置往返：在线并发", 5, b.BiqugaOnlineWorkers);
            Eq("设置往返：PC 并发", 4, b.BiqugaPcWorkers);
            Eq("设置往返：最小间隔", 111, b.MinDelayMs);
            Eq("设置往返：最大间隔", 999, b.MaxDelayMs);
            Eq("设置往返：遍历超时", 42, b.CrawlTimeoutMinutes);
            Eq("设置往返：重试轮数", 2, b.RetryPasses);
            Eq("设置往返：输出繁体", true, b.OutputTraditional);

            // AI 配置：字符串 + 一个数值，最容易出现"解析了但没接上"
            var a = new AppSettings
            {
                AiEnabled = true,
                AiBackend = "openai",
                AiBaseUrl = "https://api.deepseek.com",
                AiModel = "deepseek-chat",
                AiApiKey = "sk-test-key-123",
                AiBatchSize = 7,
            };
            a.Save(path);
            var ab = AppSettings.Load(path);
            Eq("设置往返：AI 开关", true, ab.AiEnabled);
            Eq("设置往返：AI 后端", "openai", ab.AiBackend);
            Eq("设置往返：AI 地址", "https://api.deepseek.com", ab.AiBaseUrl);
            Eq("设置往返：AI 模型", "deepseek-chat", ab.AiModel);
            Eq("设置往返：AI Key", "sk-test-key-123", ab.AiApiKey);
            // ★ 这一条就是那个 bug 的守卫
            Eq("设置往返：AI 批大小（曾忘在 Set 里接，配 7 也变 10）", 7, ab.AiBatchSize);

            // AI 默认必须是关的：这个工具一直守"不把内容交给第三方"，
            // 默认开等于偷偷改变了这个姿态。
            var def = AppSettings.Load(Path.Combine(work, "不存在的配置.ini"));
            Eq("设置默认值：AI 默认关闭", false, def.AiEnabled);
            Eq("设置默认值：AI 默认用本地后端", "ollama", def.AiBackend);
            Eq("设置默认值：AI 未填模型（不填就用不了）", "", def.AiModel);
            Eq("设置默认值：AI 未填 Key", "", def.AiApiKey);

            // 越界/写坏的值要被夹住或忽略（坏 ini 不能让程序崩）
            File.WriteAllText(path,
                "AiBatchSize=999\nBiqugaOfflineWorkers=0\nRetryPasses=99\nOutputTraditional=maybe\n",
                new UTF8Encoding(true));
            var c = AppSettings.Load(path);
            Check("设置夹取：批大小被夹到 1~50（" + c.AiBatchSize + "）",
                c.AiBatchSize >= 1 && c.AiBatchSize <= 50);
            Check("设置夹取：并发被夹到 ≥1", c.BiqugaOfflineWorkers >= 1);
            Check("设置夹取：重试轮数被夹到 ≤3", c.RetryPasses <= 3);
            Eq("设置夹取：非布尔的布尔值不改变默认", false, c.OutputTraditional);

            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ============================================================
        //  20) 回归守卫：日志里不许出现写死的"总量"
        // ============================================================

        /// <summary>
        /// 真实 bug：目录遍历的进度日志曾经写成
        ///     "目录遍历中：已收录 {0} 章（第 {1} 页，共约 {2} 页待走）"  最后一个参数是 772
        /// 那个 772 是我测《沧元图》时看到的页数，**顺手写死进了代码**。
        /// 用户拿它跑章节更多的书就看到"已收录 800 章（共约 772 页待走）"——
        /// 数字自己打自己，用户合理地来问"你这不对吧"。
        ///
        /// 更本质的问题：这个遍历是**从最后一章往回走**的，
        /// 在走回目录页之前**根本不可能知道总页数**。
        /// 所以"共约 N 页"这个说法本身就是编的，压根不该出现在日志里。
        ///
        /// 这条断言扫源码文本，防止以后又有人把某个实测数字写进提示语。
        /// </summary>
        private static void TestNoHardcodedTotalsInLogs()
        {
            var root = FindRepoRoot();
            if (root == null)
            {
                Check("日志硬编码守卫：没找到仓库根目录，跳过", true);
                return;
            }

            var bad = new List<string>();
            var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                string[] lines;
                try { lines = File.ReadAllLines(f, Encoding.UTF8); }
                catch { continue; }

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var t = line.TrimStart();
                    // 注释行不算 —— 我们正是在注释里解释这个 bug 的
                    if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal))
                        continue;
                    if (line.IndexOf("共约", StringComparison.Ordinal) >= 0 ||
                        line.IndexOf("页待走", StringComparison.Ordinal) >= 0)
                        bad.Add(Path.GetFileName(f) + ":" + (i + 1) + " " + t);
                }
            }

            Check("日志硬编码守卫：代码里没有写死的总量（" +
                  (bad.Count == 0 ? "干净" : string.Join(" ｜ ", bad.ToArray())) + "）",
                  bad.Count == 0);

            // 顺带确认新日志报的是"真实测得"的东西
            var sitePath = Path.Combine(root, "src", "Sites", "BiqugaSite.cs");
            if (File.Exists(sitePath))
            {
                var src = File.ReadAllText(sitePath, Encoding.UTF8);
                Contains("日志守卫：进度日志改报实际走过的页数", src, "走过 {1} 页");
            }
        }

        /// <summary>从程序所在目录往上找仓库根（有 src\Sites 和 tests 的那一级）</summary>
        private static string FindRepoRoot()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && d != null; i++)
            {
                if (Directory.Exists(Path.Combine(d.FullName, "src", "Sites")) &&
                    Directory.Exists(Path.Combine(d.FullName, "tests")))
                    return d.FullName;
                d = d.Parent;
            }
            return null;
        }

        // ============================================================
        //  21) 正文清洗：**不许误杀正文**（P0-1）+ 真实广告必须删掉（P0-2）
        // ============================================================

        /// <summary>
        /// 表驱动地验 TextCleaner.CleanBody。
        ///
        /// 为什么必须表驱动 + 逐条断言最终结果：
        /// 这个函数的前一版只判「广告标记是否出现」，结果把这些正常句子毁了 ——
        ///   「他还在为找不到回家的路而发愁。」→「他」
        ///   「他能够一秒记住整页内容，过目不忘。」→ 整行删除
        /// 15 条语料里 11 条被破坏。而**误删正文是静默损坏**：
        /// 用户看不出来，会以为作者就这么写的。所以这段断言的优先级高于一切广告规则。
        ///
        /// 第二段语料（真实广告）来自用户已下载的 5 本书，用来防止
        /// "为了修误杀把广告规则改废"。两段必须同时绿。
        /// </summary>
        private static void TestTextCleanerCorpus()
        {
            // ---------- 第一段：这些**必须原样保留**（正常正文 / 作者的话）----------
            var mustKeep = new[]
            {
                "他还在为找不到回家的路而发愁。",
                "她低声问道：你在关注公众号吗？",
                "别信什么全网免费的说法。",
                "他想看看最新章节请往下翻。",
                "他关注公众号已经三年了。",
                "他能够一秒记住整页内容，过目不忘。",
                "他查到了最新网址，记在了本子上。",
                "那本书的笔趣阁版本比这个全。",
                "他打开了手机版页面继续阅读。",
                "我要给你投月票",
                "这一章讲的是关于推荐票的故事，主角收到了很多推荐票，他很开心。",
                "真人在线服务很周到。",
                "“来，红包收着。”陈果递上大红包。",
                "“校长办公室里，有老师发红包，数量有限，先到先得。”",
                "卫国公吐出一口浊气：“罢了，你们穷得够呛，估计也办不起酒席。”",
                "他算出了 x < y 的结果。",
                "价格 <100 元",
                // 作者的话：作者真的会写"关注我的公众号"，删了就是毁书
                "————大伙可以关注下我的公众号“宅猪”，精彩书评、人物图、剧情讨论期待你们的参与！",
                "ps：国师已经成神，那残老村的诸老呢？大伙可以关注下公众号“宅猪”，查看相关资料。",
                "————宅在家里很久了，难得出门一次，更新受到影响，这里说一声抱歉！",
            };
            foreach (var line in mustKeep)
            {
                var got = TextCleaner.CleanBody(line);
                Eq("清洗不许误杀：「" + Short(line) + "」", line, got);
            }

            // ---------- 第二段：这些**必须被删掉**（真实广告，逐条来自已下载的书）----------
            var mustDrop = new[]
            {
                "【领红包】现金or点币红包已经发放到你的账户！微信关注公.众.号【书友大本营】领取！",
                "#送888现金红包#关注vx.公众号【书友大本营】，看热门神作，抽888现金红包！",
                "没钱看小说？送你现金or点币，限时1天领取！关注公·众·号【书友大本营】，免费领！",
                "交流好书，关注vx公众号.【书友大本营】。现在关注，可领现金红包！",
                "【看书福利】关注公众..号【书友大本营】，每天看书抽现金点币!",
                "【收集免费好书】关注v.x【书友大本营】推荐你喜欢的小说，领现金红包！",
                "本书由公众号整理制作。关注VX【书友大本营】，看书领现金红包！",
                "【送红包】阅读福利来啦！你有最高888现金红包待抽取！关注weixin公众号【书友大本营】抽红包！",
                "大家好，我们公众.号每天都会发现金、点币红包，只要关注就可以领取。年末最后一次福利，请大家抓住机会。公众号[书友大本营]",
                "，最快更新神级高手在都市最新章节！",
                "送你一个现金红包",
            };
            foreach (var line in mustDrop)
            {
                var got = TextCleaner.CleanBody(line);
                Eq("清洗必须删广告：「" + Short(line) + "」", "", got);
            }

            // 行尾拼接：正文留下、广告切掉
            var tail = TextCleaner.CleanBody("他转身就走。帮你找书陪你尬聊");
            Eq("清洗：行尾拼接的广告被切掉、正文保留", "他转身就走。", tail);

            var tail2 = TextCleaner.CleanBody(
                "“是!”苏沐橙拿着她的新角色开心地去练级了。有最新章节更新及时");
            Check("清洗：正文句尾拼接广告后正文还在（" + Short(tail2) + "）",
                tail2.IndexOf("苏沐橙", StringComparison.Ordinal) >= 0);

            // ---------- 第三段：反爬水印 ----------
            var wm = "有异能者挺身而出，提出了建议。<span style='display:none'>gfbmmjD6vtLSaDjNAMr7x+abcdefghijklmnop==</span>";
            var wmOut = TextCleaner.CleanBody(wm);
            Check("水印：现形标签被清掉（" + Short(wmOut) + "）",
                wmOut.IndexOf("span", StringComparison.OrdinalIgnoreCase) < 0);
            Check("水印：正文保住了", wmOut.IndexOf("有异能者挺身而出", StringComparison.Ordinal) >= 0);

            var blob = "gfbmmjD6vtLSaDjNAMr7x+abcdefghijklmnopqrstuvwxyz0123456789==";
            Eq("水印：纯 base64 行被丢弃", "", TextCleaner.CleanBody(blob));
            Eq("水印：含 < 的正常句子不动（反例）", "他算出了 x < y 的结果。",
                TextCleaner.CleanBody("他算出了 x < y 的结果。"));

            // ---------- 第四段：幂等性（清洗两遍结果一致）----------
            foreach (var line in mustKeep)
            {
                var once = TextCleaner.CleanBody(line);
                var twice = TextCleaner.CleanBody(once);
                Eq("清洗：幂等（第二遍不再改动）「" + Short(line) + "」", once, twice);
            }
        }

        private static string Short(string s)
        {
            if (s == null) return "null";
            return s.Length <= 18 ? s : s.Substring(0, 18) + "…";
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
        //  13) ZhConvert：简繁转换（字表/词表全部内联，不读任何外部字典文件）
        // ============================================================

        private static void TestZhConvert()
        {
            // --- 基本字表命中：简体 → 繁体 ---
            Eq("ZhConvert 们→們", "我們", ZhConvert.ToTraditional("我们"));
            Eq("ZhConvert 个→個", "一個", ZhConvert.ToTraditional("一个"));
            Eq("ZhConvert 无/线", "無線", ZhConvert.ToTraditional("无线"));
            Eq("ZhConvert 时/间", "時間", ZhConvert.ToTraditional("时间"));
            Eq("ZhConvert 整句 简→繁", "我們都來了，這裡沒有人。",
                ZhConvert.ToTraditional("我们都来了，这里没有人。"));

            // --- 字表默认值 vs 词级例外（一字多形，只有靠词才分得清）---
            Eq("ZhConvert 发 默认→發", "發現", ZhConvert.ToTraditional("发现"));
            Eq("ZhConvert 词级例外 头发→頭髮", "頭髮", ZhConvert.ToTraditional("头发"));
            Eq("ZhConvert 词级例外 发型→髮型", "髮型", ZhConvert.ToTraditional("发型"));
            Eq("ZhConvert 词级例外 理发→理髮", "理髮", ZhConvert.ToTraditional("理发"));
            Eq("ZhConvert 词级例外 皇后→皇后（后 不换後）", "皇后", ZhConvert.ToTraditional("皇后"));
            Eq("ZhConvert 词级例外 后来→後來", "後來", ZhConvert.ToTraditional("后来"));
            Eq("ZhConvert 词级例外 公里→公里（里 不换裡）", "公里", ZhConvert.ToTraditional("公里"));
            Eq("ZhConvert 词级例外 里程→里程", "里程", ZhConvert.ToTraditional("里程"));
            Eq("ZhConvert 词级例外 里面→裡面", "裡面", ZhConvert.ToTraditional("里面"));
            Eq("ZhConvert 词级例外 一台→一臺", "一臺", ZhConvert.ToTraditional("一台"));
            Eq("ZhConvert 词级例外 台风→颱風", "颱風", ZhConvert.ToTraditional("台风"));
            Eq("ZhConvert 只 默认不换（只有）", "只有", ZhConvert.ToTraditional("只有"));
            Eq("ZhConvert 词级例外 一只→一隻", "一隻", ZhConvert.ToTraditional("一只"));
            Eq("ZhConvert 干 默认不换（干扰）", "干擾", ZhConvert.ToTraditional("干扰"));
            Eq("ZhConvert 词级例外 干净→乾淨", "乾淨", ZhConvert.ToTraditional("干净"));
            Eq("ZhConvert 词级例外 干部→幹部", "幹部", ZhConvert.ToTraditional("干部"));
            Eq("ZhConvert 词级例外 计划→計劃", "計劃", ZhConvert.ToTraditional("计划"));

            // --- 往返：简→繁→简 不能串味，繁体专有字也要能单独反查回来 ---
            Eq("ZhConvert 往返 简→繁→简", "我们说话的时候，这里没有人。",
                ZhConvert.ToSimplified(ZhConvert.ToTraditional("我们说话的时候，这里没有人。")));
            Eq("ZhConvert 往返 繁→简→繁", "我們說話的時候，這裡沒有人。",
                ZhConvert.ToTraditional(ZhConvert.ToSimplified("我們說話的時候，這裡沒有人。")));
            Eq("ZhConvert 繁→简 头发", "头发", ZhConvert.ToSimplified("頭髮"));
            Eq("ZhConvert 反查 發→发", "发", ZhConvert.ToSimplified("發"));
            Eq("ZhConvert 反查 髮→发（词表补的反查项）", "发", ZhConvert.ToSimplified("髮"));
            Eq("ZhConvert 反查 隻→只", "只", ZhConvert.ToSimplified("隻"));
            Eq("ZhConvert 反查 臺→台", "台", ZhConvert.ToSimplified("臺"));

            // --- 幂等：转过的正文再转一次不变（反复处理不会累积失真）---
            var mixed = "第1章 少女说：「后来我去了台北，头发也剪短了。」abc 123";
            var once = ZhConvert.ToTraditional(mixed);
            Eq("ZhConvert 混合句 简→繁 幂等", once, ZhConvert.ToTraditional(once));
            var back = ZhConvert.ToSimplified(once);
            Eq("ZhConvert 混合句 繁→简 幂等", back, ZhConvert.ToSimplified(back));
            Eq("ZhConvert 混合句 繁→简 回到原文", mixed, back);
            Contains("ZhConvert 混合句含 後來", once, "後來");
            Contains("ZhConvert 混合句含 臺北", once, "臺北");
            Contains("ZhConvert 混合句含 頭髮", once, "頭髮");
            Contains("ZhConvert 混合句 ASCII 原样", once, "abc 123");

            // --- 表里没有的字必须原样透传（正文里大量汉字不在表内）---
            var plain = "甲乙丙丁戊己庚辛壬癸 ABC 123 ！？，。";
            Eq("ZhConvert 无命中原样（简→繁）", plain, ZhConvert.ToTraditional(plain));
            Eq("ZhConvert 无命中原样（繁→简）", plain, ZhConvert.ToSimplified(plain));

            // --- null / 空 安全：原样进原样出，不抛异常 ---
            Eq("ZhConvert ToTraditional null", null, ZhConvert.ToTraditional(null));
            Eq("ZhConvert ToTraditional 空串", "", ZhConvert.ToTraditional(""));
            Eq("ZhConvert ToSimplified null", null, ZhConvert.ToSimplified(null));
            Eq("ZhConvert ToSimplified 空串", "", ZhConvert.ToSimplified(""));
            Eq("ZhConvert LooksTraditional null", false, ZhConvert.LooksTraditional(null));
            Eq("ZhConvert LooksTraditional 空串", false, ZhConvert.LooksTraditional(""));
            Eq("ZhConvert LooksTraditional 简体=false", false, ZhConvert.LooksTraditional("我们都来了"));
            Eq("ZhConvert LooksTraditional 繁体=true", true, ZhConvert.LooksTraditional("我們都來了"));

            // --- DetectScript：按样本里"繁体专有字 / 简体专有字"的数量投票 ---
            Eq("ZhConvert DetectScript 简体", "zh-CN", ZhConvert.DetectScript("我们说话的时候，这里没有人。"));
            Eq("ZhConvert DetectScript 繁体", "zh-TW", ZhConvert.DetectScript("我們說話的時候，這裡沒有人。"));
            Eq("ZhConvert DetectScript null", "zh-CN", ZhConvert.DetectScript(null));
            Eq("ZhConvert DetectScript 空串", "zh-CN", ZhConvert.DetectScript(""));
            Eq("ZhConvert DetectScript 纯空白", "zh-CN", ZhConvert.DetectScript("  \r\n\t "));
            Eq("ZhConvert DetectScript 无证据（英文数字）", "zh-CN", ZhConvert.DetectScript("abc 123 !?"));
        }

        /// <summary>
        /// 繁简转换的「里 / 裡」补充回归。
        ///
        /// 背景：字表里**故意没有** 里→裡 这条映射 —— 里本身就是合法繁体字，
        /// 做字级替换会把「公里」「里程」误写成「公裡」「裡程」。
        /// 所以"里当内部讲"的情况只能靠词表兜。最初只加了「里面」，
        /// 实测发现 `这里` 会输出「這里」（港台通行写法是「這裡」），
        /// 整本书通篇错一个字很显眼，于是补了 这里/那里/哪里/心里… 一组。
        /// 这里把两边的边界都钉住，免得以后有人"顺手"给字表加上 里→裡。
        /// </summary>
        private static void TestZhConvertLi()
        {
            // 当"内部"讲 → 裡
            Eq("ZhConvert 里 这里→這裡", "這裡", ZhConvert.ToTraditional("这里"));
            Eq("ZhConvert 里 那里→那裡", "那裡", ZhConvert.ToTraditional("那里"));
            Eq("ZhConvert 里 哪里→哪裡", "哪裡", ZhConvert.ToTraditional("哪里"));
            Eq("ZhConvert 里 心里→心裡", "心裡", ZhConvert.ToTraditional("心里"));
            Eq("ZhConvert 里 手里→手裡", "手裡", ZhConvert.ToTraditional("手里"));

            // 当"长度/故乡"讲 → 必须保持 里（这几条正是字表不做映射的理由）
            Eq("ZhConvert 里 故里不被误改", "故里", ZhConvert.ToTraditional("故里"));
            Eq("ZhConvert 里 万里→萬里", "萬里", ZhConvert.ToTraditional("万里"));

            // 同一句里两种情况同时出现，必须各归各的
            var s = ZhConvert.ToTraditional("他跑了一公里，后来回到那里。");
            Contains("ZhConvert 里 同句公里保持里", s, "公里");
            Contains("ZhConvert 里 同句那里用裡", s, "那裡");
            Contains("ZhConvert 里 同句后来用後", s, "後來");

            // 反查：裡 能回退成 里（T2C 里这条是靠词表补的，不是字表）
            Eq("ZhConvert 里 這裡→这里", "这里", ZhConvert.ToSimplified("這裡"));
            Eq("ZhConvert 里 公里往返不变", "公里", ZhConvert.ToSimplified(ZhConvert.ToTraditional("公里")));
            Eq("ZhConvert 里 那里往返不变", "那里", ZhConvert.ToSimplified(ZhConvert.ToTraditional("那里")));
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
