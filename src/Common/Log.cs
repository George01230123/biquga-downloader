using System;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 统一日志：带时间戳、可选带步骤序号。
    ///
    /// 为什么需要它：命令行自测程序（`_selftest`）原来直接 `Console.WriteLine`，
    /// **没有时间戳**。排查"卡在哪一步、某一步花了多久"时完全对不上时间；
    /// 而界面里的日志早就有时间戳了，两边不一致也让对日志很费劲。
    ///
    /// 步骤序号（`[1/5]`）是给"一次下载要走搜索→目录→下载→写盘"这种流程用的：
    /// 用户贴日志过来时，一眼能看出停在第几步。
    /// </summary>
    public static class Log
    {
        /// <summary>当前步骤 / 总步骤。0 表示不使用步骤编号。</summary>
        private static int _step;
        private static int _stepTotal;

        /// <summary>开始一段带步骤编号的流程</summary>
        public static void BeginSteps(int total)
        {
            _step = 0;
            _stepTotal = total;
        }

        /// <summary>进入下一步，并写一行标题</summary>
        public static void Step(string title)
        {
            _step++;
            if (_stepTotal > 0) Line(string.Format("[{0}/{1}] {2}", _step, _stepTotal, title));
            else Line(title);
        }

        /// <summary>结束步骤编号（之后的 Line 不再带编号）</summary>
        public static void EndSteps()
        {
            _step = 0;
            _stepTotal = 0;
        }

        /// <summary>带时间戳的一行</summary>
        public static void Line(string msg)
        {
            var text = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + (msg ?? "");
            try { Console.WriteLine(text); }
            catch { /* 输出被重定向关掉了也不能让程序崩 */ }
        }

        /// <summary>不带时间戳的一行（用于本来就是标题/分隔线的内容）</summary>
        public static void Raw(string msg)
        {
            try { Console.WriteLine(msg ?? ""); }
            catch { }
        }

        /// <summary>空白行</summary>
        public static void Blank()
        {
            try { Console.WriteLine(); }
            catch { }
        }
    }
}
