using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TomatoBiquga
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        /// <summary>
        /// 把本进程挂到父进程的控制台上（-1 = ATTACH_PARENT_PROCESS）。
        /// GUI 子系统进程默认没有控制台，`--selftest` 要看输出就必须先挂上去。
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        private static void Main(string[] args)
        {
            // ★ 命令行自测：`小说下载器.exe --selftest`
            //
            // 这段必须用 #if SELFTEST 隔离 —— 不是保险，是必需：
            // Program.cs 被 7 个编译目标共用（GUI + _selftest + _edgetest + _layoutprobe
            // + _textfit + _aimock + _liveprobe），而 OfflineTests.cs 只编进 GUI 主程序
            // 与 _offlinetests.exe。Program.cs 本身是共用的，所以它**不能引用**只在某一个
            // 目标里存在的类型。实测连踩两次：
            //   ① 直接调 OfflineTests.Run → _selftest.exe 报 CS0103 找不到 OfflineTests；
            //   ② 挪进 SelfTestEntry.cs     → 又报找不到 SelfTestEntry（同一原因）。
            // 正解：只有 GUI 目标带 SELFTEST 常量（build.bat 与 *.csproj **两处**都要改）。
            //
            // 收益：**"测试跑通的那个二进制"和"用户手里这个二进制"从此是同一个文件**。
#if SELFTEST
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                // ★ GUI 子系统（winexe）的进程**默认没有控制台**，
                //   Console.set_OutputEncoding 会抛 IOException（"句柄无效"，退出码 0xE0434352）。
                //   必须先 attach 到父进程（调用它的那个 cmd/PowerShell）的控制台。
                try { AttachConsole(-1); } catch { }
                try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { }

                // ★ 跳过「布局体检」那一条 —— 详见 OfflineTests.Run 里 --skip-layout 的注释：
                //   它会真的 new 一个 MainForm 去量控件，量出来的尺寸依赖进程的 DPI 环境，
                //   于是同一个断言在控制台进程通过、在 GUI 进程失败。
                //   布局另有 _layoutprobe.exe / _textfit.exe 两个专门的探针在 CI 里守着。
                var inner = new string[(args == null ? 0 : args.Length) + 1];
                if (args != null) Array.Copy(args, inner, args.Length);
                inner[inner.Length - 1] = "--skip-layout";

                int code;
                try { code = OfflineTests.Run(inner); }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine("自测异常：" + ex); } catch { }
                    code = 1;
                }
                Environment.ExitCode = code;
                return;
            }
#endif

            EnableHighDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序出错：" + ex.Message, "小说下载器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 高分屏适配。build.bat 用 csc 直接编译时不带清单（app.manifest 只在
        /// dotnet build 那一路上），所以代码里再兜一次，保证两条编译路径表现一致。
        /// </summary>
        private static void EnableHighDpi()
        {
            try
            {
                if (Environment.OSVersion.Version.Major >= 6) SetProcessDPIAware();
            }
            catch { /* 老系统上失败不影响运行 */ }
        }
    }
}
