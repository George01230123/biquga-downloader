using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 错字检测：**双源比对**。
    ///
    /// 思路（FictionDown 验证过有效的做法）：同一本书从**两个不同的源**各取一份正文，
    /// 逐字对比。两个源**同时错成同一个字**的概率极低，所以差异点就是"至少有一边错"的位置，
    /// 把两边的写法都摆出来，人一眼就能判断哪个对。
    ///
    /// 为什么不自己做"字形相近"的启发式：那种方法只能标出"可疑"，
    /// 无法回答"到底哪个字对"，而且中文形近字表本身就有大量误报。
    /// 双源比对给出的是**带对照的确定差异**，可判断、可核对。
    ///
    /// 为什么不用成熟的 diff 库：这个项目坚持零第三方依赖（EPUB 是手写 zip、
    /// JSON 是手写解析），而这里需要的只是"两个字符串的最短编辑脚本"，
    /// 用经典 LCS 动态规划自己写几十行就够 —— 章节正文只有几 KB。
    ///
    /// 已知的局限（如实写在界面上，不能让用户以为这是万能的）：
    ///   · 两个源都错同一个字 → 检测不出来（只能靠第三个源或人工）
    ///   · 一个是"错字"、另一个是"漏字/多字" → 会报成差异，但提示的是"字数不一致"
    ///   · 站点自己插入的广告/水印 → 会报成差异。所以比对前先跑同一套 TextCleaner，
    ///     并且把长度差异过大的章节标为"疑似站点差异"而不是"错字"
    /// </summary>
    public static class TypoFinder
    {
        /// <summary>一处差异</summary>
        public class Diff
        {
            /// <summary>章节标题（用主源那边的）</summary>
            public string Chapter = "";
            /// <summary>在主源正文里的字符下标（0 起）</summary>
            public int Position;
            /// <summary>主源写法（可能为空 = 主源这里少了内容）</summary>
            public string Primary = "";
            /// <summary>对照源写法（可能为空 = 对照源这里少了内容）</summary>
            public string Other = "";
            /// <summary>主源这边的前后文（便于定位）</summary>
            public string Context = "";

            /// <summary>差异类型</summary>
            public DiffKind Kind = DiffKind.Replace;

            /// <summary>
            /// AI 裁决结果（没跑 AI 就是 None）。由 AiAdjudicator 填。
            /// 放在 Diff 上而不是另开一张表：报告、CSV、界面都要按差异逐条展示，
            /// 分开存会导致三处各自做一次下标对齐，容易错位。
            /// </summary>
            public AiAdjudicator.VerdictResult Ai;

            /// <summary>AI 裁决的短标签（给 CSV / 列表用）</summary>
            public string AiText()
            {
                if (Ai == null || Ai.Verdict == AiAdjudicator.Verdict.None) return "";
                switch (Ai.Verdict)
                {
                    case AiAdjudicator.Verdict.PrimaryRight: return "主源正确";
                    case AiAdjudicator.Verdict.OtherRight: return "主源错";
                    case AiAdjudicator.Verdict.BothWrong:
                        return Ai.Suggestion.Length > 0 ? "都不对→" + Ai.Suggestion : "都不对";
                    default: return "判断不了";
                }
            }

            public override string ToString()
            {
                return string.Format("{0} @{1}  {2} → {3}", Chapter, Position,
                    Primary.Length == 0 ? "(无)" : Primary, Other.Length == 0 ? "(无)" : Other);
            }
        }

        public enum DiffKind
        {
            /// <summary>两边字数一致、有字不同 —— 最可能就是错字</summary>
            Replace,
            /// <summary>主源多了内容（可能是多字，也可能是站点广告没洗净）</summary>
            ExtraInPrimary,
            /// <summary>主源少了内容（漏字）</summary>
            MissingInPrimary,
        }

        /// <summary>一次比对的结果</summary>
        public class Result
        {
            public string Title = "";
            public int ComparedChapters;
            public int SkippedChapters;              // 对照源没有这一章 / 抓取失败
            public int IdenticalChapters;
            public readonly List<Diff> Diffs = new List<Diff>();
            /// <summary>对照源的名字（写进报告）</summary>
            public string OtherSource = "";

            /// <summary>差异最多的前 N 章（给报告用）</summary>
            public Dictionary<string, int> PerChapter = new Dictionary<string, int>();

            public string Summary()
            {
                return string.Format("比对 {0} 章：完全一致 {1} 章，有差异 {2} 章，跳过 {3} 章，共 {4} 处差异",
                    ComparedChapters, IdenticalChapters, ComparedChapters - IdenticalChapters,
                    SkippedChapters, Diffs.Count);
            }
        }

        /// <summary>单章允许的最大差异数（超过就当成"站点差异"，避免广告把报告刷爆）</summary>
        public const int MaxDiffsPerChapter = 200;

        /// <summary>
        /// 比对两段正文。
        /// 返回的 Diff.Position 是**主源**里的下标，方便用户在自己的 txt 里定位。
        /// </summary>
        public static List<Diff> CompareChapter(string title, string primary, string other)
        {
            var list = new List<Diff>();
            primary = (primary ?? "").Replace("\r\n", "\n");
            other = (other ?? "").Replace("\r\n", "\n");

            // 完全一致：最常见的情况，走快路径
            if (string.Equals(primary, other, StringComparison.Ordinal)) return list;

            // 把正文压成"只含有效字符"的序列并记录原始下标 ——
            // 两个源的换行/空行处理常常不同，直接逐字比会满屏假差异。
            var a = Normalize(primary);
            var b = Normalize(other);
            if (a.Text.Length == 0 || b.Text.Length == 0) return list;

            // 长度差异过大：多半是站点差异（一边有广告/缺章），不当成错字逐个报
            double ratio = (double)a.Text.Length / Math.Max(1, b.Text.Length);
            if (ratio < 0.5 || ratio > 2.0)
            {
                list.Add(new Diff
                {
                    Chapter = title,
                    Kind = b.Text.Length > a.Text.Length ? DiffKind.MissingInPrimary : DiffKind.ExtraInPrimary,
                    Primary = a.Text.Length > 60 ? a.Text.Substring(0, 60) + "…" : a.Text,
                    Other = b.Text.Length > 60 ? b.Text.Substring(0, 60) + "…" : b.Text,
                    Context = string.Format("两源字数相差过大（{0} vs {1}），疑似站点差异而非错字，已跳过逐字比对",
                        a.Text.Length, b.Text.Length),
                });
                return list;
            }

            // LCS 动态规划求最短编辑脚本。
            // 章节正文几 KB，n*m 在内存上没问题；但为了稳妥，超长章节降级为"只比对相同长度的前缀段"。
            int n = a.Text.Length, m = b.Text.Length;
            if ((long)n * m > 40L * 1000 * 1000)
            {
                list.Add(new Diff
                {
                    Chapter = title,
                    Kind = DiffKind.Replace,
                    Context = string.Format("章节过长（{0} vs {1} 字），已跳过逐字比对以免占用过多内存", n, m),
                });
                return list;
            }

            var dp = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    if (a.Text[i] == b.Text[j]) dp[i, j] = dp[i + 1, j + 1] + 1;
                    else dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }

            // 回溯出编辑脚本，并把相邻的同类操作合并成"一段差异"
            int x = 0, y = 0;
            var runP = new StringBuilder();
            var runO = new StringBuilder();
            int runStart = -1;

            while (x < n && y < m)
            {
                if (a.Text[x] == b.Text[y])
                {
                    Flush(list, title, a, runStart, runP, runO);
                    x++; y++;
                    continue;
                }
                if (runStart < 0) runStart = a.Map[x];
                if (dp[x + 1, y] >= dp[x, y + 1])
                {
                    runP.Append(a.Text[x]);            // 主源多出来的字
                    x++;
                }
                else
                {
                    runO.Append(b.Text[y]);            // 对照源多出来的字
                    y++;
                }
                if (list.Count >= MaxDiffsPerChapter) break;
            }
            // 收尾：编辑脚本走完后可能还剩字（一边比另一边长）。
            // 这些剩余字符归入"最后的差异段"，起点沿用当前段，没有段就用主源末尾。
            while (x < n) { if (runStart < 0) runStart = a.Map[x]; runP.Append(a.Text[x]); x++; }
            if (y < m && runStart < 0) runStart = n > 0 ? a.Map[n - 1] : 0;
            while (y < m) { runO.Append(b.Text[y]); y++; }
            Flush(list, title, a, runStart, runP, runO);

            return list;
        }

        /// <summary>把攒下来的差异段收成一条 Diff</summary>
        private static void Flush(List<Diff> list, string title, Norm a, int start,
            StringBuilder runP, StringBuilder runO)
        {
            if (runP.Length == 0 && runO.Length == 0) return;
            if (list.Count >= MaxDiffsPerChapter) { runP.Length = 0; runO.Length = 0; return; }

            var kind = DiffKind.Replace;
            if (runP.Length > 0 && runO.Length == 0) kind = DiffKind.ExtraInPrimary;
            else if (runP.Length == 0 && runO.Length > 0) kind = DiffKind.MissingInPrimary;

            var d = new Diff
            {
                Chapter = title,
                Kind = kind,
                Position = start < 0 ? 0 : start,
                Primary = runP.ToString(),
                Other = runO.ToString(),
            };
            d.Context = MakeContext(a.Text, start);
            list.Add(d);

            runP.Length = 0;
            runO.Length = 0;
        }

        /// <summary>差异点前后各取 12 个字做上下文（用主源那边）</summary>
        private static string MakeContext(string text, int pos)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (pos < 0) pos = 0;
            int from = Math.Max(0, pos - 12);
            int to = Math.Min(text.Length, pos + 12);
            var s = text.Substring(from, to - from);
            if (from > 0) s = "…" + s;
            if (to < text.Length) s = s + "…";
            return s;
        }

        /// <summary>规范化后的正文 + 每个有效字符回指原串的下标</summary>
        private class Norm
        {
            public string Text;
            public int[] Map;
        }

        /// <summary>
        /// 压掉空白与站点常见的分隔噪声。
        /// 为什么必须做：两个源对空行、全角空格、"&amp;nbsp;" 的处理都不一样，
        /// 不归一化的话每一段空行都会被报成一处差异，报告直接没法看。
        /// </summary>
        private static Norm Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            var map = new List<int>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) continue;
                if (c == '\u3000') continue;            // 全角空格
                if (c == '\uFEFF') continue;            // BOM 残留
                sb.Append(c);
                map.Add(i);
            }
            return new Norm { Text = sb.ToString(), Map = map.ToArray() };
        }

        // ------------------------------------------------------------ 报告

        /// <summary>写成给人看的报告（txt）</summary>
        public static string BuildReport(BookInfo book, Result r, string primaryPath, string otherSource)
        {
            var sb = new StringBuilder();
            sb.AppendLine("《" + (book == null ? r.Title : book.Title) + "》错字检测报告（双源比对）");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("主源文件：" + primaryPath);
            sb.AppendLine("对照源　：" + otherSource);
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();
            sb.AppendLine(r.Summary());
            sb.AppendLine();
            sb.AppendLine("怎么读这份报告：");
            sb.AppendLine("  · 「主源」是你已经下载的这份，「对照源」是另一份。两个源同时错成同一个字的");
            sb.AppendLine("    概率极低，所以**列出来的位置至少有一边是错的**，两边写法都摆出来供你判断。");
            sb.AppendLine("  · 位置是主源正文里的字符下标，方便在自己的 txt 里找。");
            sb.AppendLine("  · 字数一致却不同 → 最可能就是错字（下面前两类）。");
            sb.AppendLine("  · 一边多一边少 → 多为漏字/多字；也可能是站点广告没洗净。");
            sb.AppendLine("  · **两个源都错同一个字时检测不出来**（只能靠第三个源或人工）。");
            if (HasAi(r))
            {
                sb.AppendLine("  · 带【AI】标记的行是交给大模型裁决过的：它会指出哪个写法对，");
                sb.AppendLine("    或者两个都不对时给出建议写法。AI 也可能判断错，重要处请自己复核。");
            }
            sb.AppendLine();

            // AI 已经明确判定"主源这里错了"的，单独拎到最前面 ——
            // 这是用户真正要改的地方，埋在几百条里等于没找到。
            WriteAiConfirmed(sb, r);

            // 按类型分组
            WriteGroup(sb, r, DiffKind.Replace, "【一】字数一致但用字不同（最可能是错字）");
            WriteGroup(sb, r, DiffKind.ExtraInPrimary, "【二】主源多了内容（多字，或站点差异）");
            WriteGroup(sb, r, DiffKind.MissingInPrimary, "【三】主源少了内容（漏字，或站点差异）");

            if (r.Diffs.Count == 0)
                sb.AppendLine("两个源逐字一致，没有发现差异。");
            return sb.ToString();
        }

        private static bool HasAi(Result r)
        {
            foreach (var d in r.Diffs)
                if (d.Ai != null && d.Ai.Verdict != AiAdjudicator.Verdict.None) return true;
            return false;
        }

        /// <summary>把 AI 判定"主源确实错了"的差异汇总到报告最前面（待改清单）</summary>
        private static void WriteAiConfirmed(StringBuilder sb, Result r)
        {
            var confirmed = new List<Diff>();
            foreach (var d in r.Diffs)
            {
                if (d.Ai == null) continue;
                if (d.Ai.Verdict == AiAdjudicator.Verdict.OtherRight ||
                    d.Ai.Verdict == AiAdjudicator.Verdict.BothWrong)
                    confirmed.Add(d);
            }
            if (confirmed.Count == 0) return;

            sb.AppendLine("★ 待改清单（AI 判定这些位置的主源写法有问题）　共 " + confirmed.Count + " 处");
            sb.AppendLine(new string('-', 60));
            foreach (var d in confirmed)
            {
                var fix = d.Ai.Verdict == AiAdjudicator.Verdict.BothWrong && d.Ai.Suggestion.Length > 0
                    ? d.Ai.Suggestion
                    : d.Other;
                sb.AppendLine(string.Format("{0} @{1}　「{2}」→「{3}」{4}",
                    d.Chapter, d.Position, d.Primary, fix,
                    d.Ai.Reason.Length > 0 ? "　（" + d.Ai.Reason + "）" : ""));
            }
            sb.AppendLine();
            sb.AppendLine("（要精确替换，用同目录的 错字检测报告.csv 按「位置」列定位）");
            sb.AppendLine();
        }

        private static void WriteGroup(StringBuilder sb, Result r, DiffKind kind, string header)
        {
            var items = new List<Diff>();
            foreach (var d in r.Diffs) if (d.Kind == kind) items.Add(d);
            if (items.Count == 0) return;

            sb.AppendLine(header + "　共 " + items.Count + " 处");
            sb.AppendLine(new string('-', 60));
            foreach (var d in items)
            {
                sb.AppendLine(string.Format("章节：{0}", d.Chapter));
                if (!string.IsNullOrEmpty(d.Context)) sb.AppendLine("  上下文：" + d.Context);
                sb.AppendLine(string.Format("  主源　：{0}", d.Primary.Length == 0 ? "(无)" : d.Primary));
                sb.AppendLine(string.Format("  对照源：{0}", d.Other.Length == 0 ? "(无)" : d.Other));
                if (d.Ai != null && d.Ai.Verdict != AiAdjudicator.Verdict.None)
                    sb.AppendLine(string.Format("  【AI】{0}{1}", d.Ai.VerdictText(),
                        d.Ai.Reason.Length > 0 ? "　理由：" + d.Ai.Reason : ""));
                sb.AppendLine();
            }
        }

        /// <summary>写成 CSV（方便用 Excel 过一遍）</summary>
        public static string BuildCsv(Result r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("章节,位置,类型,主源,对照源,上下文,AI结论,AI建议");
            foreach (var d in r.Diffs)
            {
                var sug = (d.Ai != null && d.Ai.Verdict == AiAdjudicator.Verdict.BothWrong)
                    ? d.Ai.Suggestion : "";
                sb.Append(Csv(d.Chapter)).Append(',')
                  .Append(d.Position).Append(',')
                  .Append(KindName(d.Kind)).Append(',')
                  .Append(Csv(d.Primary)).Append(',')
                  .Append(Csv(d.Other)).Append(',')
                  .Append(Csv(d.Context)).Append(',')
                  .Append(Csv(d.AiText())).Append(',')
                  .Append(Csv(sug)).Append('\n');
            }
            return sb.ToString();
        }

        private static string KindName(DiffKind k)
        {
            switch (k)
            {
                case DiffKind.Replace: return "用字不同";
                case DiffKind.ExtraInPrimary: return "主源多字";
                default: return "主源漏字";
            }
        }

        /// <summary>CSV 字段转义（Excel 打开中文 CSV 要注意 BOM，调用方负责写 BOM）</summary>
        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\"", "\"\"");
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) return "\"" + s + "\"";
            return s;
        }
    }
}
