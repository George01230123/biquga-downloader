using System;
using System.Drawing;
using System.Drawing.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 界面字体选取。
    ///
    /// ── 为什么需要它（真实故障）────────────────────────────
    /// 用户截图里**界面日志区的中文全是方框**（tofu），但 ASCII 完全正常：
    ///     18:39:21 □□□□□ curl□C:\windows\system32\curl.exe□
    /// 这不是编码问题（编码错的话 ASCII 也会跟着坏），而是**字体缺字形**：
    /// 日志控件原来指定 `new Font("Consolas", 8.5F)` ——
    /// **Consolas 是纯拉丁字体，不含任何汉字字形**。
    /// 正常情况下 Windows 会做"字体链接"(font linking) 去兜底找中文字体，
    /// 但这依赖注册表里的 SystemLink / 字体回退设置，**在部分机器上不生效**，
    /// 于是中文整片渲染成方框。
    ///
    /// 结论：**凡是要显示中文的控件，都不能把字体写死成一个纯拉丁字体。**
    /// 这里按"优先等宽、但不牺牲中文"的顺序挑，挑不到就退到系统默认。
    ///
    /// 注意：不要改成"检查字体是否支持某段中文再选"—— 那要枚举字形、
    /// 又慢又脆。按名字挑 + 试构造，足够且好懂。
    /// </summary>
    internal static class UiFont
    {
        /// <summary>
        /// 等宽 + 含中文字形的候选字体（按优先级）。
        ///
        /// 为什么不直接用 Consolas：它没中文。
        /// 为什么不直接上"宋体"：太丑，而且点阵感重。
        /// 这几个都是 Windows 自带、且中文覆盖良好的：
        ///   · Microsoft YaHei UI —— 中文清晰，且是本程序界面默认字体，观感统一
        ///   · 微软雅黑 / MS Gothic / SimSun —— 依次兜底
        /// </summary>
        private static readonly string[] Candidates =
        {
            "Microsoft YaHei UI",
            "微软雅黑",
            "Microsoft YaHei",
            "NSimSun",
            "SimSun",
            "宋体",
            "MS Gothic",
        };

        /// <summary>挑一个"能显示中文"的界面字体；全都不行就返回系统默认字体。</summary>
        public static Font Mono(float size)
        {
            foreach (var name in Candidates)
            {
                try
                {
                    // 用 FontFamily 探活比"构造 Font 再比对 Name"可靠：
                    // Font 构造在字体缺失时**会静默回退**成别的字体，名字对不上，
                    // 而 FontFamily 构造失败会抛异常，能明确判断。
                    using (var ff = new FontFamily(name))
                    {
                        if (string.Equals(ff.Name, name, StringComparison.OrdinalIgnoreCase))
                            return new Font(ff, size);
                    }
                }
                catch { /* 这个字体没有，试下一个 */ }
            }
            // 连一个中文字体都没有（极端情况）：用系统默认，至少不会乱码得更糟
            try { return new Font(FontFamily.GenericSansSerif, size); }
            catch { return null; }
        }

        /// <summary>本机是否存在某个字体（诊断/测试用）。</summary>
        public static bool Exists(string familyName)
        {
            if (string.IsNullOrEmpty(familyName)) return false;
            try
            {
                using (var ff = new FontFamily(familyName))
                    return string.Equals(ff.Name, familyName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 这台机器上"日志区该用的那个字体"实际落到了哪个（诊断用）。
        /// 用户报"界面中文是方框"时，先问这个值最快。
        /// </summary>
        public static string ResolvedName
        {
            get
            {
                foreach (var name in Candidates)
                    if (Exists(name)) return name;
                return "(回退到系统默认)";
            }
        }
    }
}
