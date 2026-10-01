using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// Markdown 导出。
    ///
    /// 为什么要它（同类工具的做法）：Markdown 是"万能中转格式" ——
    /// 想要 EPUB 有 pandoc、想要 PDF 有 Typora、想要网页直接丢进静态站点生成器；
    /// 而且它**用记事本就能看**、能进 git 做版本对比。FictionDown 就是拿 md 当主力输出。
    ///
    /// 和 TXT 的分工：
    ///   TXT      —— 保底，任何设备都能读（默认）
    ///   Markdown —— 需要目录层级、加粗/引用、或者要转成别的格式时用
    /// </summary>
    public static class MarkdownWriter
    {
        /// <summary>
        /// 写一本 Markdown。chapters 里正文为空的章节会被跳过（未下载的章）。
        /// includeToc=true 时在开头生成带锚点链接的目录。
        /// </summary>
        public static void Write(string path, BookInfo book, IList<ChapterInfo> chapters, bool includeToc)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path 不能为空");
            if (book == null) throw new ArgumentException("book 不能为空");

            var items = new List<ChapterInfo>();
            foreach (var c in chapters)
            {
                if (c == null || c.IsVolume) continue;
                if (string.IsNullOrEmpty(c.Text)) continue;
                items.Add(c);
            }
            if (items.Count == 0) throw new Exception("这本书还没有任何已下载的正文，先下载再导出 Markdown。");

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            // YAML front matter：pandoc / 静态站点生成器都认，顺手把元信息带上
            sb.Append("---\n");
            sb.Append("title: ").Append(Yaml(book.Title)).Append('\n');
            sb.Append("author: ").Append(Yaml(string.IsNullOrEmpty(book.Author) ? "未知" : book.Author)).Append('\n');
            if (!string.IsNullOrEmpty(book.Url)) sb.Append("source: ").Append(Yaml(book.Url)).Append('\n');
            sb.Append("exported: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            sb.Append("chapters: ").Append(items.Count).Append('\n');
            sb.Append("generator: novel-downloader\n");
            sb.Append("---\n\n");

            sb.Append("# ").Append(book.Title).Append("\n\n");
            if (!string.IsNullOrEmpty(book.Author))
                sb.Append("作者：").Append(book.Author).Append("\n\n");
            if (!string.IsNullOrEmpty(book.Desc))
                sb.Append("> ").Append(OneLine(book.Desc, 300)).Append("\n\n");
            if (!string.IsNullOrEmpty(book.Url))
                sb.Append("来源：<").Append(book.Url).Append(">\n\n");

            if (includeToc)
            {
                sb.Append("## 目录\n\n");
                for (int i = 0; i < items.Count; i++)
                    sb.Append("- [").Append(Escape(ChapterTitle(items[i]))).Append("](#")
                      .Append(AnchorId(i + 1)).Append(")\n");
                sb.Append('\n');
            }

            sb.Append("---\n");
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                sb.Append('\n');
                // 显式 html 锚点：GitHub / Typora / VS Code 都认，保证目录链接点得动
                sb.Append("<a id=\"").Append(AnchorId(i + 1)).Append("\"></a>\n\n");
                sb.Append("## ").Append(Escape(ChapterTitle(c))).Append("\n\n");
                foreach (var raw in (c.Text ?? "").Split('\n'))
                {
                    var line = raw.TrimEnd('\r').Trim();
                    if (line.Length == 0) continue;
                    // 正文里的 markdown 语法字符要转义，否则章节能把文档结构搞乱
                    sb.Append(EscapeLine(line)).Append("\n\n");
                }
            }

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static string ChapterTitle(ChapterInfo c)
        {
            if (c == null) return "";
            var t = c.Title;
            if (string.IsNullOrEmpty(t)) return "（无标题）";
            return t.Trim();
        }

        internal static string AnchorId(int n)
        {
            return "ch" + n.ToString("D4");
        }

        /// <summary>标题里的 markdown 语法字符转义（# * _ ` [ ] 等）</summary>
        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var ch in s)
            {
                if ("\\`*_{}[]()#+-.!|<>".IndexOf(ch) >= 0) sb.Append('\\');
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>正文行：只转义会破坏段落结构的开头字符，保持可读性</summary>
        internal static string EscapeLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";
            var t = line.TrimStart();
            // 行首的 # - * > 数字. 会被当成标题/列表/引用，加反斜杠
            if (t.Length > 0 && "#-*>".IndexOf(t[0]) >= 0) return "\\" + line;
            if (t.Length > 1 && char.IsDigit(t[0]) && (t[1] == '.' || t[1] == '、')) return "\\" + line;
            return line;
        }

        /// <summary>YAML 值：引号内的引号要转义，换行要压平</summary>
        internal static string Yaml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
            return "\"" + s + "\"";
        }

        private static string OneLine(string s, int max)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
