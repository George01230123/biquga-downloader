using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// txt 正文里某一章的位置。
    ///
    /// 为什么需要它：**缺章补齐**和**章节级断点续传**都要求"能在文件里定位到具体的某一章"，
    /// 光靠"标题字符串搜索"不可靠（同名标题、标题被站点改过、正文里恰好出现同样的字）。
    /// 所以每章开头写一个机器可读的锚点，见 <see cref="AnchorOf"/>。
    /// </summary>
    public class ChapterSpan
    {
        public string Id;          // 章节 id（biquga 的 cid / 番茄的 itemId）
        public string Title;
        public long Start;         // 锚点行的起始字节偏移
        public long End;           // 本章结束（下一章锚点之前，或文件末尾）
        public bool HasBody;       // 这一段里有没有正文（只有标题没正文 = 抓到空内容）
        public int BodyBytes;      // 正文大致字节数（用于判断"这一章是不是空的"）
    }

    /// <summary>
    /// 章节在 txt 里的定位与就地改写。
    ///
    /// 文件格式（v1.0.5 起每一章前面多一行锚点）：
    /// <code>
    ///   &lt;!--c:44302617--&gt;
    ///   第一章 天黑别出门
    ///   ---------
    ///
    ///   正文…
    /// </code>
    /// 锚点是一行 HTML 注释：阅读器/编辑器不会显示它，正则清洗不会误删，
    /// 但它让程序**按 id 精确定位**任意一章 —— 这是"补齐"和"续传"的地基。
    /// 旧文件（没有锚点）仍然能读，只是补齐/续传会退化（见 HasAnchors）。
    /// </summary>
    public static class ChapterIndex
    {
        public const string AnchorPrefix = "<!--c:";
        public const string AnchorSuffix = "-->";

        /// <summary>生成一章的锚点行（带换行）</summary>
        public static string AnchorOf(string id)
        {
            return AnchorPrefix + (id ?? "") + AnchorSuffix + "\n";
        }

        /// <summary>
        /// 扫描文件里的所有章节锚点，返回按出现顺序排列的位置表。
        /// 用字节扫描（不用 ReadAllText）：中文的字符下标和字节偏移不是一回事，
        /// 而后面要做的是**按字节切片替换**，必须从一开始就在字节坐标系里。
        /// </summary>
        public static List<ChapterSpan> Scan(string path)
        {
            var list = new List<ChapterSpan>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return list;
            try
            {
                var bytes = File.ReadAllBytes(path);
                return ScanBytes(bytes);
            }
            catch { return list; }
        }

        /// <summary>字节级扫描（测试可以直接喂字节）</summary>
        public static List<ChapterSpan> ScanBytes(byte[] bytes)
        {
            var list = new List<ChapterSpan>();
            if (bytes == null || bytes.Length == 0) return list;

            var prefix = Encoding.ASCII.GetBytes(AnchorPrefix);
            var suffix = Encoding.ASCII.GetBytes(AnchorSuffix);

            int i = 0;
            while (i < bytes.Length)
            {
                int at = DownloadRunner.IndexOfBytes(bytes, i, bytes.Length - i, prefix);
                if (at < 0) break;
                int idStart = at + prefix.Length;
                int idEnd = DownloadRunner.IndexOfBytes(bytes, idStart, bytes.Length - idStart, suffix);
                if (idEnd < 0) break;

                string id = Encoding.ASCII.GetString(bytes, idStart, idEnd - idStart).Trim();
                // 锚点之后紧跟标题行（下一行），再往后是破折号行与正文
                int lineEnd = DownloadRunner.IndexOfByte(bytes, idEnd, bytes.Length - idEnd, (byte)'\n');
                long start = at;
                long contentStart = (lineEnd < 0) ? bytes.Length : lineEnd + 1;

                var span = new ChapterSpan { Id = id, Start = start };
                if (list.Count > 0) list[list.Count - 1].End = start;   // 上一章到此为止
                span.End = bytes.Length;                                 // 先假定到文件末尾
                span.Title = ReadFirstLine(bytes, contentStart);
                list.Add(span);
                i = idEnd + suffix.Length;
            }

            // 判定每一段里有没有正文（去掉标题行、破折号行、空行后还剩多少内容）
            for (int k = 0; k < list.Count; k++)
            {
                var s = list[k];
                long from = s.Start;
                long to = s.End;
                // 跳过锚点行与标题行
                long p = SkipLines(bytes, from, to, 2);
                int content = 0;
                for (long x = p; x < to; x++)
                {
                    byte b = bytes[x];
                    if (b == (byte)'\n' || b == (byte)'\r' || b == (byte)' ') continue;
                    if (b == (byte)'-') continue;      // 破折号分隔线
                    content++;
                }
                s.BodyBytes = content;
                s.HasBody = content >= 40;             // 和"空章节"的门槛保持一致
            }
            return list;
        }

        /// <summary>从 from 起跳过 n 行，返回下一行行首</summary>
        private static long SkipLines(byte[] bytes, long from, long to, int n)
        {
            long p = from;
            for (int i = 0; i < n && p < to; i++)
            {
                int nl = DownloadRunner.IndexOfByte(bytes, (int)p, (int)(to - p), (byte)'\n');
                if (nl < 0) return to;
                p = nl + 1;
            }
            return p;
        }

        private static string ReadFirstLine(byte[] bytes, long from)
        {
            try
            {
                int nl = DownloadRunner.IndexOfByte(bytes, (int)from, (int)(bytes.Length - from), (byte)'\n');
                if (nl < 0) nl = bytes.Length;
                var s = Encoding.UTF8.GetString(bytes, (int)from, nl - (int)from).Trim('\r', ' ', '\t');
                return s;
            }
            catch { return ""; }
        }

        /// <summary>文件里有没有章节锚点（旧文件没有 → 补齐/续传要退化处理）</summary>
        public static bool HasAnchors(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var bytes = File.ReadAllBytes(path);
                return DownloadRunner.IndexOfBytes(bytes, 0, Math.Min(bytes.Length, 65536),
                    Encoding.ASCII.GetBytes(AnchorPrefix)) >= 0;
            }
            catch { return false; }
        }

        /// <summary>把位置表转成 id → span 的字典（同一 id 只保留第一个）</summary>
        public static Dictionary<string, ChapterSpan> ById(List<ChapterSpan> spans)
        {
            var d = new Dictionary<string, ChapterSpan>();
            if (spans == null) return d;
            foreach (var s in spans)
                if (!string.IsNullOrEmpty(s.Id) && !d.ContainsKey(s.Id)) d[s.Id] = s;
            return d;
        }

        /// <summary>这一章在正文里的完整文本块（含锚点行），用于"补进去"</summary>
        public static string BuildChapterBlock(string id, string title, string body)
        {
            var t = string.IsNullOrEmpty(title) ? "（无标题）" : title.Trim();
            var sb = new StringBuilder();
            sb.Append('\n').Append('\n');
            sb.Append(AnchorOf(id));
            sb.Append(t).Append('\n');
            sb.Append(new string('-', Math.Min(24, Math.Max(6, t.Length)))).Append('\n').Append('\n');
            if (!string.IsNullOrEmpty(body)) sb.Append(TextCleaner.CleanBody(body)).Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// 把一章**插入到正确位置**（按目录顺序），返回是否成功。
        ///
        /// 定位规则：找目录里排在它后面、且文件里已经存在的第一项，插到那一项之前；
        /// 若后面都没有，就追加到文件末尾。
        /// </summary>
        public static bool InsertChapter(string filePath, BookInfo book, ChapterInfo chapter, string body)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
            if (chapter == null || string.IsNullOrEmpty(chapter.Id)) return false;

            try
            {
                var spans = Scan(filePath);
                if (spans.Count == 0) return false;                    // 没有锚点 → 不支持按章插入
                var byId = ById(spans);

                // 这一章已经在了？先在原位替换（补齐时更常见的是"原来没有"）
                long at;
                if (byId.ContainsKey(chapter.Id))
                {
                    var s = byId[chapter.Id];
                    at = s.Start;
                    long end = s.End;
                    return Splice(filePath, at, end, BuildChapterBlock(chapter.Id, chapter.Title, body));
                }

                // 找"目录里排在它后面、且文件里已存在"的第一章
                int myOrder = OrderOf(book, chapter);
                ChapterSpan next = null;
                if (myOrder >= 0 && book != null && book.Chapters != null)
                {
                    for (int i = myOrder + 1; i < book.Chapters.Count; i++)
                    {
                        var c = book.Chapters[i];
                        if (c == null || c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
                        ChapterSpan s;
                        if (byId.TryGetValue(c.Id, out s)) { next = s; break; }
                    }
                }
                at = (next != null) ? next.Start : new FileInfo(filePath).Length;
                return Splice(filePath, at, at, BuildChapterBlock(chapter.Id, chapter.Title, body));
            }
            catch { return false; }
        }

        private static int OrderOf(BookInfo book, ChapterInfo chapter)
        {
            if (book == null || book.Chapters == null) return -1;
            for (int i = 0; i < book.Chapters.Count; i++)
            {
                var c = book.Chapters[i];
                if (c == null) continue;
                if (ReferenceEquals(c, chapter)) return i;
                if (!string.IsNullOrEmpty(c.Id) && c.Id == chapter.Id) return i;
            }
            return chapter.Order > 0 ? chapter.Order - 1 : -1;
        }

        /// <summary>把 [from,to) 这段字节换成 newText（from==to 就是插入），返回是否成功</summary>
        public static bool Splice(string filePath, long from, long to, string newText)
        {
            try
            {
                var old = File.ReadAllBytes(filePath);
                if (from < 0 || to < from || to > old.Length) return false;
                var ins = Encoding.UTF8.GetBytes(newText ?? "");
                var tmp = filePath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(old, 0, (int)from);
                    fs.Write(ins, 0, ins.Length);
                    fs.Write(old, (int)to, (int)(old.Length - to));
                }
                if (File.Exists(filePath)) File.Delete(filePath);
                File.Move(tmp, filePath);
                return true;
            }
            catch { return false; }
        }
    }
}
