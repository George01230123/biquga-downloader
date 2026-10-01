using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 下载执行器：界面和命令行自测都用这一份代码，保证“测过的就是你会用到的”。
    /// 负责：逐章下载 → 边下边存 → 失败重试 → 最后修正表头 → 报告路径。
    /// </summary>
    public class DownloadRunner
    {
        public ISite Site;
        public BookInfo Book;
        // 初始化成空列表：以前这里是 null，程序化调用（自测/脚本/未来的 CLI）先 Add 再 Run
        // 会直接空引用炸掉；Run() 里本来就有"没有选中任何章节"的明确报错。
        public List<ChapterInfo> Chapters = new List<ChapterInfo>();
        public string RootDir;          // 保存的根目录
        public Action<string> Log = delegate { };
        public Action<int, int> OnProgress = delegate { };   // (done, total)
        public Func<bool> IsCanceled = () => false;

        public int Ok, Skipped, Failed;
        public string OutputFile;
        public string BookDir;
        public int FromCacheCount;      // 有多少章是直接用目录遍历时抓到的缓存
        public string ReportFile;       // 缺失章节报告（只有真的缺章时才生成）

        /// <summary>跳过/失败的章节（按实际顺序），用于收尾报告“到底缺了哪几章”</summary>
        public readonly List<ChapterInfo> SkippedChapters = new List<ChapterInfo>();
        public readonly List<ChapterInfo> FailedChapters = new List<ChapterInfo>();
        /// <summary>cid → 失败原因（超时/404/风控提示…）</summary>
        public readonly Dictionary<string, string> FailReasons = new Dictionary<string, string>();

        /// <summary>失败章节自动重试轮数（0 = 不重试）。界面“设置”里可改。</summary>
        public int RetryPasses = 1;

        // 增量写盘用：攒够一批就 append，避免每次都重写整个文件
        private StringBuilder _pending = new StringBuilder();
        private bool _headerWritten;
        private long _appendedBytes;    // 本次追加了多少字节（更新模式要用它算累计正文长度）
        /// <summary>写表头用的编码（GBK 时不能带 BOM，否则老阅读器会把 BOM 当正文）</summary>
        private Encoding HeaderEncoding()
        {
            if (OutputEncoding != null && OutputEncoding.CodePage != 65001)
                return Encoding.GetEncoding(OutputEncoding.CodePage);   // 不带 BOM
            return new UTF8Encoding(true);
        }

        /// <summary>追加内容时的编码：UF8 不带 BOM（文件头已经写过了），GBK 直接用 GBK</summary>
        private Encoding AppendEncoding()
        {
            if (OutputEncoding != null && OutputEncoding.CodePage != 65001) return OutputEncoding;
            return new UTF8Encoding(false);
        }

        private long _baseBodyBytes;    // 更新模式下文件的原始大小
        private Dictionary<string, ChapterSpan> _existing = new Dictionary<string, ChapterSpan>();
        private bool _replaceMissing;    // 是否把新章节插到文件里的正确位置（有锚点时为 true）
        private int _inserted;           // 本次实际插入到文件里的章数

        /// <summary>算出一本书的保存目录与 txt 路径</summary>
        public static void ResolvePaths(string rootDir, string bookTitle, out string bookDir, out string txtPath)
        {
            bookDir = Path.Combine(rootDir, Http.SafeFileName(bookTitle));
            txtPath = Path.Combine(bookDir, Http.SafeFileName(bookTitle) + ".txt");
        }

        public void Run()
        {
            if (Site == null || Book == null || Chapters == null) throw new Exception("下载器没有初始化");
            if (Chapters.Count == 0) throw new Exception("没有选中任何章节");

            var root = string.IsNullOrEmpty(RootDir)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载")
                : RootDir;

            ResolvePaths(root, Book.Title, out BookDir, out OutputFile);
            Log("保存根目录：" + root);
            Log("本书目录：" + BookDir);
            Directory.CreateDirectory(BookDir);
            if (!Directory.Exists(BookDir))
                throw new Exception("创建目录失败：" + BookDir + "（可能没有写入权限）");

            // 计数器与列表每次都从零开始（同一个 runner 对象被复用也不会串数据）
            Ok = Skipped = Failed = FromCacheCount = 0;
            SkippedChapters.Clear();
            FailedChapters.Clear();
            FailReasons.Clear();
            _failedPass.Clear();
            _pending.Length = 0;
            _headerWritten = false;
            _appendedBytes = 0;
            int total = Chapters.Count;

            // 更新已有文件：记住它原来的大小，并准备"文件里已经有哪些章"的索引。
            // 有锚点就**按章节位置插入**（这样缺章补齐、断点续传都能落到正确位置）；
            // 旧文件没有锚点则退化成"追加到末尾"。
            if (AppendToExistingFile && File.Exists(OutputFile))
            {
                _baseBodyBytes = new FileInfo(OutputFile).Length;
                _headerWritten = true;      // 别重写表头
                _existing = ChapterIndex.ById(ChapterIndex.Scan(OutputFile));
                _replaceMissing = _existing.Count > 0;
                Log(string.Format("增量/补齐模式：已有文件 {0:N0} 字节，识别出 {1} 章{2}。",
                    _baseBodyBytes, _existing.Count,
                    _replaceMissing ? "（新章节会插入到正确位置）" : "（旧格式无锚点，只能追加到末尾）"));
            }
            else if (AppendToExistingFile)
            {
                // 调用方要求"追加"，但文件其实不存在（比如用户在下载过程中删了文件，
                // 或者目录缓存说是旧的、文件却没了）。这时按普通下载处理：
                // 正常写表头，收尾也走 FixHeaderNow —— 否则会生成一个没有表头的文件。
                AppendToExistingFile = false;
                Log("注意：文件不存在，本次按普通下载处理（会重新写表头）。");
            }

            for (int i = 0; i < total; i++)
            {
                if (IsCanceled())
                {
                    Log("已取消，正在保存已下载的部分…");
                    break;
                }
                var c = Chapters[i];
                OnProgress(i, total);
                FetchInto(c);
                OnProgress(i + 1, total);

                // 每 20 章把新增内容追加到文件（append 而不是重写整个文件，
                // 否则 1800 章的书要反复重写十几 MB，写入阶段会拖到十几分钟）
                if ((i + 1) % 20 == 0 || i == total - 1)
                {
                    try { FlushIncremental(); }
                    catch (Exception ex) { Log("写文件失败：" + ex.Message); }
                }
                Http.Polite();
            }

            // 自动重试：只对失败的那几章再跑一遍（站点偶尔抽风，重试一遍通常就好了）
            if (RetryPasses > 0 && FailedChapters.Count > 0 && !IsCanceled())
            {
                var retryList = new List<ChapterInfo>(FailedChapters);
                for (int pass = 1; pass <= RetryPasses; pass++)
                {
                    Log(string.Format("=== 自动重试第 {0}/{1} 轮：{2} 章 ===", pass, RetryPasses, retryList.Count));
                    var still = new List<ChapterInfo>();
                    _failedPass.Clear();
                    foreach (var c in retryList)
                    {
                        if (IsCanceled()) break;
                        FetchInto(c, true);
                        if (!string.IsNullOrEmpty(c.Text)) { c.Text = ""; continue; }   // 重试成功
                        still.Add(c);
                    }
                    FlushIncremental();
                    retryList = still;
                    if (retryList.Count == 0) { Log("重试后已无失败章节。"); break; }
                    Log(string.Format("重试第 {0} 轮结束，仍有 {1} 章失败。", pass, retryList.Count));
                    if (pass < RetryPasses) System.Threading.Thread.Sleep(1500);
                }
                if (retryList.Count > 0)
                    Log(string.Format("仍有 {0} 章没下到（多为站点侧空内容或风控），详见报告文件。", retryList.Count));
            }

            // 收尾：更新表头统计，并把“缺了哪几章”落成独立报告
            FlushIncremental();
            if (AppendToExistingFile)
            {
                // 增量更新：不能整块重写表头（会顶掉旧正文），改成"原位替换统计那一行"，
                // 并把进度标记（累计章数 + 指纹）写进去，供下次更新判断"哪些是新的"。
                // 统计要反映"文件里实际有多少章有正文"，而不是"本次下了几章"：
                //   · 断点续传：原进度 + 本次成功
                //   · 缺章补齐：文件里原本的章数 + 本次补上的（累计不变多，但进度标记要更新）
                int cumulative = CountChaptersWithBody(OutputFile);
                if (cumulative <= 0) cumulative = (CumulativeOkCount >= 0 ? CumulativeOkCount : 0) + Ok;
                int bodyBytes = (int)Math.Min(int.MaxValue, _baseBodyBytes + _appendedBytes);
                if (InjectHeaderStatistics(OutputFile, Book, cumulative, Skipped, Failed, Chapters, bodyBytes))
                    Log(string.Format("表头统计已更新：累计 {0} 章（本次新增 {1}，跳过 {2}，失败 {3}）",
                        cumulative, Ok, Skipped, Failed));
                else
                    Log("注意：这个文件的表头没有预留区（旧版本下载的），累计统计没写进去；正文不受影响。");
            }
            else
            {
                FixHeaderNow();
            }
            WriteMissingReport();
            if (Failed > 0 || Skipped > 0) LogMissingSummary();

            var fi = new FileInfo(OutputFile);
            Log(string.Format("文件已生成：{0}（{1:N0} 字节）", OutputFile, fi.Length));
            if (FromCacheCount > 0)
                Log(string.Format("其中 {0}/{1} 章直接复用了目录遍历时已抓到的正文（未重复请求站点）", FromCacheCount, Ok));
            Log(string.Format("本次下载：成功 {0} 章，跳过 {1} 章，失败 {2} 章", Ok, Skipped, Failed));
        }

        /// <summary>文件里"有正文"的章节数（统计和进度标记用；没有锚点时返回 0）</summary>
        public static int CountChaptersWithBody(string path)
        {
            try
            {
                int n = 0;
                foreach (var s in ChapterIndex.Scan(path)) if (s.HasBody) n++;
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 缺章补齐 / 章节级续传的核心：找出文件里**缺失或空掉的章节**，
        /// 只重新抓这些章，并按目录顺序插回正确位置（不是追加到末尾）。
        ///
        /// 两个功能共用这一份实现，区别只是调用方的意图：
        ///   · 断点续传：上次中断，文件里后面还缺一大段 → 缺章列表就是"剩下的"
        ///   · 缺章补齐：整本已下完，中间有站点空内容 → 缺章列表就是中间那几个洞
        /// </summary>
        public static void FillMissing(DownloadRunner runner, string filePath, Action<string> log)
        {
            if (runner == null || string.IsNullOrEmpty(filePath)) return;
            if (log == null) log = delegate { };

            if (!File.Exists(filePath)) { log("文件不存在，没法补齐：" + filePath); return; }

            var spans = ChapterIndex.Scan(filePath);
            if (spans.Count == 0)
            {
                log("这个文件没有章节锚点（v1.0.4 之前下载的），没法定位到具体某一章；");
                log("请点「下载全部章节」重新下载一本（会写成带锚点的新格式）。");
                return;
            }

            var have = new Dictionary<string, ChapterSpan>();
            foreach (var s in spans)
            {
                // 只有标题没有正文的章也算"缺"，需要重抓
                if (!string.IsNullOrEmpty(s.Id) && s.HasBody && !have.ContainsKey(s.Id)) have[s.Id] = s;
            }

            var missing = new List<ChapterInfo>();
            foreach (var c in runner.Book.Chapters)
            {
                if (c == null || c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
                if (!have.ContainsKey(c.Id)) missing.Add(c);
            }

            if (missing.Count == 0)
            {
                log(string.Format("检查完毕：文件里 {0} 章都有正文，没有需要补齐的。", have.Count));
                return;
            }

            log(string.Format("检查完毕：文件里 {0} 章完整，缺 {1} 章；开始只抓这 {2} 章（已有的不动）。",
                have.Count, missing.Count, missing.Count));

            runner.Chapters = missing;
            runner.AppendToExistingFile = true;
            runner.RetryPasses = 0;                  // 补齐只跑一遍，失败的下次再来（避免长时间卡住）
            // 保存根目录从文件路径反推（文件一定是「根目录\书名\书名.txt」）：
            // 这样调用方忘了设 RootDir 也不会崩（离线单测就踩过这个）
            if (string.IsNullOrEmpty(runner.RootDir))
            {
                var bookDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                var rootDir = (bookDir == null) ? null : Path.GetDirectoryName(bookDir);
                runner.RootDir = string.IsNullOrEmpty(rootDir) ? bookDir : rootDir;
            }
            runner.Run();

            log(string.Format("补齐结束：新补上 {0} 章，插入位置已按目录顺序排好。", runner.Ok));
            if (runner.Failed > 0)
                log(string.Format("仍有 {0} 章抓不到（多为站点侧空内容），下次可以再点一次补齐。", runner.Failed));
        }

        /// <summary>失败原因按“这一轮”记账（重试成功后要把上一轮的原因清掉）</summary>
        private readonly Dictionary<string, string> _failedPass = new Dictionary<string, string>();

        /// <summary>
        /// 抓一章并追加到待写缓冲。成功/跳过/失败的计数与列表都在这里维护，
        /// 这样“重试”走的是完全相同的代码路径（测过的就是会跑的）。
        /// </summary>
        private void FetchInto(ChapterInfo c, bool isRetry = false)
        {
            int total = Chapters.Count;
            string prefix = isRetry ? "[重试] " : "";
            try
            {
                // 目录遍历时顺手抓到的正文（biquga 会把整本正文缓存下来），这里直接用
                var text = c.Text;
                if (string.IsNullOrEmpty(text))
                {
                    var provider = Site as ITextCacheProvider;
                    if (provider != null) text = provider.GetCachedText(c.Id);
                }
                if (!string.IsNullOrEmpty(text)) FromCacheCount++;
                else text = Site.LoadChapter(Book, c, null);

                if (string.IsNullOrEmpty(text) || text.Length < 40)
                {
                    Skipped++;
                    c.Text = "";
                    if (!SkippedChapters.Contains(c)) SkippedChapters.Add(c);
                    Log(string.Format("{0}跳过（站点侧空内容/公告）：{1}", prefix, c.Title));
                }
                else
                {
                    Ok++;
                    c.Text = text;
                    // 章节块统一由 ChapterIndex 生成：里面会先写一行锚点 <!--c:id-->，
                    // 「缺章补齐」和「章节级续传」靠它定位到具体某一章（见 ChapterIndex.cs）
                    var block = ChapterIndex.BuildChapterBlock(c.Id, c.Title, text);
                    if (_replaceMissing)
                    {
                        // 插入模式：直接落到文件里的正确位置（不是简单追加）
                        if (ChapterIndex.InsertChapter(OutputFile, Book, c, text)) _inserted++;
                        else { _pending.Append(block); Log("（插入失败，退化为追加）" + c.Title); }
                    }
                    else
                    {
                        _pending.Append(block);
                    }
                    if (isRetry && Failed > 0) Failed--;                 // 重试成功：把上一轮的失败计数还回去
                    FailReasons.Remove(c.Id);
                    FailedChapters.Remove(c);
                    Log(string.Format("{0}完成 {1}（{2} 字）", prefix, c.Title, text.Length));
                }
            }
            catch (Exception ex)
            {
                if (isRetry) Failed--;                                   // 计入本轮的失败
                Failed++;
                c.Text = "";
                _failedPass[c.Id] = ex.Message;
                FailReasons[c.Id] = ex.Message;
                if (!FailedChapters.Contains(c)) FailedChapters.Add(c);
                Log(string.Format("{0}失败 {1}：{2}", prefix, c.Title, ex.Message));
            }
        }

        /// <summary>
        /// 收尾报告：把“缺了哪几章”单独写一个文件，比塞进表头可靠
        /// （表头是原地改写的，越写越长会盖到正文上）。
        /// </summary>
        public void WriteMissingReport()
        {
            if (SkippedChapters.Count == 0 && FailedChapters.Count == 0) { ReportFile = null; return; }
            // 报告只是“锦上添花”，缺任何前提条件都不该让整个下载流程炸掉
            if (Book == null || string.IsNullOrEmpty(BookDir)) { ReportFile = null; return; }
            try
            {
                ReportFile = Path.Combine(BookDir, Http.SafeFileName(Book.Title) + ".缺失章节.txt");
                var sb = new StringBuilder();
                sb.AppendLine("《" + Book.Title + "》缺失章节报告");
                sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine(string.Format("本次下载：成功 {0} 章，跳过 {1} 章，失败 {2} 章，共处理 {3} 章",
                    Ok, Skipped, Failed, Chapters.Count));
                sb.AppendLine(new string('=', 46));
                sb.AppendLine();
                sb.AppendLine("【失败 " + FailedChapters.Count + " 章】—— 网络/站点风控导致，重新勾选这几章再下一次通常就好了");
                int n = 0;
                foreach (var c in FailedChapters)
                {
                    n++;
                    string reason;
                    if (!FailReasons.TryGetValue(c.Id, out reason) || string.IsNullOrEmpty(reason)) reason = "未知";
                    sb.AppendLine(string.Format("{0}. 第 {1} 章 {2}", n, c.Order, c.Title));
                    sb.AppendLine("   原因：" + reason);
                    sb.AppendLine("   地址：" + ChapterUrl(c));
                }
                if (FailedChapters.Count == 0) sb.AppendLine("（无）");
                sb.AppendLine();
                sb.AppendLine("【跳过 " + SkippedChapters.Count + " 章】—— 站点这一章本身就是空的（“正在手打中，请稍等片刻”），");
                sb.AppendLine("不是工具的 bug，换别的站点/源也不会有内容。");
                n = 0;
                foreach (var c in SkippedChapters)
                {
                    n++;
                    sb.AppendLine(string.Format("{0}. 第 {1} 章 {2}", n, c.Order, c.Title));
                    sb.AppendLine("   地址：" + ChapterUrl(c));
                }
                if (SkippedChapters.Count == 0) sb.AppendLine("（无）");
                File.WriteAllText(ReportFile, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Log("写缺失章节报告失败：" + ex.Message);
                ReportFile = null;
            }
        }

        private string ChapterUrl(ChapterInfo c)
        {
            try
            {
                if (Book != null && Book.Site == "fanqie")
                    return "https://fanqienovel.com/reader/" + c.Id;
                var dir = Book == null ? "" : Book.Dir;
                if (string.IsNullOrEmpty(dir)) return "(无)";
                if (!dir.StartsWith("/")) dir = "/" + dir;
                if (!dir.EndsWith("/")) dir += "/";
                return "https://www.biquga.com" + dir + c.Id + ".html";
            }
            catch { return "(无)"; }
        }

        /// <summary>日志里也报一遍（取前 8 章，剩下的让用户看报告文件）</summary>
        public void LogMissingSummary()
        {
            if (FailedChapters.Count > 0)
            {
                Log(string.Format("失败章节（{0} 章，重新勾选这几章再下一次通常就好了）：", FailedChapters.Count));
                for (int i = 0; i < FailedChapters.Count && i < 8; i++)
                    Log(string.Format("  · 第 {0} 章 {1}", FailedChapters[i].Order, FailedChapters[i].Title));
                if (FailedChapters.Count > 8) Log(string.Format("  · …其余 {0} 章见报告文件", FailedChapters.Count - 8));
            }
            if (SkippedChapters.Count > 0)
            {
                Log(string.Format("跳过章节（{0} 章，站点侧本身就是空内容）：", SkippedChapters.Count));
                for (int i = 0; i < SkippedChapters.Count && i < 8; i++)
                    Log(string.Format("  · 第 {0} 章 {1}", SkippedChapters[i].Order, SkippedChapters[i].Title));
                if (SkippedChapters.Count > 8) Log(string.Format("  · …其余 {0} 章见报告文件", SkippedChapters.Count - 8));
            }
            if (!string.IsNullOrEmpty(ReportFile)) Log("缺失章节报告：" + ReportFile);
        }

        /// <summary>表头区预留的字节数（表头本身只有几百字节，留 2KB 足够宽裕）</summary>
        internal const int ReservedHeaderBytes = 2048;

        /// <summary>表头区标记：有了它，收尾时才知道这个文件预留了多少字节可以原地改写</summary>
        internal const string HeaderZoneMarker = "#header-zone:";

        /// <summary>
        /// 进度标记（写进表头）：记录"已经成功写入正文的章节数"和这些章节 id 的指纹。
        /// 增量更新靠它判断"哪些章是新的" —— 不用去解析文件名，也不怕标题被站点改过。
        /// 格式故意用 key:value，方便机器读，同时人类看也不刺眼。
        /// </summary>
        internal const string ProgressOkMarker = "#progress-ok:";

        /// <summary>
        /// 是否往已有文件**追加**（增量更新模式）。
        /// 这种模式下不重写表头（否则会把已有正文顶掉），只 append 新章节，
        /// 收尾再用 InjectHeaderStatistics 原位更新统计。
        /// </summary>
        /// <summary>输出文件编码（站点配置里可设为 gbk；默认 utf-8 带 BOM）</summary>
        public Encoding OutputEncoding = new UTF8Encoding(true);

        public bool AppendToExistingFile;

        /// <summary>本次是"更新已有文件"：表头统计里显示累计章数而不是本次新增数</summary>
        public int CumulativeOkCount = -1;

        /// <summary>章节 id 列表的短指纹（MD5 前 12 位十六进制）</summary>
        internal static string Fingerprint(string s)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var h = md5.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// 从已有文件里读出进度：txt 里已经有多少章、指纹是什么、正文有多少字符。
        /// 读不到（旧版本文件、手工拼的文件）就返回 -1，调用方走"整本重新下载"。
        /// </summary>
        public static bool TryReadProgress(string path, out int okCount, out string fingerprint, out int bodyChars)
        {
            okCount = -1; fingerprint = null; bodyChars = -1;
            try
            {
                if (!File.Exists(path)) return false;
                var bytes = File.ReadAllBytes(path);
                int bom = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
                int scan = Math.Min(16384, bytes.Length - bom);
                if (scan <= 0) return false;
                var text = Encoding.ASCII.GetString(bytes, bom, scan);
                int i = text.IndexOf(ProgressOkMarker, StringComparison.Ordinal);
                if (i < 0) return false;
                int end = text.IndexOf('\n', i);
                var payload = text.Substring(i + ProgressOkMarker.Length,
                    (end < 0 ? text.Length : end) - i - ProgressOkMarker.Length).Trim();
                var parts = payload.Split(':');
                if (parts.Length < 2) return false;
                int ok;
                if (!int.TryParse(parts[0].Trim(), out ok) || ok < 0) return false;
                okCount = ok;
                fingerprint = parts[1].Trim();
                int bc;
                if (parts.Length >= 3 && int.TryParse(parts[2].Trim(), out bc)) bodyChars = bc;
                return true;
            }
            catch { return false; }
        }

        /// <summary>表头 + 补白 + 表头区标记（正文从这之后才开始，所以正文永远不会被表头挤到）</summary>
        internal string BuildHeaderZone()
        {
            var head = BuildHeader();
            int headBytes = Encoding.UTF8.GetByteCount(head);
            int pad = ReservedHeaderBytes - headBytes;
            if (pad < 0) pad = 0;
            // 末尾那个 '\n' 很关键：让 #header-zone 标记**自成一行**。
            // 否则标记紧贴在补白后面，增量更新时"往前找行首"会命中补白里的换行，
            // 把「下载时间 / 46 个等号」两行误当成补白填掉（真实 bug，等号行会消失）。
            return head + new string('\n', pad) + "\n" + HeaderZoneMarker + ReservedHeaderBytes + "\n";
        }

        /// <summary>
        /// 增量落盘：第一次写表头 + **预留表头区**（带 UTF-8 BOM），之后只把新增的正文 append 上去。
        /// 预留表头区是为了让收尾时的“原位改表头”永远有地方写：
        /// 否则统计行一长（例如多出“缺失 N 章”那一行），新表头占的字节比旧的多，
        /// 就会盖掉正文开头几个字 —— 这是离线单测真实抓出来的 bug。
        ///
        /// AppendToExistingFile=true（更新已有文件）时跳过写表头：表头已经在那儿了，
        /// 重写一次会从文件开头覆盖，把已有正文顶掉。收尾用 InjectHeaderStatistics 更新统计。
        /// </summary>
        private void FlushIncremental()
        {
            if (_pending.Length == 0 && (_headerWritten || AppendToExistingFile)) return;
            if (!_headerWritten && !AppendToExistingFile)
            {
                using (var w = new StreamWriter(OutputFile, false, HeaderEncoding()))
                    w.Write(BuildHeaderZone());
                _headerWritten = true;
            }
            if (_pending.Length > 0)
            {
                var chunk = _pending.ToString();
                using (var w = new StreamWriter(OutputFile, true, AppendEncoding()))
                    w.Write(chunk);
                // 记下追加的字节数（更新模式算累计正文长度用；UTF-8 下等于字节数）
                _appendedBytes += Encoding.UTF8.GetByteCount(chunk);
                _pending.Length = 0;
            }
        }

        /// <summary>
        /// 在一段可显示区域里，把「本次下载：成功 …」那一行换成带进度标记的新统计，
        /// 用换行补齐到原来的长度。文件长度不变 → 不会顶掉后面的正文。
        ///
        /// 这是增量更新的收尾步骤：新章节 append 完之后，表头要反映"累计多少章"。
        /// 判据是 zoneLen（表头区大小，由 #header-zone: 标记给出），信息不全就不改。
        /// </summary>
        public static bool InjectHeaderStatistics(string path, BookInfo book, int cumulativeOk, int skipped, int failed,
            IList<ChapterInfo> chapters, int bodyChars)
        {
            // ============================================================
            //  做法：把「表头起点 → 正文起点」这一整块（表头 + 补白 + 标记行）
            //  **整个重建**后写回，总字节数严格守恒。
            //
            //  为什么重建而不是"只替换统计行"：表头里既有中文（字符≠字节）又有多行，
            //  只替换一行要做到"覆盖长度精确"很脆 —— 我已经在这上面栽了三次：
            //    1) 把 ASCII 解码后的字符下标当字节偏移用（覆盖长度算大 2 倍多）；
            //    2) "往后找行首"命中补白里的换行，把后面几行当补白填掉；
            //    3) 定长补齐与"放不下就放弃"互相打架，统计永远更新不了。
            //  重建法只有一个约束：新表头 + 补白 + 标记行 ≤ 原来的表头区字节数，否则整体放弃。
            // ============================================================
            try
            {
                if (!File.Exists(path)) return false;
                var bytes = File.ReadAllBytes(path);
                int bom = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
                int scan = Math.Min(16384, bytes.Length - bom);
                if (scan <= 0) return false;

                // 表头区总字节数 = 从（BOM 后的）文件开头到 #header-zone 标记那一行的行尾。
                // 标记行本身的字节数 = 标记 + 数字 + 换行（数字位数会变，所以**不能写死**，要量出来）。
                int markerAt = IndexOfBytes(bytes, bom, scan, Encoding.UTF8.GetBytes(HeaderZoneMarker));
                if (markerAt < 0) return false;
                int markerLineEnd = IndexOfByte(bytes, markerAt, scan - (markerAt - bom), (byte)'\n');
                if (markerLineEnd < 0) return false;
                int zoneBytes = markerLineEnd + 1 - bom;         // 要守恒的字节数
                int markerLineBytes = markerLineEnd + 1 - markerAt;

                var head = BuildHeaderFor(book, chapters, cumulativeOk, skipped, failed);
                var headBytes = Encoding.UTF8.GetBytes(head);
                int pad = zoneBytes - headBytes.Length - markerLineBytes;   // 把差额全给补白
                if (pad < 0) return false;                       // 放不下就整体放弃，绝不越界

                var block = new MemoryStream(zoneBytes);
                block.Write(headBytes, 0, headBytes.Length);
                var nl = new byte[Math.Max(0, pad)];
                for (int i = 0; i < nl.Length; i++) nl[i] = (byte)'\n';
                block.Write(nl, 0, nl.Length);
                var tail = Encoding.UTF8.GetBytes(HeaderZoneMarker + ReservedHeaderBytes + "\n");
                block.Write(tail, 0, tail.Length);
                if (block.Length != zoneBytes) return false;     // 保险：字节数必须一模一样

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    fs.Position = bom;
                    fs.Write(block.GetBuffer(), 0, (int)block.Length);
                }
                return true;
            }
            catch { return false; }
        }
        /// <summary>在字节数组里从 to 往前找某个字节（用来定位"某一行的行首"）</summary>
        internal static int IndexOfByteBackward(byte[] haystack, int from, int to, byte value)
        {
            if (haystack == null) return -1;
            int lo = Math.Max(0, from);
            int hi = Math.Min(haystack.Length - 1, to);
            for (int i = hi; i >= lo; i--) if (haystack[i] == value) return i;
            return -1;
        }
        internal static int IndexOfByte(byte[] haystack, int from, int len, byte value)
        {
            if (haystack == null) return -1;
            int end = Math.Min(haystack.Length, from + len);
            for (int i = from; i < end; i++) if (haystack[i] == value) return i;
            return -1;
        }
        /// <summary>在字节数组的 [from, from+len) 区间里找一段字节序列（中文不能用字符下标定位）</summary>
        internal static int IndexOfBytes(byte[] haystack, int from, int len, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0) return -1;
            int end = from + len - needle.Length;
            for (int i = from; i <= end; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        private void WriteWithHeader(string body)
        {
            // 整文件重写路径（自测用）：同样带上预留表头区，保证后续 FixHeaderNow 行为一致
            var tmp = OutputFile + ".tmp";
            using (var w = new StreamWriter(tmp, false, HeaderEncoding()))
            {
                w.Write(BuildHeaderZone());
                w.Write(body);
            }
            if (File.Exists(OutputFile)) File.Delete(OutputFile);
            File.Move(tmp, OutputFile);
        }

        /// <summary>
        /// 把开头的表头替换成准确统计，而不必把整本书（可能几 MB）重写一遍。
        /// 表头与正文的分界是那一行 46 个等号（纯 ASCII，所以可以按字节定位，
        /// 不受中文多字节影响）。
        ///
        /// 安全规则（两轮实测 + 离线单测逼出来的）：
        ///  · 新格式文件里有 "#header-zone:N" 标记，说明表头区预留了 N 字节 → 随便改，随便补白；
        ///  · 老格式文件（本工具 1.0 之前下载的、或手工拼的文件）没有标记 →
        ///    只在“新表头不比旧的占字节多”时才原位改写，否则宁可不改表头，
        ///    也绝不覆盖正文（正文比统计数字重要得多）。
        /// </summary>
        internal void FixHeaderNow()
        {
            const int HeaderMax = 16384;
            var bytes = File.ReadAllBytes(OutputFile);
            int bom = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            int scanLen = Math.Min(HeaderMax, bytes.Length - bom);
            if (scanLen <= 0) return;

            // 表头里中英文混排，但等号行、标记行都是 ASCII，按字节找位置是安全的
            var ascii = Encoding.ASCII.GetString(bytes, bom, scanLen);
            int sep = ascii.IndexOf(new string('=', 46), StringComparison.Ordinal);
            if (sep < 0) return;                                  // 没有表头（例如用户自己粘的 txt）→ 不动
            int end = ascii.IndexOf('\n', sep);
            if (end < 0) return;                                  // 表头被截断 → 不动，避免乱写
            int cutBytes = end + 1;

            int zone = 0;
            int mk = ascii.IndexOf(HeaderZoneMarker, StringComparison.Ordinal);
            if (mk > 0)
            {
                int eol = ascii.IndexOf('\n', mk);
                var num = ascii.Substring(mk + HeaderZoneMarker.Length,
                    (eol < 0 ? ascii.Length : eol) - mk - HeaderZoneMarker.Length).Trim();
                int.TryParse(num, out zone);
            }
            bool hasZone = zone >= cutBytes + 256;                // 标记可信（且确实比表头区大）

            var headerBytes = HeaderEncoding().GetBytes(BuildHeader());
            if (!hasZone && headerBytes.Length > cutBytes)
            {
                Log("注意：这是旧格式文件（表头区没有预留空间），新统计比原来长，"
                    + "为保证正文不被覆盖，本次不更新文件头统计。");
                return;
            }

            int pad = (hasZone ? zone : cutBytes) - headerBytes.Length;
            using (var fs = new FileStream(OutputFile, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.Position = bom;
                fs.Write(headerBytes, 0, headerBytes.Length);
                // 补白把表头区填满，正文位置因此永远不变
                if (pad > 0)
                {
                    var padBytes = new byte[pad];
                    for (int i = 0; i < pad; i++) padBytes[i] = (byte)'\n';
                    fs.Write(padBytes, 0, padBytes.Length);
                }
            }
        }

        public string BuildHeader()
        {
            return BuildHeader(Ok, Skipped, Failed);
        }

        /// <summary>按指定统计生成表头（测试和正式流程共用）</summary>
        /// <summary>
        /// 静态版表头构造（增量更新收尾时用：那时只有一个 DownloadRunner 实例，
        /// 但重建表头需要按"累计统计"而不是"本次统计"来写，所以单独抽一个能传统计的入口）。
        /// 与实例版 BuildHeader 共用同一份实现，避免两处写得不一致。
        /// </summary>
        internal static string BuildHeaderFor(BookInfo book, IList<ChapterInfo> chapters, int ok, int skipped, int failed)
        {
            var r = new DownloadRunner { Book = book };
            if (chapters != null) foreach (var c in chapters) r.Chapters.Add(c);
            return r.BuildHeader(ok, skipped, failed);
        }
        public string BuildHeader(int ok, int skipped, int failed)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Book.Title);
            sb.AppendLine("作者：" + (string.IsNullOrEmpty(Book.Author) ? "未知" : Book.Author));
            sb.AppendLine("来源：" + (Book.Site == "fanqie" ? "番茄小说" : "笔趣阁") + "　" + Book.Url);
            if (!string.IsNullOrEmpty(Book.Desc))
            {
                var desc = Book.Desc.Replace("\n", " ");
                if (desc.Length > 300) desc = desc.Substring(0, 300) + "…";
                sb.AppendLine("简介：" + desc);
            }
            // 统计行里带上进度标记（累计章数 + 章节 id 指纹），供「更新已下载的书」判断哪些是新的
            sb.AppendLine(BuildStatisticsLine(ok, skipped, failed, Chapters));
            // 缺章时在表头补一句（只加一行、体积可控，正文不会被打乱；明细在“缺失章节.txt”）
            if (skipped + failed > 0)
                sb.AppendLine(string.Format("缺失 {0} 章（跳过 {1} + 失败 {2}），明细见同名「.缺失章节.txt」",
                    skipped + failed, skipped, failed));
            sb.AppendLine("下载时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(new string('=', 46));
            return sb.ToString();
        }


        /// <summary>
        /// 统计行文本（含进度标记、补齐到固定字节数）。表头初次生成、收尾更新、增量更新
        /// 三处都用它，保证格式唯一 —— 否则标记写法一变，老文件就读不出进度了。
        /// 读取端（TryReadProgress）按"标记 → 行尾"取值，末尾补的空格会被 Trim 掉。
        /// </summary>
        internal static string BuildStatisticsLine(int ok, int skipped, int failed, IList<ChapterInfo> chapters)
        {
            string fp = Fingerprint(IdsOf(chapters, ok));
            var line = string.Format("本次下载：成功 {0} 章，跳过 {1} 章，失败 {2} 章  {3}{0}:{4}",
                ok, skipped, failed, ProgressOkMarker, fp);
            // 不做定长补齐：表头区里「统计行 + 补白」的整体长度是守恒的，
            // 收尾注入时会把这段区域重新填满到 #header-zone 标记之前，
            // 所以统计行长短变化不会让标记行/正文起点漂移（见 InjectHeaderStatistics）。
            return line;
        }

        /// <summary>前 n 章的 id 拼起来（进度指纹用）</summary>
        private static string IdsOf(IList<ChapterInfo> chapters, int n)
        {
            if (chapters == null || n <= 0) return "";
            var sb = new StringBuilder();
            int end = Math.Min(n, chapters.Count);
            for (int i = 0; i < end; i++)
            {
                var c = chapters[i];
                if (c == null) continue;
                sb.Append(c.Id).Append('|');
            }
            return sb.ToString();
        }
    }

    /// <summary>正文清洗：去掉站点插进正文的广告/导航行（注意别误杀正文）</summary>
    public static class TextCleaner
    {
        /// <summary>强标记：整行几乎不可能是正文（站点固定广告语）</summary>
        private static readonly string[] StrongMarkers = new[]
        {
            "送你一个现金红包", "请关闭浏览器阅读模式", "本站所有内容来源于互联网",
            "最新网址", "请记住本站", "一秒记住",
        };

        /// <summary>弱标记：正文里也可能出现（比如“求推荐票”），只有整行很短才当广告</summary>
        private static readonly string[] WeakMarkers = new[]
        {
            "推荐票", "月票", "加入书签", "手机版", "笔趣阁",
        };

        /// <summary>短行判定阈值：弱标记行超过这个长度就认为是正文</summary>
        private const int WeakMarkerMaxLen = 24;

        /// <summary>
        /// 移动版会把广告**直接拼在正文句子末尾**（不是独立行），例如：
        ///   “……他心中纳闷。还在为找不到的最新章节苦恼？安利一个公众号：rd444?…”
        /// 所以要先按标记把行尾那一段切掉，否则整行过滤会连正文一起丢掉。
        /// </summary>
        private static readonly string[] TrailingAdMarkers = new[]
        {
            "还在为找不到", "安利一个公众号", "帮你找书", "陪你尬聊", "不然搜不到哦",
            "关注公众号", "全网免费", "最新章节请", "请记住本书首发",
            "真人小姐姐在线服务", "小姐姐在线服务",
        };

        public static string CleanBody(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (var raw in s.Split('\n'))
            {
                var t = StripPageMark(StripTrailingAd(raw.Trim()));
                if (t.Length == 0) continue;
                if (IsAdLine(t)) continue;
                sb.Append(t).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>切掉行尾拼接的广告（广告一定出现在正文之后）</summary>
        public static string StripTrailingAd(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;
            var t = line;
            foreach (var m in TrailingAdMarkers)
            {
                int i = t.IndexOf(m, StringComparison.Ordinal);
                if (i >= 0)
                {
                    // 广告出现在行首 → 整行是广告；出现在中间/行尾 → 切成正文
                    t = i > 0 ? t.Substring(0, i) : "";
                    // 只收掉广告前那些“没写完”的标点；句号/感叹号/问号是句子正常结尾，必须留下
                    t = t.TrimEnd(' ', '\u3000', '，', ',', '；', ';', '、', '：', ':');
                    if (t.Length == 0) return "";
                }
            }
            return t;
        }

        /// <summary>
        /// 站点自己的页码残留，形如 --1120dmabgioie1777198--&gt;
        /// （移动版偶尔会在正文里插这种标记，必须清掉）
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex PageMark =
            new System.Text.RegularExpressions.Regex(@"--\d{2,6}[a-z]{6,16}\d{4,12}-->",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public static string StripPageMark(string line)
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf("--", StringComparison.Ordinal) < 0) return line;
            var t = PageMark.Replace(line, "");
            // 整行只有这个标记 → 丢弃
            if (t.Trim().Length == 0) return "";
            return t;
        }

        public static bool IsAdLine(string t)
        {
            if (string.IsNullOrEmpty(t)) return true;
            foreach (var m in StrongMarkers)
                if (t.Contains(m)) return true;
            if (t.Length <= WeakMarkerMaxLen)
            {
                foreach (var m in WeakMarkers)
                    if (t.Contains(m)) return true;
            }
            return false;
        }
    }
}
