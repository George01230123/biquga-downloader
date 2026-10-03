using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using TomatoBiquga;

/// <summary>
/// 检查每个按钮的文字**是否真的放得下**（不出现省略号/截断）。
///
/// 为什么需要单独一个探针：布局体检（_layoutprobe / TestLayout）只能查"越界/重叠"，
/// **查不出"按钮太窄把文字压成 搜… "** —— 那种情况既不越界也不重叠，
/// 几何上完全合规。用户截图里整排按钮都成了「搜…」「载入…」「设…」，
/// 就是这个盲区漏掉的。
///
/// 判据：用 TextRenderer 实测文字宽度（含按钮内边距），和按钮实际宽度比。
/// AutoSize 生效时应当总是放得下；放不下就报红。
/// </summary>
internal static class TextFitProbe
{
    private static int _pass, _fail;
    private static readonly List<string> Fails = new List<string>();

    private static void Main()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine("=== 按钮文字是否放得下（防省略号） ===");
        Console.WriteLine();

        var sizes = new[]
        {
            new Size(820, 600),    // 最小尺寸
            new Size(1000, 720),   // 默认
            new Size(1400, 900),   // 拉大
        };
        var sites = new[] { 0, 1, 2 };   // 番茄 / 移动版 / PC版

        foreach (var size in sizes)
        {
            foreach (var si in sites)
            {
                MainForm f = null;
                try
                {
                    f = new MainForm();
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-4000, -4000);
                    f.ShowInTaskbar = false;
                    f.SuppressDialogs = true;
                    f.Size = size;
                    f.Show();
                    Application.DoEvents();
                    f.SelectSiteForTest(si);
                    Application.DoEvents();
                    f.PerformLayout();
                    Application.DoEvents();

                    CheckButtons(f, string.Format("{0}x{1}/站点{2}", size.Width, size.Height, si));
                }
                catch (Exception ex)
                {
                    Record("探针不抛异常（" + size + "）", false, ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    try { if (f != null) { f.Hide(); f.Dispose(); } } catch { }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(string.Format("通过 {0}/{1}", _pass, _pass + _fail));
        if (_fail > 0)
        {
            Console.WriteLine("放不下的按钮：");
            foreach (var s in Fails) Console.WriteLine("  " + s);
        }
        else
        {
            Console.WriteLine("==> 所有按钮文字都完整显示（无省略号）");
        }
        Environment.ExitCode = _fail == 0 ? 0 : 1;
    }

    private static void CheckButtons(Control parent, string where)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is Button && !string.IsNullOrEmpty(c.Text))
            {
                var b = (Button)c;
                // 按钮文字可用宽 = 控件宽 - 左右内边距（WinForms 默认约 3px 边框 + padding）
                var measured = TextRenderer.MeasureText(b.Text, b.Font);
                int chrome = b.Width - b.ClientSize.Width;        // 边框占的宽度
                int available = b.ClientSize.Width - 6;           // 留 3px 边距
                bool fits = measured.Width <= available;

                Record(string.Format("文字放得下：「{0}」({1}) 需要 {2}px / 可用 {3}px",
                    b.Text, where, measured.Width, available), fits,
                    fits ? "" : "宽度 " + b.Width + "px 不够，会被压成省略号");

                // 反过来也查一下：AutoEllipsis 开着的话，即使宽度够也可能显示省略号
                if (b.AutoEllipsis)
                    Record("AutoEllipsis 已关闭：「" + b.Text + "」", false,
                        "开着 AutoEllipsis 会在宽度不足时显示省略号，掩盖真正的问题");
            }
            if (c.HasChildren) CheckButtons(c, where);
        }
    }

    private static void Record(string name, bool ok, string detail)
    {
        if (ok) { _pass++; return; }
        _fail++;
        var line = "[FAIL] " + name + (detail.Length > 0 ? " → " + detail : "");
        Console.WriteLine(line);
        Fails.Add(line);
    }
}
