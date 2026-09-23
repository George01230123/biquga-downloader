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
            int total = Chapters.Count;

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
            FixHeaderNow();
            WriteMissingReport();
            if (Failed > 0 || Skipped > 0) LogMissingSummary();

            var fi = new FileInfo(OutputFile);
            Log(string.Format("文件已生成：{0}（{1:N0} 字节）", OutputFile, fi.Length));
            if (FromCacheCount > 0)
                Log(string.Format("其中 {0}/{1} 章直接复用了目录遍历时已抓到的正文（未重复请求站点）", FromCacheCount, Ok));
            Log(string.Format("本次下载：成功 {0} 章，跳过 {1} 章，失败 {2} 章", Ok, Skipped, Failed));
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
                    _pending.Append("\n\n").Append(c.Title).Append('\n')
                        .Append(new string('-', Math.Min(24, Math.Max(6, c.Title.Length)))).Append("\n\n")
                        .Append(TextCleaner.CleanBody(text)).Append('\n');
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

        /// <summary>表头 + 补白 + 表头区标记（正文从这之后才开始，所以正文永远不会被表头挤到）</summary>
        internal string BuildHeaderZone()
        {
            var head = BuildHeader();
            int headBytes = Encoding.UTF8.GetByteCount(head);
            int pad = ReservedHeaderBytes - headBytes;
            if (pad < 0) pad = 0;
            return head + new string('\n', pad) + HeaderZoneMarker + ReservedHeaderBytes + "\n";
        }

        /// <summary>
        /// 增量落盘：第一次写表头 + **预留表头区**（带 UTF-8 BOM），之后只把新增的正文 append 上去。
        /// 预留表头区是为了让收尾时的“原位改表头”永远有地方写：
        /// 否则统计行一长（例如多出“缺失 N 章”那一行），新表头占的字节比旧的多，
        /// 就会盖掉正文开头几个字 —— 这是离线单测真实抓出来的 bug。
        /// </summary>
        private void FlushIncremental()
        {
            if (_pending.Length == 0 && _headerWritten) return;
            if (!_headerWritten)
            {
                using (var w = new StreamWriter(OutputFile, false, new UTF8Encoding(true)))
                    w.Write(BuildHeaderZone());
                _headerWritten = true;
            }
            if (_pending.Length > 0)
            {
                using (var w = new StreamWriter(OutputFile, true, new UTF8Encoding(false)))
                    w.Write(_pending.ToString());
                _pending.Length = 0;
            }
        }

        private void WriteWithHeader(string body)
        {
            // 整文件重写路径（自测用）：同样带上预留表头区，保证后续 FixHeaderNow 行为一致
            var tmp = OutputFile + ".tmp";
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(true)))
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

            var headerBytes = Encoding.UTF8.GetBytes(BuildHeader());
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
            sb.AppendLine(string.Format("本次下载：成功 {0} 章，跳过 {1} 章，失败 {2} 章", ok, skipped, failed));
            // 缺章时在表头补一句（只加一行、体积可控，正文不会被打乱；明细在“缺失章节.txt”）
            if (skipped + failed > 0)
                sb.AppendLine(string.Format("缺失 {0} 章（跳过 {1} + 失败 {2}），明细见同名「.缺失章节.txt」",
                    skipped + failed, skipped, failed));
            sb.AppendLine("下载时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(new string('=', 46));
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
