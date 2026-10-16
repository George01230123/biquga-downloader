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

        /// <summary>
        /// 额外的日志接收方（界面注册）。
        ///
        /// ★ 为什么需要：<see cref="Line"/> 原来只写 Console，而 **GUI(winexe) 进程
        /// 没有控制台**，所以 Common 层里写的诊断信息在界面里根本看不到 ——
        /// 那些信息只出现在命令行自测程序里。
        /// 结果是排查真实故障时一片黑：比如 SearchIndex 加载失败、
        /// 界面刷新异常，全都"消失"了。
        ///
        /// 界面在构造时注册进来，之后任何 Common 层的日志都能进界面日志区。
        /// </summary>
        public static Action<string> Sink;

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

        /// <summary>
        /// 带时间戳的一行。同时写 Console（命令行程序）和 <see cref="Sink"/>（界面）。
        ///
        /// ★ 两处都要写：GUI(winexe) 进程**没有控制台**，只写 Console 的话
        ///   界面里什么都看不到 —— Common 层的诊断信息（比如"搜索索引读取失败"）
        ///   就这么消失了，排查真实故障时一片黑。
        /// </summary>
        public static void Line(string msg)
        {
            var text = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + (msg ?? "");
            try { Console.WriteLine(text); }
            catch { /* 输出被重定向关掉了也不能让程序崩 */ }

            var sink = Sink;
            if (sink != null)
            {
                // ★ 绝不能在这里再调 Log.Line —— 会自己喂自己形成死循环。
                //   界面那个 sink 只往日志控件里追加。
                try { sink(text); } catch { /* 界面可能正在关闭 */ }
            }
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
