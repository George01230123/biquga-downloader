using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TomatoBiquga
{
    /// <summary>
    /// 本地书架：记住"下载过哪些书"，用来做一键追更。
    ///
    /// 为什么需要它：项目里本来就有「更新新章节」，但**前提是当前已经载入了那本书的目录** ——
    /// 想追更就得重新搜书名、再等一次目录。而追更恰恰是高频操作（每天/每周一次），
    /// 每次都重走搜索流程非常别扭。书架把 (站点, 书籍标识, 标题) 存下来，
    /// 让"打开工具 → 点更新"成为可能。
    ///
    /// 存储形式：exe 旁边的 `书架.json`，UTF-8 带 BOM，纯文本可手改。
    /// 为什么不复用 fanqie-core/logs/download_history.jsonl：那是第三方番茄核心写的，
    /// 格式不由本项目控制，而且只覆盖番茄一个站点。
    ///
    /// 失败处理：文件损坏 → 当成空书架（绝不因此起不来）；写失败 → 返回 false 由调用方提示。
    /// </summary>
    public class Bookshelf
    {
        public const string FileName = "书架.json";

        /// <summary>一条书架记录</summary>
        public class Entry
        {
            /// <summary>站点 key：biquga-m / biquga / fanqie</summary>
            public string Site = "";
            /// <summary>站点内的书籍标识：biquga 是 dir（/10_10333），番茄是 bookId</summary>
            public string Key = "";
            public string Title = "";
            public string Author = "";
            /// <summary>详情页地址（重新载入目录时用）</summary>
            public string Url = "";
            /// <summary>上次下载时目录里有多少章（用来判断"有没有新章"）</summary>
            public int LastChapterCount = 0;
            /// <summary>最后一次下载时间</summary>
            public DateTime LastDownload = DateTime.MinValue;
            /// <summary>已落盘的正文文件路径</summary>
            public string FilePath = "";

            /// <summary>主键：站点 + 书籍标识（同一本书在不同站点算两条）</summary>
            public string Id { get { return (Site ?? "") + "|" + (Key ?? ""); } }

            public override string ToString()
            {
                return string.Format("《{0}》 {1}  [{2}]  {3} 章  {4}",
                    Title, string.IsNullOrEmpty(Author) ? "未知" : Author, Site, LastChapterCount,
                    LastDownload == DateTime.MinValue ? "从未下载" : LastDownload.ToString("yyyy-MM-dd HH:mm"));
            }
        }

        public readonly List<Entry> Entries = new List<Entry>();

        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        /// <summary>从书对象里取出"站点内的书籍标识"</summary>
        public static string KeyOf(BookInfo book)
        {
            if (book == null) return "";
            if (!string.IsNullOrEmpty(book.Dir)) return book.Dir;
            if (!string.IsNullOrEmpty(book.BookId)) return book.BookId;
            return book.Url ?? "";
        }

        /// <summary>
        /// 新增或更新一条记录（按 站点+标识 去重）。
        /// 已存在时**保留原有的 LastDownload**，除非传了 downloaded=true。
        /// </summary>
        public Entry Touch(BookInfo book, string filePath, int chapterCount, bool downloaded)
        {
            if (book == null || string.IsNullOrEmpty(book.Title)) return null;

            var site = book.Site ?? "";
            var key = KeyOf(book);
            var e = Find(site, key);
            if (e == null)
            {
                e = new Entry { Site = site, Key = key };
                Entries.Add(e);
            }
            e.Title = book.Title;
            e.Author = book.Author ?? "";
            if (!string.IsNullOrEmpty(book.Url)) e.Url = book.Url;
            if (chapterCount > 0) e.LastChapterCount = chapterCount;
            if (!string.IsNullOrEmpty(filePath)) e.FilePath = filePath;
            if (downloaded) e.LastDownload = DateTime.Now;
            return e;
        }

        public Entry Find(string site, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (var e in Entries)
            {
                if (string.Equals(e.Site, site ?? "", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        public bool Remove(string site, string key)
        {
            var e = Find(site, key);
            return e != null && Entries.Remove(e);
        }

        /// <summary>最近下载的排在前面；从没下载过的排最后（按标题）。</summary>
        public List<Entry> Sorted()
        {
            var list = new List<Entry>(Entries);
            list.Sort(delegate (Entry a, Entry b)
            {
                int c = b.LastDownload.CompareTo(a.LastDownload);
                if (c != 0) return c;
                return string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
            });
            return list;
        }

        // ---------------------------------------------------------- 读写

        public static Bookshelf Load()
        {
            return Load(DefaultPath);
        }

        /// <summary>读取书架。文件不存在或损坏 → 返回空书架（不抛异常）。</summary>
        public static Bookshelf Load(string path)
        {
            var shelf = new Bookshelf();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return shelf;
                var text = File.ReadAllText(path, Encoding.UTF8);
                Parse(text, shelf);
            }
            catch { /* 书架坏了不该影响启动，当成空的 */ }
            return shelf;
        }

        public bool Save()
        {
            return Save(DefaultPath);
        }

        public bool Save(string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\n  \"version\": 1,\n  \"books\": [\n");
                for (int i = 0; i < Entries.Count; i++)
                {
                    var e = Entries[i];
                    sb.Append("    {");
                    sb.Append("\"site\": ").Append(J(e.Site)).Append(", ");
                    sb.Append("\"key\": ").Append(J(e.Key)).Append(", ");
                    sb.Append("\"title\": ").Append(J(e.Title)).Append(", ");
                    sb.Append("\"author\": ").Append(J(e.Author)).Append(", ");
                    sb.Append("\"url\": ").Append(J(e.Url)).Append(", ");
                    sb.Append("\"chapters\": ").Append(e.LastChapterCount).Append(", ");
                    sb.Append("\"downloaded\": ").Append(J(e.LastDownload == DateTime.MinValue
                        ? "" : e.LastDownload.ToString("yyyy-MM-dd HH:mm:ss"))).Append(", ");
                    sb.Append("\"file\": ").Append(J(e.FilePath));
                    sb.Append("}");
                    if (i < Entries.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append("  ]\n}\n");

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------- 极简 JSON

        /// <summary>
        /// 手写解析而不是引 Json.NET：这个项目坚持零第三方依赖（见 README 的"零依赖"一节），
        /// 而书架的结构是**本项目自己写出去的**，格式完全可控，用不着通用解析器。
        /// 解析原则：认不出的字段一律跳过，绝不因为多了个字段就整份丢掉。
        /// </summary>
        internal static void Parse(string text, Bookshelf shelf)
        {
            if (string.IsNullOrEmpty(text)) return;

            int booksAt = text.IndexOf("\"books\"", StringComparison.Ordinal);
            if (booksAt < 0) return;
            int arrStart = text.IndexOf('[', booksAt);
            if (arrStart < 0) return;

            int depth = 0;
            int objStart = -1;
            for (int i = arrStart; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    // 跳过整个字符串，里面的 [ ] { } 不算结构
                    i = SkipString(text, i) - 1;
                    continue;
                }
                if (c == '{')
                {
                    if (depth == 0) objStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        var e = ParseEntry(text.Substring(objStart, i - objStart + 1));
                        if (e != null && !string.IsNullOrEmpty(e.Title)) shelf.Entries.Add(e);
                        objStart = -1;
                    }
                }
                else if (c == ']' && depth == 0)
                {
                    break;   // books 数组结束
                }
            }
        }

        /// <summary>返回字符串结束引号之后的下标</summary>
        private static int SkipString(string s, int start)
        {
            int i = start + 1;
            while (i < s.Length)
            {
                if (s[i] == '\\') { i += 2; continue; }
                if (s[i] == '"') return i + 1;
                i++;
            }
            return s.Length;
        }

        private static Entry ParseEntry(string obj)
        {
            var e = new Entry();
            e.Site = Str(obj, "site");
            e.Key = Str(obj, "key");
            e.Title = Str(obj, "title");
            e.Author = Str(obj, "author");
            e.Url = Str(obj, "url");
            e.FilePath = Str(obj, "file");

            // chapters 是**裸数字**（"chapters": 1067），不能走 Str() ——
            // Str() 只匹配带引号的值，用它读数字会永远得到 0。
            // 这个 bug 被离线单测当场抓到过，别再改回去。
            e.LastChapterCount = Num(obj, "chapters");

            DateTime dt;
            var ds = Str(obj, "downloaded");
            if (!string.IsNullOrEmpty(ds) &&
                DateTime.TryParseExact(ds, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dt))
                e.LastDownload = dt;

            return e;
        }

        private static string Str(string obj, string key)
        {
            var m = Regex.Match(obj, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return Unescape(m.Groups[1].Value);
        }

        /// <summary>
        /// 读一个**裸数字**字段（"chapters": 1067）。
        /// 必须是独立方法：Str() 要求值带引号，数字会被它漏掉。
        /// 也顺手容忍写成字符串的情况（手改 JSON 的人可能加上引号）。
        /// </summary>
        private static int Num(string obj, string key)
        {
            var m = Regex.Match(obj, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            if (m.Success)
            {
                int n;
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    return n;
                return 0;
            }
            // 值被写成字符串："chapters": "1067"
            int s;
            if (int.TryParse(Str(obj, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out s)) return s;
            return 0;
        }

        internal static string J(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        internal static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                i++;
                switch (s[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                                break;
                            }
                        }
                        sb.Append("\\u");
                        break;
                    default: sb.Append(s[i]); break;
                }
            }
            return sb.ToString();
        }
    }
}
