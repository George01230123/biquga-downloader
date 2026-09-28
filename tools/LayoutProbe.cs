using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TomatoBiquga
{
    /// <summary>
    /// 界面布局体检：把窗体摆到指定尺寸（可能超出屏幕，WinForms 照样能布局），
    /// 然后报告每个控件的坐标、是否越出父容器、以及同层控件是否互相重叠。
    /// 用途：改界面之前先量出"到底哪个控件被谁盖住了"，而不是靠肉眼猜。
    /// 用法：_layoutprobe.exe [宽 高] ...（默认测 1000x720 / 900x660 / 820x600 / 1280x800）
    /// </summary>
    internal static class LayoutProbe
    {
        /// <summary>--shots 时才真的截屏（默认只量尺寸，速度快且不闪窗）</summary>
        private static bool Shots;

        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_NOACTIVATE = 0x0010;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                return RunProbe(args);
            }
            catch (Exception ex)
            {
                // 崩溃信息写文件：直接打控制台会因为控制台编码不是 UTF-8 而"无法打印异常字符串"
                var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_layout_error.txt");
                var text = "LayoutProbe 崩了：" + ex.GetType().FullName + Environment.NewLine +
                           ex.Message + Environment.NewLine + Environment.NewLine + ex.StackTrace;
                try { File.WriteAllText(p, text, new UTF8Encoding(true)); } catch { }
                Console.WriteLine("LayoutProbe 崩了，详情见：" + p);
                Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
                return 2;
            }
        }

        private static int RunProbe(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var sizes = new List<Size>();
            bool shots = false;
            var argv = new List<string>();
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (string.Equals(a, "--shots", StringComparison.OrdinalIgnoreCase)) shots = true;
                    else argv.Add(a);
                }
            }
            Shots = shots;
            if (argv.Count >= 2)
            {
                for (int i = 0; i + 1 < argv.Count; i += 2)
                    sizes.Add(new Size(int.Parse(argv[i]), int.Parse(argv[i + 1])));
            }
            else
            {
                sizes.Add(new Size(1000, 720));   // 默认
                sizes.Add(new Size(900, 660));
                sizes.Add(new Size(820, 600));    // 最小尺寸
                sizes.Add(new Size(1280, 800));
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== 界面布局体检 ===");

            int problems = 0;
            string[] siteNames = { "番茄小说", "笔趣阁（移动版）", "笔趣阁（PC版）" };
            foreach (var size in sizes)
            {
                for (int si = 0; si < siteNames.Length; si++)
                {
                    sb.AppendLine();
                    sb.AppendLine(string.Format("---------------- 窗体 {0}x{1} / 站点 {2} ----------------",
                        size.Width, size.Height, siteNames[si]));
                    using (var f = new MainForm())
                    {
                        f.StartPosition = FormStartPosition.Manual;
                        f.Location = new Point(-4000, -4000);   // 默认摆屏幕外，不打扰人
                        f.ShowInTaskbar = false;
                        f.SuppressDialogs = true;               // 别弹窗卡住体检
                        f.Size = size;
                        f.Show();                                // Show 后再量，布局才是最终态
                        Application.DoEvents();
                        f.SelectSiteForTest(si);                 // 番茄模式会多出第三行
                        Application.DoEvents();
                        f.PerformLayout();

                        sb.AppendLine(string.Format("ClientSize = {0}x{1}", f.ClientSize.Width, f.ClientSize.Height));
                        problems += Dump(sb, f, f);

                        // 截屏要让窗口真的出现在屏幕上：临时挪进来，截完挪回去并隐藏。
                        // 用 SetWindowPos + SWP_NOACTIVATE 挪动，不抢用户当前的焦点。
                        if (Shots)
                        {
                            FitToScreen(f, size);
                            SetWindowPos(f.Handle, IntPtr.Zero, 20, 20, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                            // 必须把窗体提到最前：否则截图会把盖在它上面的别的窗口（比如微信）一起截进去。
                            // 只在 --shots 模式下会短暂抢一下焦点，量尺寸的默认模式完全不碰屏幕。
                            f.BringToFront();
                            f.Activate();
                            f.Refresh();
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(250);
                            SaveShot(f, size, siteNames[si]);
                            SetWindowPos(f.Handle, IntPtr.Zero, -4000, -4000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                        }
                        f.Hide();
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine(problems == 0 ? "==> 没发现越界/重叠" : string.Format("==> 发现 {0} 处问题", problems));

            var outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_layout.txt");
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
            // 控制台只打摘要：完整报告在 _layout.txt（控制台编码不一定是 UTF-8）
            Console.WriteLine(sb.ToString());
            Console.WriteLine("报告已写入：" + outPath);
            return problems == 0 ? 0 : 1;
        }

        /// <summary>递归遍历控件，检查越界与重叠</summary>
        private static int Dump(StringBuilder sb, Control parent, Form root)
        {
            int problems = 0;
            var kids = new List<Control>();
            var hidden = new List<Control>();
            foreach (Control c in parent.Controls)
            {
                // 不可见的控件不参与布局，也不算越界（比如番茄专用的第三行，在笔趣阁模式下是隐藏的）
                if (c.Visible) kids.Add(c); else hidden.Add(c);
            }
            // 按可见面积从大到小，便于人读
            kids.Sort((a, b) => (b.Width * b.Height).CompareTo(a.Width * a.Height));

            foreach (var c in kids)
            {
                bool overflow = c.Right > parent.ClientSize.Width || c.Bottom > parent.ClientSize.Height || c.Left < 0 || c.Top < 0;
                string name = Describe(c);
                if (overflow)
                {
                    problems++;
                    sb.AppendLine(string.Format("  [越界] {0}", name));
                    sb.AppendLine(string.Format("         控件 {0},{1} {2}x{3} (右={4} 下={5})  父容器 {6} 可显示区 {7}x{8}",
                        c.Left, c.Top, c.Width, c.Height, c.Right, c.Bottom,
                        parent.Name.Length > 0 ? parent.Name : parent.GetType().Name,
                        parent.ClientSize.Width, parent.ClientSize.Height));
                    int cut = c.Right - parent.ClientSize.Width;
                    if (cut > 0)
                        sb.AppendLine(string.Format("         → 右边被切掉 {0}px，有 {1} 个字看不见",
                            cut, Math.Max(1, (int)Math.Round(cut / (double)Math.Max(1, c.Font.Size * 1.1)))));
                    int cutY = c.Bottom - parent.ClientSize.Height;
                    if (cutY > 0)
                        sb.AppendLine(string.Format("         → 下边被切掉 {0}px（一行文字约 20px，会被裁字）", cutY));
                }

                // 同层重叠（只报有文字的，避免噪音）
                if (!string.IsNullOrEmpty(c.Text))
                {
                    foreach (var o in kids)
                    {
                        if (ReferenceEquals(o, c) || !o.Visible) continue;
                        var inter = Rectangle.Intersect(c.Bounds, o.Bounds);
                        if (inter.Width > 4 && inter.Height > 4)
                        {
                            // 同一对只报一次
                            if (string.CompareOrdinal(Describe(c), Describe(o)) < 0)
                            {
                                problems++;
                                sb.AppendLine(string.Format("  [重叠] {0}  <->  {1}   交集 {2}x{3}",
                                    Describe(c), Describe(o), inter.Width, inter.Height));
                            }
                        }
                    }
                }
            }

            if (kids.Count > 0)
            {
                sb.AppendLine(string.Format("  -- {0} 的子控件（{1} 个可见 / {2} 个隐藏）--",
                    parent.Name.Length > 0 ? parent.Name : parent.GetType().Name, kids.Count, hidden.Count));
                foreach (var c in kids)
                {
                    sb.AppendLine(string.Format("     {0,-46} {1,5},{2,-5} {3,5}x{4,-5} 文本='{5}'",
                        Describe(c), c.Left, c.Top, c.Width, c.Height, Short(c.Text)));
                }
                foreach (var c in hidden)
                {
                    sb.AppendLine(string.Format("     （隐藏）{0,-40} {1,5},{2,-5} {3,5}x{4,-5} 文本='{5}'",
                        Describe(c), c.Left, c.Top, c.Width, c.Height, Short(c.Text)));
                }
            }

            // 递归 container
            foreach (var c in kids)
            {
                if (c is SplitContainer || c is Panel || c is FlowLayoutPanel || c is TableLayoutPanel || c is GroupBox || c is TabControl)
                    problems += Dump(sb, c, root);
            }
            return problems;
        }

        /// <summary>
        /// 截图前把窗口缩到"能完整放进屏幕"的尺寸。
        /// 因为窗口尺寸是逻辑像素，125% 缩放下要乘 1.25 才是物理像素：
        /// 逻辑 1000px 的窗口在 2048 宽的屏上会跑到屏幕外，截出来右边就是黑的
        /// （一开始误以为是界面溢出，其实是取景问题）。
        /// </summary>
        private static void FitToScreen(Form f, Size want)
        {
            var screen = Screen.FromControl(f).Bounds;
            double scale = 1.0;
            try
            {
                using (var g = f.CreateGraphics()) scale = g.DpiX / 96.0;
            }
            catch { }
            if (scale <= 0.1) scale = 1.0;

            int maxW = (int)((screen.Width - 80) / scale);
            int maxH = (int)((screen.Height - 120) / scale);
            int w = Math.Min(want.Width, maxW);
            int h = Math.Min(want.Height, maxH);
            if (w != f.Width || h != f.Height)
            {
                f.Size = new Size(Math.Max(400, w), Math.Max(300, h));
                f.PerformLayout();
                Application.DoEvents();
            }
        }

        private static void SaveShot(Form f, Size size, string site)
        {
            try
            {
                var name = string.Format("_layout_{0}x{1}_{2}.png", size.Width, size.Height, Sanitize(site));
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);

                // 取景留 40px 余量：窗口的物理宽度不一定等于 Size 属性（边框/缩放差异），
                // 少了余量右边就会被裁掉一截，看起来像"界面溢出"，实际是截图没框全。
                var origin = f.PointToScreen(Point.Empty);
                var screen = Screen.FromControl(f).Bounds;
                int x = Math.Max(origin.X - 20, screen.Left);
                int y = Math.Max(origin.Y - 20, screen.Top);
                int right = Math.Min(origin.X + f.Width + 40, screen.Right);
                int bottom = Math.Min(origin.Y + f.Height + 40, screen.Bottom);
                int w = Math.Max(50, right - x);
                int h = Math.Max(50, bottom - y);

                using (var bmp = new Bitmap(w, h))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(new Point(x, y), new Point(0, 0), new Size(w, h));
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch { /* 截不到不影响体检结论 */ }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "site";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace('（', '_').Replace('）', '_').Replace(" ", "");
        }

        private static string Describe(Control c)        {
            string type = c.GetType().Name;
            string name = c.Name;
            if (!string.IsNullOrEmpty(name)) return type + "(" + name + ")";
            // 不是所有控件都设了 Name（本项目大量用匿名局部变量建控件），退化成用文本识别
            var t = c.Text;
            if (!string.IsNullOrEmpty(t)) return type + "('" + Short(t) + "')";
            return type + "(未命名)";
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 26 ? s.Substring(0, 26) + "…" : s;
        }
    }
}
