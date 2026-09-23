using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TomatoBiquga
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
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
