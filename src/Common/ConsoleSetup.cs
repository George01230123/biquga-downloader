using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 控制台编码初始化。**每个带文字输出的入口都要在打印第一行之前调一次。**
    ///
    /// ── 为什么需要它（真实故障）────────────────────────────
    /// 用户在 cmd / 双击里跑，输出中文全是乱码：
    ///     鎼滅储 鎼滅储鈥滄钵鍏冨浘鈥濆緱鍒?3 鏉＄粨鏋?
    /// 而同一份输出按 UTF-8 解码是好的（"搜索“沧元图”得到 3 条结果"）。
    ///
    /// 根因是**两条输出路径的编码规则不一样**：
    ///
    ///   · stdout 被重定向到文件/管道时：.NET 按 <see cref="Console.OutputEncoding"/>
    ///     把字符编成字节写出去。设成 UTF-8 就是 UTF-8 字节，正确。
    ///
    ///   · stdout 是**真实控制台**时：Windows 按**控制台自己的输出代码页**
    ///     把字节翻译成屏幕字符。这个代码页默认是 936(GBK)，
    ///     于是 UTF-8 字节被当 GBK 解释 → 乱码。
    ///
    /// 所以只设 <c>Console.OutputEncoding</c> **不够** —— 还得把控制台的
    /// 输出代码页一起切到 65001。反过来也一样：只切代码页、
    /// 不设 OutputEncoding，重定向那条路径又会写错。
    ///
    /// 实测（cmd 里 `chcp 936` 后跑）：
    ///   _selftest.exe      设了 OutputEncoding、没切代码页 → 控制台里乱码
    ///   _liveprobe.exe     同上 → 乱码
    /// 本方法两件事都做，两条路径都正确。
    ///
    /// ── 为什么用 P/Invoke 而不是别的 ──────────────────────
    /// <c>Console.OutputEncoding = ...</c> 在 .NET Framework 上**有时也会顺手**
    /// 调 SetConsoleOutputCP，但这是实现细节、不是契约；在"句柄无效"
    /// （GUI 子系统没挂控制台）时它直接抛 IOException。显式调 Win32 更可靠，
    /// 失败也只是返回 false，不影响程序继续跑。
    ///
    /// ── 注意 ───────────────────────────────────────────
    /// SetConsoleOutputCP 改的是**整个控制台**的设置，不只本进程。
    /// 对命令行工具来说这是期望行为（cmd 里 chcp 65001 就是这个效果），
    /// 而且进程退出后调用方自己会恢复。所以不额外做保存/还原。
    /// </summary>
    internal static class ConsoleSetup
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleOutputCP(uint wCodePageID);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCP(uint wCodePageID);

        /// <summary>UTF-8 的代码页编号（Windows 用 65001 表示 UTF-8）。</summary>
        private const uint Utf8CodePage = 65001;

        /// <summary>最近一次初始化是否成功切换了控制台代码页（测试用）。</summary>
        public static bool CodePageSwitched { get; private set; }

        /// <summary>最近一次初始化是否把 Console.Out 换成了 UTF-8 直写流（测试用）。</summary>
        public static bool BoundUtf8Writer { get; private set; }

        /// <summary>最近一次 <see cref="AttachForOutput"/> 是否拿到了可写的输出通道（测试用）。</summary>
        public static bool HasOutput { get; private set; }

        /// <summary>
        /// stdout 是否已被重定向（管道/文件）。重定向时进程**已经有有效的输出句柄**，
        /// 不需要也不该再去挂控制台。
        /// </summary>
        public static bool StdoutIsRedirected()
        {
            try
            {
                IntPtr h = GetStdHandle(StdOutputHandle);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
                uint type = GetFileType(h);
                const uint FileTypeDisk = 0x0001, FileTypePipe = 0x0003;
                return type == FileTypeDisk || type == FileTypePipe;
            }
            catch { return false; }
        }

        /// <summary>
        /// 给 <c>--selftest</c> 这种"GUI 子系统进程却要打印东西"的场景准备输出通道。
        ///
        /// ── 为什么需要它（真实故障）────────────────────────────
        /// `小说下载器.exe` 是 **winexe（GUI 子系统）**，这类进程**默认没有控制台**，
        /// 而且 Windows 会把它的 stdout 句柄**置空**（实测 GetStdHandle 返回 0）。
        /// 于是"在 cmd 里跑 --selftest"什么都看不到：退出码 0、输出 0 字节。
        ///
        /// 处理顺序（每步只在前面都失败时才走）：
        ///   ① stdout 已被重定向 → 直接写就行（管道/文件那条路径）
        ///   ② <c>AttachConsole(-1)</c> 挂到父进程控制台 → 能写进那个窗口
        ///      实测 CI/沙箱里它会返回 ACCESS_DENIED(err=5)，所以必须有 ③
        ///   ③ 都没有 → 返回 false，由调用方引导用户去看日志文件
        /// </summary>
        public static bool AttachForOutput()
        {
            if (StdoutIsRedirected()) { HasOutput = true; return true; }   // ①
            try { if (AttachConsole(-1)) { HasOutput = true; return true; } } catch { }  // ②
            HasOutput = false;                                            // ③
            return false;
        }

        /// <summary>
        /// 把输出**同时**写进一个日志文件（原样保留 stdout）。
        /// 用于 <see cref="AttachForOutput"/> 失败后的兜底：用户至少能打开文件看结果。
        /// 返回实际写入的路径；失败返回 null（调用方据此不要再提示"结果已写入 xxx"）。
        /// </summary>
        public static string TeeToFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var fs = new System.IO.FileStream(path, System.IO.FileMode.Create,
                    System.IO.FileAccess.Write, System.IO.FileShare.Read);
                var w = new TeeWriter(Console.Out, new System.IO.StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true });
                Console.SetOut(w);
                return path;
            }
            catch { return null; }
        }

        /// <summary>
        /// 决定"自测结果"这类日志文件写到哪里。
        /// 首选 exe 同目录（用户一眼能找到）；那儿不可写（装在 Program Files、
        /// 或 U 盘写保护）就退到 %TEMP%。
        /// 实测踩过：只写 exe 同目录时，目录只读会让日志**静默丢失**，
        /// 用户又回到"什么都没看到"。
        /// </summary>
        public static string ResolveResultFile(string fileName)
        {
            string dir = null;
            try { dir = System.IO.Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath); }
            catch { }
            if (string.IsNullOrEmpty(dir))
            {
                try { dir = AppDomain.CurrentDomain.BaseDirectory; } catch { }
            }

            if (!string.IsNullOrEmpty(dir))
            {
                try
                {
                    string primary = System.IO.Path.Combine(dir, fileName);
                    // 真写一下试试，别只看目录属性 —— 写保护/权限不足只有写才知道
                    using (var probe = new System.IO.FileStream(primary, System.IO.FileMode.Append,
                        System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite)) { }
                    return primary;
                }
                catch { /* 落到 %TEMP% */ }
            }

            try { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName); }
            catch { return fileName; }   // 最后退成相对路径，至少不抛
        }

        /// <summary>把每一行同时写到两个 TextWriter（"既进控制台/管道，又落一份日志"）。</summary>
        private sealed class TeeWriter : System.IO.TextWriter
        {
            private readonly System.IO.TextWriter _a, _b;
            public TeeWriter(System.IO.TextWriter a, System.IO.TextWriter b) { _a = a; _b = b; }
            public override Encoding Encoding { get { return _a.Encoding; } }
            public override void Write(char c) { try { _a.Write(c); } catch { } try { _b.Write(c); } catch { } }
            public override void Write(string s) { try { _a.Write(s); } catch { } try { _b.Write(s); } catch { } }
            public override void WriteLine(string s) { try { _a.WriteLine(s); } catch { } try { _b.WriteLine(s); } catch { } }
            public override void Flush() { try { _a.Flush(); } catch { } try { _b.Flush(); } catch { } }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(IntPtr hFile);

        private const int StdOutputHandle = -11;

        /// <summary>
        /// 把控制台切到 UTF-8。可以重复调用（幂等）。
        /// 任何一步失败都不抛异常 —— 命令行工具不该因为编码设置挂掉。
        /// </summary>
        public static void UseUtf8()
        {
            // ① 控制台**输出**代码页：决定"真实控制台"路径怎么翻译字节。
            //    这是原先缺的一步，也就是乱码的直接原因。
            try { CodePageSwitched = SetConsoleOutputCP(Utf8CodePage); }
            catch { CodePageSwitched = false; }

            // ② 控制台**输入**代码页：让 Console.ReadLine 能正确读中文。
            //    交互式命令（比如"按回车退出"）在 936 下读 UTF-8 输入会出问题。
            try { SetConsoleCP(Utf8CodePage); } catch { }

            // ③ .NET 层的输出编码：决定"重定向"路径写出的字节。
            //    GUI 子系统（winexe）进程没有控制台时这一步会抛 IOException，
            //    所以必须包起来 —— Program.cs 的 --selftest 就是这种情形
            //    （它先 AttachConsole(-1) 再调本方法）。
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch
            {
                // ★ 关键补救：这一步失败时 **Console.Out 会按系统代码页(GBK)编码**，
                //   把本该是 UTF-8 的输出写坏 —— 实测 `小说下载器.exe --selftest`
                //   从 CI/管道调用时**一个字都出不来**（AttachConsole 返回
                //   ACCESS_DENIED，随后 set_OutputEncoding 抛"句柄无效"）。
                //   所以这里改成**绕开 Console.Out，直接往 stdout 的字节流写 UTF-8**：
                //   管道/重定向那条路径完全正确，且不依赖控制台是否存在。
                BindUtf8Stdout();
            }

            // ④ 顺带把输入编码也对齐（同样可能在无控制台时抛异常）。
            try { Console.InputEncoding = new UTF8Encoding(false); } catch { }
        }

        /// <summary>
        /// 把 <see cref="Console.Out"/>/<see cref="Console.Error"/> 换成"直接以 UTF-8
        /// 写 stdout/stderr 字节流"的写入器，绕开 .NET 那层代码页编码。
        ///
        /// 只在 <c>Console.OutputEncoding</c> 设不上时调用（见 <see cref="UseUtf8"/>）。
        /// 失败就保持原样，绝不让编码问题把程序搞挂。
        /// </summary>
        public static void BindUtf8Stdout()
        {
            try
            {
                var utf8 = new UTF8Encoding(false);
                // 用 OpenStandardOutput 而不是 Console.Out：前者拿到的是**原始字节流**，
                // 不经过 TextWriter 的编码器。AutoFlush 打开，保证进程退出前不丢输出。
                var so = Console.OpenStandardOutput();
                var se = Console.OpenStandardError();
                Console.SetOut(new System.IO.StreamWriter(so, utf8) { AutoFlush = true });
                Console.SetError(new System.IO.StreamWriter(se, utf8) { AutoFlush = true });
                BoundUtf8Writer = true;
            }
            catch { BoundUtf8Writer = false; }
        }

        /// <summary>
        /// 本进程是否**真的有**一个控制台窗口。
        ///
        /// 和 <see cref="StdoutIsRedirected"/> 不是一回事：
        ///   · 从 cmd/PowerShell 里跑 → 有控制台窗口，输出就显示在那儿
        ///   · 双击 exe              → 没有控制台窗口，输出**无处可去**
        ///
        /// 用途：决定"要不要弹个模态框告诉用户结果"。
        /// 只判 <c>Environment.UserInteractive</c> **不够** —— 实测在有桌面的机器上
        /// 它恒为 true，于是从 cmd 跑也会弹窗、把自动化流程挂死（实测挂过 573 秒）。
        /// </summary>
        public static bool HasConsoleWindow()
        {
            try { return GetConsoleWindow() != IntPtr.Zero; }
            catch { return false; }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
    }
}
