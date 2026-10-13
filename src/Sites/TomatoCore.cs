using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace TomatoBiquga
{
    /// <summary>
    /// 对接原版 TomatoNovelDownloader 的本地 Web API。
    ///
    /// 逆向得到的结论：
    /// - 原工具 `--server` 模式会在 127.0.0.1:18423 起一个 axum 服务（无需密码）
    /// - POST /api/jobs  {"book_id":"..."}  → 建下载任务，返回 {"id":1,"state":"queued"}
    /// - GET  /api/jobs                     → 任务列表（含 progress.saved_chapters / chapter_total）
    /// - GET  /api/history                  → 下载历史
    /// - GET  /api/status                   → 状态（含 save_dir / version / locked）
    /// 官方接口 + 解密都在它内部完成，所以正文是干净完整的（不像网页版有风控和形近字替换）。
    ///
    /// 注意：它的 CLI `--update <book_id>` 只能更新“已下载过”的书，
    /// 首次下载必须走 Web UI/TUI，所以这里统一走本地 API。
    /// </summary>
    public class TomatoCore
    {
        public const int DefaultPort = 18423;
        public string Host = "127.0.0.1";
        public int Port = DefaultPort;
        public string ExePath;
        public string DataDir;          // 为空则用 exe 所在目录

        private Process _ownedProcess;  // 由本工具启动的服务进程（退出时清理）

        public string BaseUrl { get { return string.Format("http://{0}:{1}", Host, Port); } }

        /// <summary>配置文件路径（记录原工具的 exe 路径）</summary>
        public static string ConfigPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fanqie-core.ini"); }
        }

        /// <summary>读取上次保存的原工具 exe 路径</summary>
        public static string LoadSavedExePath()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    foreach (var line in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                    {
                        var s = line.Trim();
                        if (s.StartsWith("exe=", StringComparison.OrdinalIgnoreCase))
                            return s.Substring(4).Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        public static void SaveExePath(string path)
        {
            try
            {
                File.WriteAllText(ConfigPath,
                    "# 原版 TomatoNovelDownloader 的 exe 路径（本工具会用它来下载番茄小说）" + Environment.NewLine +
                    "exe=" + (path ?? "") + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>本工具自带的番茄核心目录（自带 = 自包含，不依赖外面那个工具目录）</summary>
        public static string BundledDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fanqie-core"); }
        }

        /// <summary>自带的番茄核心 exe（存在就优先用它）</summary>
        public static string BundledExe()
        {
            try
            {
                var dir = BundledDir;
                if (!Directory.Exists(dir)) return null;
                var files = Directory.GetFiles(dir, "*.exe");
                foreach (var f in files)
                {
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    if (name.Contains("tomatonovel") || name.Contains("fanqie")) return f;
                }
                return files.Length > 0 ? files[0] : null;
            }
            catch { return null; }
        }

        /// <summary>找一个可用的番茄核心 exe：自带副本优先 → 用户指定过 → 原来的工具目录</summary>
        public static string GuessDefaultExePath()
        {
            var bundled = BundledExe();
            if (bundled != null) return bundled;

            var candidates = new List<string>();
            var saved = LoadSavedExePath();
            if (!string.IsNullOrEmpty(saved)) candidates.Add(saved);

            var here = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                foreach (var f in Directory.GetFiles(here, "TomatoNovelDownloader*.exe")) candidates.Add(f);
            }
            catch { }

            foreach (var c in candidates)
            {
                try { if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c; }
                catch { }
            }
            return null;
        }

        // ---------------------------------------------------------------- 版本检测

        /// <summary>
        /// 已知的最新版本（内置，作为"联网查不到"时的兜底）。
        ///
        /// 为什么要兜底：这个工具的更新地址和 GitHub 在国内经常连不上，
        /// 如果只在联网成功时才提示，用户就永远看不到提示了。
        /// 内置一个常量至少能回答"你手上这份是不是旧的"。
        ///
        /// ★ 更新这里的方式：看到新版发布就改这个字符串。
        /// </summary>
        public const string KnownLatestVersion = "2.4.15";

        /// <summary>从 exe 里读出它的版本号。读不出返回 null（不瞎猜）。</summary>
        public static string VersionOf(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            try
            {
                if (!File.Exists(exePath)) return null;

                // 首选 PE 版本资源（最可靠）
                var fv = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
                if (!string.IsNullOrEmpty(fv) && Regex.IsMatch(fv, @"\d+\.\d+"))
                    return Regex.Match(fv, @"\d+(\.\d+)+").Value;

                // 退一步：从文件名里抠（发布名是 TomatoNovelDownloader-Win64-v2.4.15.exe）
                var name = Path.GetFileName(exePath);
                var m = Regex.Match(name, @"[vV]?(\d+\.\d+(?:\.\d+)*)");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// <summary>官方发布页（给用户点过去下载用）</summary>
        public const string ReleasePage = "https://github.com/zhongbai2333/Tomato-Novel-Downloader/releases/latest";

        /// <summary>
        /// 联网查最新版本号。查不到返回 null（调用方应退回 <see cref="KnownLatestVersion"/>）。
        /// 只读 GitHub 的公开 releases 接口，**不做任何自动下载/自动替换**。
        /// </summary>
        public static string QueryLatestVersion(int timeoutSeconds)
        {
            try
            {
                var json = Http.GetWithTimeout(
                    "https://api.github.com/repos/zhongbai2333/Tomato-Novel-Downloader/releases/latest",
                    timeoutSeconds);
                if (string.IsNullOrEmpty(json)) return null;

                // 复用自带更新那套 JSON 取值，不引第二个实现
                var tag = UpdateChecker.JsonStr(json, "tag_name");
                if (string.IsNullOrEmpty(tag)) return null;
                var v = UpdateChecker.Normalize(tag);
                return string.IsNullOrEmpty(v) ? null : v;
            }
            catch { return null; }
        }

        /// <summary>
        /// remote 是否比 local 新。
        ///
        /// ★ 复用 <see cref="UpdateChecker.CompareVersions"/>，**不在这里另写一套** ——
        ///   版本比较是最容易"看着对其实错"的地方（"2.10" 按字符串比会小于 "2.9"），
        ///   项目里已经有一份经过测试的实现；两份实现迟早会不一致。
        /// </summary>
        public static bool IsNewer(string remote, string local)
        {
            if (string.IsNullOrEmpty(remote)) return false;
            if (string.IsNullOrEmpty(local)) return true;      // 本地读不出 → 当作需要更新，提示一下无妨
            return UpdateChecker.CompareVersions(remote, local) > 0;
        }

        /// <summary>
        /// 把番茄核心“装进”本工具目录：拷贝 exe + 写一份自己的配置（输出到 fanqie-core\下载）。
        /// 返回装好后的 exe 路径。
        /// </summary>
        public static string InstallBundled(string sourceExe, Action<string> log)
        {
            if (string.IsNullOrEmpty(sourceExe))
            {
                var have = BundledExe();
                if (have != null) return have;
                throw new Exception("没有指定番茄核心 exe 的位置，也没找到已装好的副本。");
            }
            if (!File.Exists(sourceExe)) throw new Exception("找不到文件：" + sourceExe);

            var dir = BundledDir;
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "TomatoNovelDownloader.exe");
            if (log != null) log("正在把番茄核心复制到本工具目录：" + dir);

            try
            {
                File.Copy(sourceExe, target, true);
            }
            catch (Exception ex)
            {
                throw new Exception("复制失败：" + ex.Message + "（如果它正在运行，先关掉再试）");
            }

            // 写一份我们自己的配置，把输出统一放到 fanqie-core\下载
            var cfg = Path.Combine(dir, "config.yml");
            var savePath = Path.Combine(dir, "下载").Replace('\\', '/');
            try
            {
                if (File.Exists(cfg))
                {
                    var text = File.ReadAllText(cfg, Encoding.UTF8);
                    text = System.Text.RegularExpressions.Regex.Replace(text,
                        @"(?m)^save_path:.*$", "save_path: '" + savePath + "'");
                    File.WriteAllText(cfg, text, new UTF8Encoding(false));
                }
                else
                {
                    File.WriteAllText(cfg,
                        "# 本工具自动生成的番茄核心配置\n" +
                        "novel_format: txt\n" +
                        "save_path: '" + savePath + "'\n" +
                        "use_official_api: true\n", new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("写配置失败（不影响下载，输出会落在核心目录下）：" + ex.Message);
            }

            var outDir = Path.Combine(dir, "下载");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            if (log != null) log("番茄核心已就位：" + target);
            return target;
        }

        // ------------------------------------------------------------ HTTP

        private string Get(string path, int timeoutSec = 15)
        {
            var req = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
            req.Method = "GET";
            req.Timeout = timeoutSec * 1000;
            req.ReadWriteTimeout = timeoutSec * 1000;
            req.KeepAlive = false;
            req.Proxy = null;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var sr = new StreamReader(s, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private string PostJson(string path, string json, int timeoutSec = 30)
        {
            var req = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
            req.Method = "POST";
            req.Timeout = timeoutSec * 1000;
            req.ReadWriteTimeout = timeoutSec * 1000;
            req.KeepAlive = false;
            req.Proxy = null;
            req.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(json);
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var sr = new StreamReader(s, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static Dictionary<string, object> Parse(string json)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return ser.Deserialize<Dictionary<string, object>>(json);
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return "";
        }

        // ------------------------------------------------------------ 状态

        /// <summary>服务是否已经在跑</summary>
        public bool IsRunning()
        {
            try
            {
                var d = Parse(Get("/api/status", 5));
                return d != null && d.ContainsKey("version");
            }
            catch { return false; }
        }

        public string StatusJson()
        {
            try { return Get("/api/status", 8); }
            catch (Exception ex) { return "读取失败：" + ex.Message; }
        }

        /// <summary>确保服务在跑：没跑就用原工具 exe 起一个（记下来，退出时清理）</summary>
        public void EnsureRunning(Action<string> log)
        {
            if (IsRunning())
            {
                if (log != null) log("已检测到番茄官方工具的服务在运行（" + BaseUrl + "）");
                return;
            }
            if (string.IsNullOrEmpty(ExePath) || !File.Exists(ExePath))
                throw new Exception("没找到原版 TomatoNovelDownloader 的 exe。请点「设置官方工具路径」指定它。");

            if (log != null) log("正在启动官方工具的服务模式：" + ExePath);
            var workDir = string.IsNullOrEmpty(DataDir) ? Path.GetDirectoryName(ExePath) : DataDir;
            var psi = new ProcessStartInfo(ExePath, "--server")
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            _ownedProcess = Process.Start(psi);
            // 把输出读掉，避免管道写满卡住子进程
            try { _ownedProcess.BeginOutputReadLine(); _ownedProcess.BeginErrorReadLine(); } catch { }

            for (int i = 0; i < 40; i++)   // 最多等 20 秒
            {
                Thread.Sleep(500);
                if (_ownedProcess.HasExited)
                    throw new Exception("官方工具启动后立即退出了（ExitCode=" + _ownedProcess.ExitCode +
                        "）。可能端口被占用，或它需要交互式界面。");
                if (IsRunning())
                {
                    if (log != null) log("服务已就绪（" + BaseUrl + "）");
                    return;
                }
            }
            throw new Exception("等待官方工具服务超时（20 秒）。可以手动运行它 --server 模式后在设置里填端口。");
        }

        /// <summary>关掉由本工具启动的服务（用户自己开的不动）</summary>
        public void ShutdownIfOwned()
        {
            try
            {
                if (_ownedProcess != null && !_ownedProcess.HasExited)
                {
                    _ownedProcess.Kill();
                    _ownedProcess.WaitForExit(3000);
                }
            }
            catch { }
            finally { _ownedProcess = null; }
        }

        // ------------------------------------------------------------ 下载

        /// <summary>提交下载任务，返回任务 id</summary>
        public long SubmitJob(string bookId, Action<string> log)
        {
            if (string.IsNullOrEmpty(bookId)) throw new Exception("book_id 为空");
            var body = "{\"book_id\":\"" + bookId.Replace("\"", "") + "\"}";
            var resp = PostJson("/api/jobs", body);
            var d = Parse(resp);
            if (d == null || !d.ContainsKey("id"))
                throw new Exception("提交任务失败，返回：" + resp);
            long id = Convert.ToInt64(d["id"]);
            if (log != null) log(string.Format("已提交下载任务 #{0}（book_id={1}）", id, bookId));
            return id;
        }

        public class JobState
        {
            public long Id;
            public string State = "";       // queued / running / done / failed / canceled
            public string Title = "";
            public string Author = "";
            public string Message = "";
            public int SavedChapters;
            public int TotalChapters;
            public bool Exists;
        }

        /// <summary>查某个任务的状态</summary>
        public JobState GetJob(long id)
        {
            var d = Parse(Get("/api/jobs", 15));
            var items = d != null && d.ContainsKey("items") ? d["items"] as System.Collections.IEnumerable : null;
            if (items == null) return new JobState { Id = id, Exists = false };
            foreach (var it in items)
            {
                var job = it as Dictionary<string, object>;
                if (job == null) continue;
                object idv;
                if (!job.TryGetValue("id", out idv)) continue;
                if (Convert.ToInt64(idv) != id) continue;

                var st = new JobState
                {
                    Id = id,
                    Exists = true,
                    State = Str(job, "state"),
                    Title = Str(job, "title"),
                    Author = Str(job, "author"),
                    Message = Str(job, "message"),
                };
                object progv;
                if (job.TryGetValue("progress", out progv))
                {
                    var prog = progv as Dictionary<string, object>;
                    if (prog != null)
                    {
                        object v;
                        if (prog.TryGetValue("saved_chapters", out v) && v != null) st.SavedChapters = Convert.ToInt32(v);
                        if (prog.TryGetValue("chapter_total", out v) && v != null) st.TotalChapters = Convert.ToInt32(v);
                    }
                }
                return st;
            }
            return new JobState { Id = id, Exists = false };
        }

        /// <summary>等任务结束，期间回调进度</summary>
        public JobState WaitJob(long id, Action<JobState> onProgress, ref bool cancel, int timeoutSeconds = 3600)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                if (cancel) return new JobState { Id = id, State = "canceled", Exists = true };
                JobState st;
                try { st = GetJob(id); }
                catch { Thread.Sleep(1500); continue; }

                if (onProgress != null) onProgress(st);
                if (!st.Exists) return st;
                if (st.State == "done" || st.State == "failed" || st.State == "canceled") return st;
                Thread.Sleep(1500);
            }
            return new JobState { Id = id, State = "timeout", Exists = true };
        }

        /// <summary>取服务端 save_dir（输出文件就在这个目录）</summary>
        public string GetSaveDir()
        {
            try
            {
                var d = Parse(Get("/api/status", 8));
                var s = Str(d, "save_dir");
                if (!string.IsNullOrEmpty(s)) return s;
            }
            catch { }
            if (!string.IsNullOrEmpty(DataDir)) return DataDir;
            if (!string.IsNullOrEmpty(ExePath)) return Path.GetDirectoryName(ExePath);
            return null;
        }

        /// <summary>在 save_dir 下找这本书的输出文件（按书名/最近修改时间匹配）</summary>
        public string FindOutputFile(string title, string saveDir)
        {
            try
            {
                if (string.IsNullOrEmpty(saveDir) || !Directory.Exists(saveDir)) return null;
                var files = Directory.GetFiles(saveDir, "*.txt");
                if (files.Length == 0) return null;

                // 优先精确同名的
                if (!string.IsNullOrEmpty(title))
                {
                    var exact = Path.Combine(saveDir, Http.SafeFileName(title) + ".txt");
                    if (File.Exists(exact)) return exact;
                }
                // 其次：文件名包含书名关键字
                if (!string.IsNullOrEmpty(title))
                {
                    foreach (var f in files)
                        if (Path.GetFileNameWithoutExtension(f).Contains(title)) return f;
                }
                // 兜底：最近修改的那个
                string newest = null;
                DateTime newestTime = DateTime.MinValue;
                foreach (var f in files)
                {
                    var t = File.GetLastWriteTime(f);
                    if (t > newestTime) { newestTime = t; newest = f; }
                }
                return newest;
            }
            catch { return null; }
        }
    }
}
