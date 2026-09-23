using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TomatoBiquga
{
    public class MainForm : Form
    {
        private ComboBox cboSite;
        private TextBox txtKeyword;
        private Button btnSearch;
        private Button btnLoad;
        private Button btnReload;
        private Button btnWebSearch;
        private ListView lstBooks;
        private ListView lstChapters;
        private Button btnSelectAll, btnSelectNone, btnInvert;
        private Button btnDownload, btnCancel, btnOpenFolder;
        private Button btnDownloadAll;
        private Label lblSel;
        private Button btnOfficial, btnCoreSetup;
        private ProgressBar progress;
        private Label lblStatus;
        private TextBox txtLog;
        private TextBox txtOutput;
        private Button btnBrowse;
        private Button btnSettings;

        private ISite _site;
        private SplitContainer _split;
        private CheckBox chkOffline;
        private AppSettings _settings;     // 并发/间隔/重试，来自 exe 同目录的 settings.ini
        private string _lastOfficialFile;   // 官方工具最近一次的输出文件（用于「打开保存目录」）

        /// <summary>把分隔条位置夹到合法范围内（窗口还很小的时候尤其重要）</summary>
        private void ClampSplitter()
        {
            if (_split == null) return;
            try
            {
                int h = _split.Height;
                int want = _preferredSplit;
                if (want <= 0) want = Math.Max(120, (int)(h * 0.28));
                int min = _split.Panel1MinSize;
                int max = h - _split.Panel2MinSize - _split.SplitterWidth;
                if (max < min) return;                 // 空间实在不够，先不动
                if (want < min) want = min;
                if (want > max) want = max;
                if (_split.SplitterDistance != want) _split.SplitterDistance = want;
            }
            catch (Exception)
            {
                // 布局竞态时忽略，下一次尺寸变化会再夹一次
            }
        }

        private int _preferredSplit;
        private BookInfo _currentBook;
        private volatile bool _cancel;
        private volatile bool _busy;

        public MainForm()
        {
            // 标题避开第三方商标：产品名用 ASCII 的 novel-downloader（= 仓库名），中文名只作说明
            Text = "小说下载器 v1.0（免安装单文件版）";
            Width = 1000;
            Height = 720;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            MinimumSize = new Size(820, 600);
            // 先加载设置再建界面：BuildUi 会在日志里打印当前设置
            _settings = AppSettings.Current;
            BuildUi();
        }

        // ------------------------------------------------------------ 界面

        private void BuildUi()
        {
            // 顶部：站点 + 关键词
            var top = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(10, 8, 10, 4) };
            var lblSite = new Label { Text = "站点：", AutoSize = true, Location = new Point(12, 14) };
            cboSite = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(60, 10),
                Width = 150,
            };
            cboSite.Items.AddRange(new object[] { "番茄小说", "笔趣阁（移动版·快）", "笔趣阁（PC版·慢）" });
            cboSite.SelectedIndex = 0;

            var lblKw = new Label { Text = "书名 / 链接：", AutoSize = true, Location = new Point(224, 14) };
            txtKeyword = new TextBox { Location = new Point(310, 10), Width = 230 };
            txtKeyword.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(); } };

            btnSearch = new Button { Text = "搜索", Location = new Point(620, 9), Width = 74, Height = 26 };
            btnSearch.Click += (s, e) => DoSearch();

            btnLoad = new Button { Text = "载入目录", Location = new Point(700, 9), Width = 88, Height = 26 };
            btnLoad.Click += (s, e) => DoLoadBook();

            btnReload = new Button { Text = "刷新目录", Location = new Point(794, 9), Width = 88, Height = 26 };
            btnReload.Click += (s, e) => DoLoadBook(true);

            btnWebSearch = new Button { Text = "浏览器搜索", Location = new Point(886, 9), Width = 96, Height = 26 };
            btnWebSearch.Click += (s, e) => DoWebSearch();
            new ToolTip().SetToolTip(btnWebSearch,
                "番茄站没有公开的中文搜索接口。\n点这里会用默认浏览器打开番茄官网的搜索页，\n找到书后把地址栏链接复制回来粘到输入框即可。");

            // 保存到那一行右侧还空着，放“设置”（并发数/请求间隔/重试轮数）
            btnSettings = new Button { Text = "设置", Location = new Point(860, 43), Width = 84, Height = 26 };
            btnSettings.Click += (s, e) => DoSettings();
            new ToolTip().SetToolTip(btnSettings,
                "并发线程数、请求间隔、失败重试轮数。\n" +
                "存成 exe 同目录的 settings.ini，也可以手改。");

            var lblOut = new Label { Text = "保存到：", AutoSize = true, Location = new Point(12, 48) };
            txtOutput = new TextBox
            {
                Location = new Point(80, 44),
                Width = 520,
                Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载"),
            };
            btnBrowse = new Button { Text = "浏览…", Location = new Point(608, 43), Width = 84, Height = 26 };
            btnBrowse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "选择小说保存目录";
                    if (d.ShowDialog() == DialogResult.OK) txtOutput.Text = d.SelectedPath;
                }
            };

            chkOffline = new CheckBox
            {
                Text = "离线模式（遍历目录时把正文全部留在内存，之后下载秒完成、不再联网）",
                Location = new Point(700, 45),
                Width = 430,
                Height = 22,
            };
            var tipOff = new ToolTip();
            tipOff.SetToolTip(chkOffline,
                "勾上后：点「载入目录」会真的遍历整本站点（约 1~2 分钟），\n" +
                "期间把每一章正文都抓下来存在内存里；之后点下载就直接写文件，\n" +
                "不再发生任何网络请求（下 700 章只需几秒）。\n" +
                "不勾：目录能秒开（走本地缓存），但下载时要再联网抓一遍正文。");

            top.Controls.AddRange(new Control[] { lblSite, cboSite, lblKw, txtKeyword, btnSearch, btnLoad, btnReload, btnWebSearch, lblOut, txtOutput, btnBrowse, btnSettings, chkOffline });
            Controls.Add(top);

            // 底部：进度条 + 状态 + 日志
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 218, Padding = new Padding(10, 4, 10, 8) };

            progress = new ProgressBar { Dock = DockStyle.Top, Height = 18 };
            lblStatus = new Label { Dock = DockStyle.Top, Height = 22, Text = "就绪。", TextAlign = ContentAlignment.MiddleLeft };

            txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Consolas", 8.5F),
            };

            var logBox = new Panel { Dock = DockStyle.Fill };
            logBox.Controls.Add(txtLog);
            bottom.Controls.Add(logBox);
            bottom.Controls.Add(lblStatus);
            bottom.Controls.Add(progress);
            Controls.Add(bottom);

            // 中间：书籍列表 + 章节列表
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                Panel1MinSize = 80,
                Panel2MinSize = 100,
            };
            _split = split;
            // SplitContainer 在窗体还没完成布局时宽度可能只有默认值，
            // 此时直接给 SplitterDistance 会抛 “必须在 Panel1MinSize 和 Width - Panel2MinSize 之间”，
            // 所以这里每次都按当前宽度夹一次，并在窗口尺寸变化时重新夹。
            split.SizeChanged += (s, e) => ClampSplitter();
            split.HandleCreated += (s, e) => ClampSplitter();
            split.SplitterMoved += (s, e) => { _preferredSplit = split.SplitterDistance; };

            lstBooks = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
            };
            lstBooks.Columns.Add("书名", 300);
            lstBooks.Columns.Add("作者", 140);
            lstBooks.Columns.Add("分类", 100);
            lstBooks.Columns.Add("备注", 380);
            lstBooks.DoubleClick += (s, e) => DoLoadBook();

            var bookBar = new Panel { Dock = DockStyle.Top, Height = 24 };
            bookBar.Controls.Add(new Label { Text = "搜索结果（双击载入目录）", Dock = DockStyle.Fill, ForeColor = Color.DimGray });
            split.Panel1.Controls.Add(lstBooks);
            split.Panel1.Controls.Add(bookBar);

            lstChapters = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                MultiSelect = true,
                HideSelection = false,
            };
            lstChapters.Columns.Add("章节", 620);
            lstChapters.Columns.Add("章节ID", 180);
            lstChapters.ItemChecked += (s, e) => UpdateSelLabel();

            var chapBar = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(0, 3, 0, 3) };
            btnSelectAll = new Button { Text = "全选", Width = 60, Height = 25, Location = new Point(0, 3) };
            btnSelectNone = new Button { Text = "全不选", Width = 66, Height = 25, Location = new Point(64, 3) };
            btnInvert = new Button { Text = "反选", Width = 60, Height = 25, Location = new Point(134, 3) };
            btnDownloadAll = new Button { Text = "下载全部章节", Width = 110, Height = 25, Location = new Point(200, 3) };
            btnDownloadAll.Click += (s, e) => DoDownloadAll();
            btnDownload = new Button { Text = "下载选中章节", Width = 110, Height = 25, Location = new Point(314, 3) };
            btnCancel = new Button { Text = "取消", Width = 60, Height = 25, Location = new Point(428, 3), Enabled = false };
            btnOpenFolder = new Button { Text = "打开保存目录", Width = 106, Height = 25, Location = new Point(492, 3) };
            lblSel = new Label
            {
                Text = "（还没载入目录）",
                AutoSize = true,
                Location = new Point(606, 8),
                ForeColor = Color.DimGray,
            };
            btnOfficial = new Button { Text = "用官方工具下载（番茄）", Width = 170, Height = 25, Location = new Point(700, 3) };
            btnCoreSetup = new Button { Text = "设置/安装番茄核心", Width = 140, Height = 25, Location = new Point(876, 3) };
            btnSelectAll.Click += (s, e) => SetAllChecks(true);
            btnSelectNone.Click += (s, e) => SetAllChecks(false);
            btnInvert.Click += (s, e) => InvertChecks();
            btnDownload.Click += (s, e) => DoDownload();
            btnCancel.Click += (s, e) => { _cancel = true; Log("已请求取消，正在收尾…"); };
            btnOpenFolder.Click += (s, e) => OpenFolder();
            btnOfficial.Click += (s, e) => DoOfficialDownload();
            btnCoreSetup.Click += (s, e) => DoConfigureCore();
            var tip = new ToolTip();
            tip.SetToolTip(btnOfficial,
                "调用原版 TomatoNovelDownloader 的本地 API 下载番茄小说。\n" +
                "它走官方接口 + 解密，正文干净完整（网页版有验证码风控和形近字替换）。\n" +
                "首次使用请先点右边「设置官方工具路径」。");
            tip.SetToolTip(btnCoreSetup, "指定原版 TomatoNovelDownloader 的 exe 位置（番茄下载靠它完成）");
            chapBar.Controls.AddRange(new Control[] { btnSelectAll, btnSelectNone, btnInvert, btnDownloadAll, btnDownload, btnCancel, btnOpenFolder, lblSel, btnOfficial, btnCoreSetup });
            split.Panel2.Controls.Add(lstChapters);
            split.Panel2.Controls.Add(chapBar);

            Controls.Add(split);
            split.BringToFront();
            top.BringToFront();
            bottom.BringToFront();

            // 等窗体尺寸确定后再设置分隔位置
            Shown += (s, e) => ClampSplitter();

            cboSite.SelectedIndexChanged += (s, e) => UpdateSiteHint();
            UpdateSiteHint();
            ApplySettings();
            Log("工具已就绪。");
            Log("取数后端：" + (Http.CurlAvailable ? "系统自带 curl（" + Http.CurlPath + "）" : "内置 .NET 请求（未找到 curl.exe）"));
            Log("当前设置：" + _settings + "（改这些点上面的「设置」按钮，或直接编辑 " + AppSettings.FileName + "）");
            Log("· 笔趣阁：直接输入中文书名搜索（例如：沧元图），双击结果载入目录后勾选章节下载。");
            Log("· 番茄小说：番茄没有公开的中文搜索接口，请点「浏览器搜索」去官网找到书，");
            Log("  再把书籍链接（.../page/数字）或 book_id 粘贴到输入框即可自动载入目录。");
            Log("  说明：番茄网页版正文有风控（验证码 / 200 字试读），正文下载可能受限。");
        }

        private void UpdateSiteHint()
        {
            bool fanqie = cboSite.SelectedIndex == 0;
            bool mobile = cboSite.SelectedIndex == 1;
            txtKeyword.Text = "";
            WatermarkExt.SetHint(txtKeyword,
                fanqie ? "粘贴番茄书籍链接或 book_id" : "输入中文书名，例如：牧神记", this);
            _site = fanqie ? (ISite)new FanqieSite()
                 : mobile ? (ISite)new BiqugaMobileSite()
                 : (ISite)new BiqugaSite();
            if (btnWebSearch != null) btnWebSearch.Enabled = fanqie;
            if (btnOfficial != null) btnOfficial.Enabled = fanqie;

            if (fanqie)
            {
                var core = new TomatoCore { ExePath = TomatoCore.GuessDefaultExePath() };
                bool bundled = core.ExePath != null && TomatoCore.BundledExe() != null &&
                               string.Equals(core.ExePath, TomatoCore.BundledExe(), StringComparison.OrdinalIgnoreCase);
                if (core.IsRunning())
                    Log("番茄：检测到番茄核心服务已在运行（" + core.BaseUrl + "），可直接点「用官方工具下载（番茄）」。");
                else if (core.ExePath == null)
                    Log("番茄：还没装番茄核心，请点「设置官方工具路径」选择 TomatoNovelDownloader 的 exe（会复制进本工具目录）。");
                else
                    Log("番茄：核心已就绪" + (bundled ? "（本工具自带，自包含）" : "（外部工具）") +
                        "：" + core.ExePath);
            }
        }

        // ------------------------------------------------------------ 交互

        private void DoSearch()
        {
            if (_busy) { BusyNotice(Text); return; }
            var kw = txtKeyword.Text.Trim();
            if (kw.Length == 0) { MessageBox.Show("请输入书名或链接"); return; }

            var site = _site;
            RunBackground("搜索中…", () =>
            {
                var list = site.Search(kw, Log);
                UiInvoke(() =>
                {
                    lstBooks.Items.Clear();
                    foreach (var b in list)
                    {
                        var it = new ListViewItem(b.Title);
                        it.SubItems.Add(b.Author);
                        it.SubItems.Add(b.Category);
                        it.SubItems.Add(b.Desc);
                        it.Tag = b;
                        lstBooks.Items.Add(it);
                    }
                    if (list.Count > 0) lstBooks.Items[0].Selected = true;
                });
                if (list.Count == 0)
                {
                    if (site.Name == "番茄小说")
                    {
                        Log("番茄没有公开的中文搜索接口。请点「浏览器搜索」去官网找书，再把链接或 book_id 粘回来。");
                        var r = MessageBox.Show(
                            "番茄小说不提供可用的中文搜索接口（官方搜索是浏览器里异步加载的）。\n\n" +
                            "要按中文书名找书，请点「浏览器搜索」按钮：\n" +
                            "它会打开番茄官网搜索页，你把找到的书籍链接粘回来即可。\n\n现在就去打开吗？",
                            "番茄站：改用浏览器搜索", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                        if (r == DialogResult.Yes) DoWebSearch();
                    }
                    else
                    {
                        Log("没有搜到这本书，换个书名或只输入书名关键字再试。");
                    }
                    return;
                }
                // 番茄站只有一条结果，直接载入目录
                if (list.Count == 1 && site.Name == "番茄小说")
                {
                    _currentBook = null;
                    DoLoadBookCore(list[0], site);
                }
            });
        }

        /// <summary>番茄站的中文搜索：没有公开接口，改为打开官网搜索页，再让用户把链接贴回来</summary>
        private void DoWebSearch()
        {
            var kw = txtKeyword.Text.Trim();
            var url = FanqieSite.SearchPageUrl(kw.Length > 0 ? kw : "");
            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception ex)
            {
                Log("打开浏览器失败：" + ex.Message);
                Clipboard.SetText(url);
                MessageBox.Show("没能自动打开浏览器，搜索页地址已复制到剪贴板：\n" + url,
                    "浏览器搜索", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Log("已用默认浏览器打开番茄搜索页：" + url);
            Log("步骤：1) 在网页里找到那本书并点进去  2) 复制地址栏链接（形如 .../page/7406592861791063064）  3) 粘贴到输入框，点「搜索」或「载入目录」");
            MessageBox.Show(
                "已打开番茄官网搜索页。\n\n" +
                "接下来：\n" +
                "1) 在网页里找到你要的书，点进书籍详情页\n" +
                "2) 复制地址栏的链接（形如 https://fanqienovel.com/page/7406592861791063064）\n" +
                "3) 回到本工具，把链接粘贴到输入框\n" +
                "4) 点「搜索」——工具会自动取出 book_id 并载入目录\n\n" +
                "（也可以直接粘贴 book_id 数字，或章节页链接）",
                "浏览器搜索 · 使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DoLoadBook(bool forceRefresh = false)
        {
            if (_busy) { BusyNotice(Text); return; }
            if (forceRefresh) Log("已选择“刷新目录”：忽略本地缓存，重新遍历站点。");

            if (lstBooks.SelectedItems.Count == 0)
            {
                // 番茄站允许直接用手输的 book_id
                if (_site.Name == "番茄小说")
                {
                    var list = _site.Search(txtKeyword.Text.Trim(), Log);
                    if (list.Count == 0) { MessageBox.Show("请先输入番茄 book_id 或分享链接"); return; }
                    var s = _site;
                    SetRefresh(s, forceRefresh);
                    RunBackground("载入目录中…", () => DoLoadBookCore(list[0], s));
                    return;
                }
                MessageBox.Show("请先在列表里选一本书");
                return;
            }
            var book = lstBooks.SelectedItems[0].Tag as BookInfo;
            var site = _site;
            SetRefresh(site, forceRefresh);
            RunBackground("载入目录中…", () => DoLoadBookCore(book, site));
        }

        /// <summary>把“强制刷新 / 离线模式”标志透传给站点实现</summary>
        private void SetRefresh(ISite site, bool force)
        {
            bool offline = chkOffline != null && chkOffline.Checked;
            var bq = site as BiqugaSite;
            if (bq != null)
            {
                bq.ForceRefresh = force || offline;
                bq.Offline = offline;
                bq.CrawlWorkers = _settings.BiqugaPcWorkers;
                if (offline && !_offlineHinted)
                {
                    _offlineHinted = true;
                    Log("离线模式已开启：载入目录会连正文一起抓下来，之后下载不再联网（下完可取消勾选）。");
                }
            }
            var bm = site as BiqugaMobileSite;
            if (bm != null)
            {
                bm.ForceRefresh = force || offline;
                bm.PrefetchText = offline;
                bm.Workers = offline ? _settings.BiqugaOfflineWorkers : _settings.BiqugaOnlineWorkers;
                if (offline && !_offlineHinted)
                {
                    _offlineHinted = true;
                    Log(string.Format("离线模式已开启：载入目录时会用 {0} 个线程并发把整本正文抓下来，之后下载不再联网"
                        + "（移动版下 1000 章约 10 分钟）。", bm.Workers));
                }
            }
            var fq = site as FanqieSite;
            if (fq != null) fq.ForceRefresh = force;
        }

        /// <summary>
        /// 把 settings.ini 里的值真正作用到取数层与站点实现上。
        /// 任何路径都要经过这里，否则改了设置不生效（历史上就吃过“改了没反应”的亏）。
        /// </summary>
        private void ApplySettings()
        {
            Http.Configure(_settings.MinDelayMs, _settings.MaxDelayMs);
            var bm = _site as BiqugaMobileSite;
            if (bm != null)
                bm.Workers = chkOffline != null && chkOffline.Checked
                    ? _settings.BiqugaOfflineWorkers : _settings.BiqugaOnlineWorkers;
            var bq = _site as BiqugaSite;
            if (bq != null) bq.CrawlWorkers = _settings.BiqugaPcWorkers;
        }

        /// <summary>设置对话框：并发数 / 请求间隔 / 重试轮数（改完立即生效并写盘）</summary>
        private void DoSettings()
        {
            var s = new AppSettings
            {
                BiqugaOfflineWorkers = _settings.BiqugaOfflineWorkers,
                BiqugaOnlineWorkers = _settings.BiqugaOnlineWorkers,
                BiqugaPcWorkers = _settings.BiqugaPcWorkers,
                MinDelayMs = _settings.MinDelayMs,
                MaxDelayMs = _settings.MaxDelayMs,
                CrawlTimeoutMinutes = _settings.CrawlTimeoutMinutes,
                RetryPasses = _settings.RetryPasses,
            };

            using (var dlg = new Form())
            {
                dlg.Text = "设置（并发与重试）";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(452, 312);
                dlg.Font = Font;
                dlg.ShowInTaskbar = false;

                var tip = new Label
                {
                    Text = "并发越大越快，但太大容易触发站点限速。8 是实测比较稳的值。\n" +
                           "改完立即生效；配置文件：" + AppSettings.DefaultPath,
                    Location = new Point(14, 10),
                    Size = new Size(424, 44),
                    ForeColor = Color.DimGray,
                };

                var nums = new NumericUpDown[5];
                string[] labels = { "移动版·离线模式并发", "移动版·普通下载并发", "PC 版并发", "每章请求间隔(毫秒)", "失败章节自动重试(轮)" };
                int[] values = { s.BiqugaOfflineWorkers, s.BiqugaOnlineWorkers, s.BiqugaPcWorkers, s.MinDelayMs, s.RetryPasses };
                int[] min = { 1, 1, 1, 0, 0 };
                int[] max = { 32, 32, 32, 5000, 3 };
                for (int i = 0; i < nums.Length; i++)
                {
                    dlg.Controls.Add(new Label { Text = labels[i], Location = new Point(16, 66 + i * 30), Size = new Size(190, 22), TextAlign = ContentAlignment.MiddleLeft });
                    nums[i] = new NumericUpDown
                    {
                        Location = new Point(212, 66 + i * 30),
                        Width = 80,
                        Minimum = min[i],
                        Maximum = max[i],
                        Value = values[i],
                    };
                    dlg.Controls.Add(nums[i]);
                }
                dlg.Controls.Add(new Label
                {
                    Text = "（间隔取 1~2 倍随机值，避免固定节奏被识别）",
                    Location = new Point(300, 156),
                    Size = new Size(150, 40),
                    ForeColor = Color.Gray,
                });

                var ok = new Button { Text = "保存", Location = new Point(262, 262), Width = 84, Height = 28, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", Location = new Point(352, 262), Width = 84, Height = 28, DialogResult = DialogResult.Cancel };
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                s.BiqugaOfflineWorkers = (int)nums[0].Value;
                s.BiqugaOnlineWorkers = (int)nums[1].Value;
                s.BiqugaPcWorkers = (int)nums[2].Value;
                s.MinDelayMs = (int)nums[3].Value;
                s.MaxDelayMs = Math.Max(s.MinDelayMs, s.MinDelayMs * 2);
                s.RetryPasses = (int)nums[4].Value;
            }

            _settings = s;
            ApplySettings();
            try
            {
                _settings.Save();
                Log("设置已保存：" + _settings);
            }
            catch (Exception ex)
            {
                Log("设置保存失败（本次仍然生效）：" + ex.Message);
                MessageBox.Show(ex.Message + "\n\n本次修改已经生效，只是没能写进配置文件。",
                    "保存设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool _offlineHinted;
        private DateTime _dlStart;

        /// <summary>把时长格式化成 3分20秒 这样的形式</summary>
        private static string Fmt(TimeSpan t)
        {
            if (t.TotalHours >= 1) return string.Format("{0}小时{1}分", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format("{0}分{1}秒", (int)t.TotalMinutes, t.Seconds);
            return string.Format("{0:F0}秒", t.TotalSeconds);
        }

        private void DoLoadBookCore(BookInfo item, ISite site)
        {
            var full = site.LoadBook(item, Log);
            _currentBook = full;
            UiInvoke(() =>
            {
                lstChapters.BeginUpdate();
                lstChapters.Items.Clear();
                foreach (var c in full.Chapters)
                {
                    var it = new ListViewItem(c.Title) { Tag = c, Checked = true };
                    it.SubItems.Add(c.Id);
                    if (c.IsVolume)
                    {
                        it.Checked = false;
                        it.ForeColor = Color.FromArgb(0, 90, 160);
                        it.Font = new Font(lstChapters.Font, FontStyle.Bold);
                    }
                    lstChapters.Items.Add(it);
                }
                lstChapters.EndUpdate();
                lblStatus.Text = full.ToString();
                UpdateSelLabel();
            });
            Log("目录已载入：" + full.ToString());

            // 目录刚载入完（尤其离线模式下正文都在内存里），主动问一句，避免用户以为“爬完就没事了”
            bool offlineNow = chkOffline != null && chkOffline.Checked;
            if (lstChapters.Items.Count > 0)
            {
                var ask = MessageBox.Show(
                    string.Format("目录已载入：{0}\n\n" +
                        "⚠ 注意：载入目录≠下载，还要点下载按钮才会生成 txt。\n\n" +
                        "现在要下载全部 {1} 章吗？\n" +
                        "· 点「是」＝ 立即开始下载全部\n" +
                        "· 点「否」＝ 先自己勾选章节，再点右下角「下载选中章节」\n\n{2}",
                        full.ToString(), full.Chapters.Count,
                        offlineNow
                            ? "（离线模式：正文已在内存里，下载会很快，不再联网）"
                            : "（当前没开离线模式：下载时还要联网抓正文，1067 章大约要 30~60 分钟；\n　 想快就先关掉弹窗、勾上「离线模式」再点「刷新目录」）"),
                    "目录已载入 —— 还需要点下载", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ask == DialogResult.Yes) DoDownload();
            }
        }

        private void SetAllChecks(bool value)
        {
            lstChapters.BeginUpdate();
            foreach (ListViewItem it in lstChapters.Items)
            {
                var c = it.Tag as ChapterInfo;
                if (c != null && c.IsVolume) { it.Checked = false; continue; }
                it.Checked = value;
            }
            lstChapters.EndUpdate();
            UpdateSelLabel();
        }

        /// <summary>常驻显示“已勾选 N 章”，避免用户以为载入目录就等于下载了</summary>
        private void UpdateSelLabel()
        {
            if (lblSel == null) return;
            int total = 0, sel = 0;
            foreach (ListViewItem it in lstChapters.Items)
            {
                var c = it.Tag as ChapterInfo;
                if (c == null || c.IsVolume) continue;
                total++;
                if (it.Checked) sel++;
            }
            lblSel.Text = total == 0
                ? "（还没载入目录）"
                : string.Format("共 {0} 章，已勾选 {1} 章 → 点右边按钮才会真正下载", total, sel);
            lblSel.ForeColor = sel > 0 ? Color.FromArgb(0, 100, 0) : Color.DimGray;
            if (btnDownload != null) btnDownload.Enabled = sel > 0 && !_busy;
            if (btnDownloadAll != null) btnDownloadAll.Enabled = total > 0 && !_busy;
        }

        /// <summary>一键下载全部章节（省得先全选再点下载）</summary>
        private void DoDownloadAll()
        {
            if (_busy) { BusyNotice(Text); return; }
            SetAllChecks(true);
            DoDownload();
        }

        private void InvertChecks()
        {
            lstChapters.BeginUpdate();
            foreach (ListViewItem it in lstChapters.Items)
            {
                var c = it.Tag as ChapterInfo;
                if (c != null && c.IsVolume) { it.Checked = false; continue; }
                it.Checked = !it.Checked;
            }
            lstChapters.EndUpdate();
            UpdateSelLabel();
        }

        // ------------------------------------------------------------ 番茄：调用官方工具

        /// <summary>设置/安装番茄核心：把它复制进本工具目录，实现自包含</summary>
        private void DoConfigureCore()
        {
            var bundled = TomatoCore.BundledExe();
            if (bundled != null)
            {
                var r = MessageBox.Show(
                    "本工具已经自带番茄核心（自包含，不再需要外面的工具目录）：\n" + bundled + "\n\n" +
                    "番茄的下载文件会保存在：\n" + Path.Combine(TomatoCore.BundledDir, "下载") + "\n\n" +
                    "要继续用这个，直接点「否」；想换成别的副本就点「是」。",
                    "番茄核心已就位", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.No) return;
            }

            var current = TomatoCore.GuessDefaultExePath();
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择原版 TomatoNovelDownloader 的 exe（会被复制进本工具目录）";
                dlg.Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                try
                {
                    if (!string.IsNullOrEmpty(current))
                    {
                        dlg.InitialDirectory = Path.GetDirectoryName(current);
                        dlg.FileName = Path.GetFileName(current);
                    }
                }
                catch { }
                if (dlg.ShowDialog() != DialogResult.OK) return;

                try
                {
                    var installed = TomatoCore.InstallBundled(dlg.FileName, Log);
                    TomatoCore.SaveExePath(installed);
                    Log("番茄核心已装进本工具目录，之后不再依赖原来的位置。");
                    MessageBox.Show(
                        "番茄核心已装好，本工具现在是自包含的。\n\n" +
                        "核心程序：" + installed + "\n" +
                        "下载输出：" + Path.Combine(TomatoCore.BundledDir, "下载") + "\n\n" +
                        "之后点「用官方工具下载（番茄）」，它会自动启动这个自带核心。",
                        "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    Log("安装番茄核心失败：" + ex.Message);
                    MessageBox.Show("安装失败：" + ex.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        /// <summary>用原版工具下载当前番茄书籍（拿干净完整正文）</summary>
        private void DoOfficialDownload()
        {
            if (_busy) { BusyNotice(Text); return; }

            // 确定 book_id：优先用已载入的书，其次从输入框解析
            string bookId = null;
            string title = null;
            if (_currentBook != null && _currentBook.Site == "fanqie" && !string.IsNullOrEmpty(_currentBook.BookId))
            {
                bookId = _currentBook.BookId;
                title = _currentBook.Title;
            }
            else
            {
                bookId = FanqieSite.ExtractBookId(txtKeyword.Text);
                if (bookId == null)
                {
                    MessageBox.Show("请先切到「番茄小说」站点，粘贴书籍链接或 book_id，\n" +
                        "或者先载入一本书的目录，再点这个按钮。",
                        "用官方工具下载", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            var core = new TomatoCore { ExePath = TomatoCore.GuessDefaultExePath() };
            var bid = bookId;
            RunBackground("调用官方工具下载中…", () => RunOfficialDownload(core, bid, title));
        }

        private void RunOfficialDownload(TomatoCore core, string bookId, string knownTitle)
        {
            bool bundled = TomatoCore.BundledExe() != null &&
                           string.Equals(core.ExePath, TomatoCore.BundledExe(), StringComparison.OrdinalIgnoreCase);
            Log(bundled
                ? "使用本工具自带的番茄核心：" + core.ExePath
                : "使用外部番茄工具：" + core.ExePath);

            core.EnsureRunning(Log);

            long jobId = core.SubmitJob(bookId, Log);
            UiInvoke(() => { progress.Minimum = 0; progress.Maximum = 100; progress.Value = 0; });

            var lastText = "";
            bool canceled = false;
            var final = core.WaitJob(jobId, st =>
            {
                var text = string.Format("官方工具进度：{0}/{1} 章", st.SavedChapters, st.TotalChapters);
                if (text != lastText)
                {
                    lastText = text;
                    UiInvoke(() =>
                    {
                        lblStatus.Text = text + (string.IsNullOrEmpty(st.Title) ? "" : "  《" + st.Title + "》");
                        if (st.TotalChapters > 0)
                        {
                            progress.Maximum = st.TotalChapters;
                            progress.Value = Math.Min(st.SavedChapters, st.TotalChapters);
                        }
                    });
                }
            }, ref canceled);

            var title = !string.IsNullOrEmpty(final.Title) ? final.Title : knownTitle;
            Log(string.Format("任务 #{0} 状态：{1}　成功 {2}/{3} 章",
                jobId, final.State, final.SavedChapters, final.TotalChapters));
            if (!string.IsNullOrEmpty(final.Message)) Log("官方工具消息：" + final.Message);

            var saveDir = core.GetSaveDir();
            var file = core.FindOutputFile(title, saveDir);

            if (final.State == "done")
            {
                Log("=== 官方工具下载完成 ===");
                if (!string.IsNullOrEmpty(file))
                {
                    var fi = new FileInfo(file);
                    Log(string.Format("书名：{0}　作者：{1}", title, final.Author));
                    Log(string.Format("大小：{0:N0} 字节（{1:N1} MB），修改时间 {2}",
                        fi.Length, fi.Length / 1048576.0, fi.LastWriteTime));
                    Log(string.Format("章节：成功 {0}/{1} 章", final.SavedChapters, final.TotalChapters));
                    Log("保存位置：" + file);
                    _lastOfficialFile = file;
                    Log("（「打开保存目录」按钮现在会打开这个官方工具的目录）");
                }
                else
                {
                    Log("已完成，但没在 " + saveDir + " 里找到输出文件，请手动查看该目录。");
                    if (!string.IsNullOrEmpty(saveDir)) _lastOfficialFile = Path.Combine(saveDir, ".");
                }
            }
            else
            {
                Log("官方工具任务没有正常完成（状态：" + final.State + "）。");
                if (!string.IsNullOrEmpty(file)) Log("目录下最新文件：" + file);
            }

            core.ShutdownIfOwned();
        }

        /// <summary>正忙时点按钮：明确告诉用户，别静默吞掉</summary>
        private void BusyNotice(string action)
        {
            lblStatus.Text = "正在执行上一个任务…";
            MessageBox.Show("现在正在执行上一个任务（" + action + "），请等它结束再操作。\n\n" +
                "任务进度看下面的进度条和状态行；日志区会滚出每一章的明细。",
                "请稍候", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DoDownload()
        {
            if (_busy) { BusyNotice(Text); return; }
            if (_currentBook == null) { MessageBox.Show("请先载入一本书的目录"); return; }

            int totalRows = 0, volumeRows = 0;
            var picked = new List<ChapterInfo>();
            foreach (ListViewItem it in lstChapters.Items)
            {
                var c = it.Tag as ChapterInfo;
                if (c == null) continue;
                totalRows++;
                if (c.IsVolume) { volumeRows++; continue; }
                if (it.Checked) picked.Add(c);
            }
            Log(string.Format("选中情况：列表共 {0} 项（其中分卷标题 {1} 项），勾选 {2} 章", totalRows, volumeRows, picked.Count));

            if (picked.Count == 0)
            {
                MessageBox.Show("请先勾选要下载的章节。\n\n" +
                    string.Format("（当前列表 {0} 项，勾选 0 章）\n", totalRows) +
                    "如果列表本来就是空的，说明目录还没载入成功，请先点「载入目录」。",
                    "没有选中章节", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var book = _currentBook;
            var site = _site;
            var root = txtOutput.Text.Trim();
            if (root.Length == 0) root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
            var chapters = picked;

            RunBackground("下载中…", () => DownloadCore(site, book, chapters, root));
        }

        /// <summary>
        /// 界面「开始下载选中章节」的真正执行体（不含 UI 弹窗部分），
        /// 单独抽出来是为了让自动化测试能跑**和界面完全一样**的代码路径。
        /// </summary>
        internal void DownloadCore(ISite site, BookInfo book, List<ChapterInfo> chapters, string root)
        {
            string bookDir, txtPath;
            DownloadRunner.ResolvePaths(root, book.Title, out bookDir, out txtPath);
            Log("准备写入：" + txtPath);
            Log("（如果这个路径不是你想要的，请用上面的「浏览…」改「保存到」）");

            var runner = new DownloadRunner
            {
                Site = site,
                Book = book,
                Chapters = chapters,
                RootDir = root,
                Log = Log,
                RetryPasses = _settings.RetryPasses,
                IsCanceled = () => _cancel,
                OnProgress = (done, total) =>
                {
                        // 记录开始时间用于估算剩余时间
                        if (done == 0) _dlStart = DateTime.Now;
                        UiInvoke(() =>
                        {
                            progress.Minimum = 0;
                            progress.Maximum = total;
                            progress.Value = Math.Min(done, total);
                            if (done == 0 || _dlStart == default(DateTime))
                            {
                                lblStatus.Text = string.Format("开始下载 0/{0} 章…", total);
                                return;
                            }
                            var used = DateTime.Now - _dlStart;
                            string eta = "";
                            if (done > 0 && done < total)
                            {
                                var per = used.TotalSeconds / done;
                                var left = TimeSpan.FromSeconds(per * (total - done));
                                eta = string.Format("，预计还需 {0}", Fmt(left));
                            }
                            lblStatus.Text = string.Format("下载中 {0}/{1} 章　已用 {2}{3}　（每章约 {4:F1} 秒）",
                                done, total, Fmt(used), eta, done > 0 ? used.TotalSeconds / done : 0);
                        });
                },
            };
            runner.Run();

            UiInvoke(() =>
            {
                lblStatus.Text = string.Format("下载结束：成功 {0}，跳过 {1}，失败 {2}",
                    runner.Ok, runner.Skipped, runner.Failed);
            });
            Log("=== 下载结束 ===");
            Log(string.Format("成功 {0} 章，跳过 {1} 章（站点公告/空内容），失败 {2} 章",
                runner.Ok, runner.Skipped, runner.Failed));
            Log("保存位置：" + runner.OutputFile);
            if (runner.Failed > 0) Log("失败的章节可重新勾选后再下一次（已下载内容会覆盖为完整版本）。");
            if (!string.IsNullOrEmpty(runner.ReportFile)) Log("缺失章节明细：" + runner.ReportFile);
            try
            {
                var fi = new FileInfo(runner.OutputFile);
                Log(string.Format("文件大小：{0:N0} 字节，修改时间 {1}", fi.Length, fi.LastWriteTime));

                // 明确弹窗告知，避免“以为没生成”
                if (!SuppressDialogs)
                {
                    var miss = new StringBuilder();
                    if (runner.FailedChapters.Count > 0)
                    {
                        miss.AppendLine();
                        miss.AppendLine(string.Format("⚠ 有 {0} 章失败（网络/风控）：", runner.FailedChapters.Count));
                        for (int i = 0; i < runner.FailedChapters.Count && i < 5; i++)
                            miss.AppendLine("   · 第 " + runner.FailedChapters[i].Order + " 章 " + runner.FailedChapters[i].Title);
                        if (runner.FailedChapters.Count > 5) miss.AppendLine("   · …");
                        miss.AppendLine("   已自动重试过，还是失败；可重新勾选这几章再下一次。");
                    }
                    if (runner.SkippedChapters.Count > 0)
                    {
                        miss.AppendLine();
                        miss.AppendLine(string.Format("ℹ 有 {0} 章被跳过：站点这一章本身就是空的（“正在手打中，请稍等片刻”），",
                            runner.SkippedChapters.Count));
                        miss.AppendLine("   不是工具的 bug，换源也一样没有内容。");
                    }
                    if (!string.IsNullOrEmpty(runner.ReportFile))
                        miss.AppendLine().AppendLine("缺哪些章看这个文件：").AppendLine(runner.ReportFile);

                    MessageBox.Show(
                        string.Format("下载完成！\n\n成功 {0} 章，跳过 {1} 章，失败 {2} 章\n" +
                                      "文件大小：{3:N0} 字节（{4:N2} MB）\n\n文件位置：\n{5}\n{6}\n" +
                                      "点「打开保存目录」可直接打开所在文件夹。",
                            runner.Ok, runner.Skipped, runner.Failed, fi.Length, fi.Length / 1048576.0,
                            runner.OutputFile, miss.ToString()),
                        "下载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex) { Log("读取文件信息失败：" + ex.Message); }
        }

        /// <summary>自动化测试时置 true，避免弹窗卡住测试</summary>
        internal bool SuppressDialogs { get; set; }

        private void OpenFolder()
        {
            // 刚用官方工具下过番茄小说，就打开它的目录（书存在那边）
            if (!string.IsNullOrEmpty(_lastOfficialFile))
            {
                try
                {
                    var dir = File.Exists(_lastOfficialFile)
                        ? Path.GetDirectoryName(_lastOfficialFile)
                        : _lastOfficialFile;
                    if (Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                        Log("已打开官方工具的保存目录：" + dir);
                        return;
                    }
                }
                catch (Exception ex) { Log("打开官方工具目录失败：" + ex.Message); }
            }

            var root = txtOutput.Text.Trim();
            if (root.Length == 0) root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
            try
            {
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + root + "\"");
            }
            catch (Exception ex) { Log("打开目录失败：" + ex.Message); }
        }

        // ------------------------------------------------------------ 线程/Lo g

        private void RunBackground(string status, Action work)
        {
            if (_busy) { BusyNotice("上一个任务"); return; }
            _busy = true;
            _cancel = false;
            btnSearch.Enabled = btnLoad.Enabled = btnReload.Enabled = btnDownload.Enabled = false;
            if (btnDownloadAll != null) btnDownloadAll.Enabled = false;
            if (btnOfficial != null) btnOfficial.Enabled = false;
            btnCancel.Enabled = true;
            lblStatus.Text = status;
            var t = new Thread(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log("出错：" + ex.Message);
                    UiInvoke(() => MessageBox.Show(ex.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning));
                }
                finally
                {
                    UiInvoke(() =>
                    {
                        _busy = false;
                        btnSearch.Enabled = btnLoad.Enabled = btnReload.Enabled = true;
                        if (btnOfficial != null) btnOfficial.Enabled = cboSite.SelectedIndex == 0;
                        UpdateSelLabel();
                        btnCancel.Enabled = false;
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Log(string msg)
        {
            if (msg == null) return;
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
            UiInvoke(() =>
            {
                txtLog.AppendText(line);
                if (txtLog.TextLength > 400000) txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 200000);
            });
        }

        private void UiInvoke(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { /* 窗口关闭中 */ }
        }
    }

    /// <summary>给空文本框加上水印提示（同一控件只创建一个标签）</summary>
    internal static class WatermarkExt
    {
        private static readonly Dictionary<TextBox, Label> Hints = new Dictionary<TextBox, Label>();

        public static void SetHint(TextBox box, string text, Form owner)
        {
            Label lbl;
            if (!Hints.TryGetValue(box, out lbl))
            {
                lbl = new Label
                {
                    ForeColor = Color.Gray,
                    BackColor = Color.White,
                    AutoSize = true,
                    Cursor = Cursors.IBeam,
                };
                lbl.Click += (s, e) => box.Focus();
                box.TextChanged += (s, e) => lbl.Visible = box.Text.Length == 0;
                box.GotFocus += (s, e) => lbl.Visible = false;
                box.LostFocus += (s, e) => lbl.Visible = box.Text.Length == 0;
                owner.Controls.Add(lbl);
                Hints[box] = lbl;
            }
            lbl.Text = text;
            lbl.Location = new Point(box.Left + 5, box.Top + 5);
            lbl.Visible = box.Text.Length == 0;
            lbl.BringToFront();
        }
    }
}
