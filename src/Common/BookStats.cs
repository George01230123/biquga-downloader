using System;
using System.Collections.Generic;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 字数统计与阅读时长估算。
    ///
    /// 为什么要自己数而不是信站点的：番茄的 page 里有 wordNumber 可以直接用，
    /// 但笔趣阁没有；而且**用户真正关心的是"我已经下载了多少字"**，
    /// 这个数字只有数本地正文才准（站点给的是全本字数，缺章的书会虚高）。
    ///
    /// 口径说明（这个必须写清楚，否则数字对不上会被当成 bug）：
    ///   - 汉字：按**字符**数 1:1，一个汉字算一个字（和网文圈的通行口径一致，
    ///     起点/番茄的"字数"也是按字符算的，不是按词）；
    ///   - 非汉字的可见字符（英文字母、数字、标点）也计入，因为它们确实占阅读量；
    ///   - 空白字符（空格/换行/制表）不计。
    /// 所以统计结果 ≈ "正文里所有非空白字符的个数"，和大多数阅读器的口径一致。
    /// </summary>
    public static class BookStats
    {
        /// <summary>
        /// 中文阅读速度（字/分钟）。350 是一个偏保守的值：
        /// 网文阅读速度普遍在 300~500 字/分钟，取低值免得"估算 3 小时"实际读 5 小时。
        /// 做成字段而不是 const，方便以后接到设置里。
        /// </summary>
        public static int CharsPerMinute = 350;

        /// <summary>一本书的统计结果</summary>
        public class Stats
        {
            /// <summary>已统计的正文字符数（不含空白）</summary>
            public long Chars;
            /// <summary>其中汉字的个数</summary>
            public long HanChars;
            /// <summary>参与统计的章节数</summary>
            public int ChapterCount;
            /// <summary>有正文的章节数（和 ChapterCount 的区别：空章不计入）</summary>
            public int NonEmptyChapters;

            /// <summary>约多少分钟读完</summary>
            public double Minutes
            {
                get
                {
                    // 嵌套类不会继承外部类的成员，必须写全 BookStats.
                    if (BookStats.CharsPerMinute <= 0) return 0;
                    return (double)Chars / BookStats.CharsPerMinute;
                }
            }

            /// <summary>人话版字数：1234 → "1234 字"，123456 → "12.3 万字"</summary>
            public string HumanChars { get { return BookStats.Humanize(Chars); } }

            /// <summary>人话版时长："约 3 小时 20 分钟"</summary>
            public string HumanTime { get { return BookStats.HumanizeMinutes(Minutes); } }

            public override string ToString()
            {
                return string.Format("{0} 字，约 {1}（{2} 章）", HumanChars, HumanTime, NonEmptyChapters);
            }
        }

        /// <summary>统计一组章节的正文</summary>
        public static Stats Measure(IEnumerable<ChapterInfo> chapters)
        {
            var s = new Stats();
            if (chapters == null) return s;
            foreach (var c in chapters)
            {
                if (c == null || c.IsVolume) continue;
                s.ChapterCount++;
                if (string.IsNullOrEmpty(c.Text)) continue;
                s.NonEmptyChapters++;
                CountInto(c.Text, s);
            }
            return s;
        }

        /// <summary>统计一段正文，累加进 s</summary>
        public static void CountInto(string text, Stats s)
        {
            if (string.IsNullOrEmpty(text) || s == null) return;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (char.IsWhiteSpace(ch)) continue;

                // 代理对（emoji / 生僻字扩展区）：高低位合起来算一个字，
                // 否则会被数成两个字，长书累积起来误差可观。
                if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    s.Chars++;
                    i++;
                    continue;
                }

                s.Chars++;
                if (IsHan(ch)) s.HanChars++;
            }
        }

        /// <summary>是不是汉字（含扩展 A 区；扩展 B 及以上是代理对，在上面的分支里处理）</summary>
        public static bool IsHan(char c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF)      // 基本区
                || (c >= 0x3400 && c <= 0x4DBF)      // 扩展 A
                || (c >= 0xF900 && c <= 0xFAFF);     // 兼容汉字
        }

        /// <summary>数字转人话：12345 → "1.2 万"，123456789 → "1.2 亿"</summary>
        public static string Humanize(long n)
        {
            if (n < 10000) return n.ToString("N0") + " 字";
            if (n < 100000000) return (n / 10000.0).ToString("0.#") + " 万字";
            return (n / 100000000.0).ToString("0.##") + " 亿字";
        }

        /// <summary>分钟转人话：45 → "45 分钟"，200 → "3 小时 20 分钟"</summary>
        public static string HumanizeMinutes(double minutes)
        {
            if (minutes <= 0) return "0 分钟";
            if (minutes < 1) return "不到 1 分钟";
            if (minutes < 60) return ((int)Math.Round(minutes)) + " 分钟";

            int total = (int)Math.Round(minutes);
            int h = total / 60;
            int m = total % 60;
            if (m == 0) return h + " 小时";
            return h + " 小时 " + m + " 分钟";
        }

        /// <summary>
        /// 给界面/表头用的一行摘要。
        /// 站点给了 WordCount（番茄）时优先用它，并标注「站点数据」，
        /// 因为那是全本字数 —— 和「已下载字数」不是一回事，不能混为一谈。
        /// </summary>
        public static string Summary(BookInfo book, IEnumerable<ChapterInfo> chapters)
        {
            var s = Measure(chapters);
            if (book != null && book.WordCount > 0)
                return string.Format("已下载 {0}（站点标称 {1}，约 {2} 读完）",
                    s.HumanChars, Humanize(book.WordCount), s.HumanTime);
            return string.Format("{0}，约 {1} 读完", s.HumanChars, s.HumanTime);
        }
    }
}
