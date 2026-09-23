using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TomatoBiquga
{
    /// <summary>
    /// HTTP 取数层。
    ///
    /// 为什么不用 .NET 自带的 HttpClient/WebRequest：
    /// biquga.com 只提供 TLS 1.3 且会拒绝 .NET(Schannel via SslStream) 的握手，
    /// .NET Framework 4.8 与 .NET 10 都试过，一律 “Received an unexpected EOF”。
    /// 而 Windows 自带的 curl.exe（Win10 1803+ 系统内置）能正常访问，
    /// 所以这里用 curl.exe 作为取数后端，工具本身保持免安装、零依赖。
    /// curl 不可用时自动回退到 .NET 原生请求（番茄等站点原生可用）。
    /// </summary>
    public static class Http
    {
        public static string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        public static int TimeoutSeconds = 25;
        public static int MaxRetries = 3;
        public static int MinDelayMs = 120;
        public static int MaxDelayMs = 500;

        /// <summary>最近一次请求用的是哪个后端（用于日志展示）</summary>
        public static string LastBackend = "";

        private static readonly Random Rnd = new Random();
        private static string _curlPath;
        private static bool _curlChecked;

        /// <summary>
        /// 应用用户设置里的请求间隔。注意 MinDelayMs 必须 ≥1：
        /// Polite() 里是 Rnd.Next(MaxDelay-MinDelay)，差值为 0 会抛 ArgumentOutOfRange。
        /// </summary>
        public static void Configure(int minDelayMs, int maxDelayMs)
        {
            if (minDelayMs < 1) minDelayMs = 1;
            if (maxDelayMs < minDelayMs) maxDelayMs = minDelayMs;
            MinDelayMs = minDelayMs;
            MaxDelayMs = maxDelayMs;
        }

        public static string CurlPath
        {
            get
            {
                if (!_curlChecked)
                {
                    _curlChecked = true;
                    foreach (var p in new[]
                    {
                        Path.Combine(Environment.SystemDirectory, "curl.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "curl.exe"),
                    })
                    {
                        if (File.Exists(p)) { _curlPath = p; break; }
                    }
                }
                return _curlPath;
            }
        }

        public static bool CurlAvailable { get { return CurlPath != null; } }

        /// <summary>抓一个页面（自动重试）</summary>
        public static string Get(string url, string referer = null)
        {
            return Request(url, null, referer);
        }

        /// <summary>提交表单</summary>
        public static string Post(string url, string body, string referer = null)
        {
            return Request(url, body, referer);
        }

        private static string Request(string url, string body, string referer)
        {
            Exception last = null;
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    string text;
                    if (CurlAvailable)
                    {
                        text = FetchWithCurl(url, body, referer);
                        LastBackend = "curl";
                    }
                    else
                    {
                        text = FetchWithDotNet(url, body, referer);
                        LastBackend = ".NET";
                    }
                    if (string.IsNullOrEmpty(text)) throw new Exception("返回内容为空");
                    return text;
                }
                catch (Exception ex)
                {
                    last = ex;
                    // curl 失败时尝试原生后端
                    if (CurlAvailable && attempt == 0)
                    {
                        try
                        {
                            var t = FetchWithDotNet(url, body, referer);
                            if (!string.IsNullOrEmpty(t)) { LastBackend = ".NET(回退)"; return t; }
                        }
                        catch (Exception ex2) { last = ex2; }
                    }
                    if (attempt < MaxRetries - 1)
                        Thread.Sleep(500 * (attempt + 1) + Rnd.Next(250));
                }
            }
            throw new Exception(string.Format("请求失败（已重试 {0} 次）：{1}\n  {2}", MaxRetries, Shorten(url),
                last == null ? "未知错误" : last.Message));
        }

        // ---------------------------------------------------------- curl 后端

        private static string FetchWithCurl(string url, string body, string referer)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "tb_" + Guid.NewGuid().ToString("N") + ".html");
            try
            {
                var args = new StringBuilder();
                args.Append("-s -L --max-time ").Append(TimeoutSeconds);
                args.Append(" --compressed");
                args.Append(" -A \"").Append(UserAgent).Append('"');
                args.Append(" -H \"Accept-Language: zh-CN,zh;q=0.9\"");
                if (!string.IsNullOrEmpty(referer))
                    args.Append(" -H \"Referer: ").Append(referer).Append('"');
                if (body != null)
                {
                    args.Append(" -X POST -H \"Content-Type: application/x-www-form-urlencoded\"");
                    args.Append(" --data-binary \"").Append(body.Replace("\"", "\\\"")).Append('"');
                }
                args.Append(" -o \"").Append(tmp).Append('"');
                args.Append(" \"").Append(url).Append('"');

                var psi = new ProcessStartInfo(CurlPath, args.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    var err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit((TimeoutSeconds + 15) * 1000))
                    {
                        try { p.Kill(); } catch { }
                        throw new Exception("curl 超时");
                    }
                    if (p.ExitCode != 0)
                        throw new Exception("curl 退出码 " + p.ExitCode + (string.IsNullOrEmpty(err) ? "" : "：" + err.Trim()));
                }

                if (!File.Exists(tmp)) throw new Exception("curl 没有产生输出文件");
                var bytes = File.ReadAllBytes(tmp);
                if (bytes.Length == 0) throw new Exception("curl 返回空内容");
                return Decode(bytes);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // ---------------------------------------------------------- 原生后端

        private static string FetchWithDotNet(string url, string body, string referer)
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = body == null ? "GET" : "POST";
            req.UserAgent = UserAgent;
            req.Timeout = TimeoutSeconds * 1000;
            req.ReadWriteTimeout = TimeoutSeconds * 1000;
            req.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
            req.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
            req.Headers["Accept-Language"] = "zh-CN,zh;q=0.9";
            req.KeepAlive = false;
            req.AllowAutoRedirect = true;
            if (!string.IsNullOrEmpty(referer)) req.Referer = referer;
            if (body != null)
            {
                var b = Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
                req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buf = new byte[16384];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return Decode(ms.ToArray(), resp.ContentType);
            }
        }

        // ---------------------------------------------------------- 编码

        private static string Decode(byte[] raw, string contentType = null)
        {
            string enc = null;
            if (!string.IsNullOrEmpty(contentType))
            {
                var m = Regex.Match(contentType, @"charset=([\w\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) enc = m.Groups[1].Value;
            }
            if (enc == null)
            {
                var head = Encoding.ASCII.GetString(raw, 0, Math.Min(3072, raw.Length));
                var m = Regex.Match(head, @"charset\s*=\s*[""']?([\w\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) enc = m.Groups[1].Value;
            }
            if (string.IsNullOrEmpty(enc)) enc = "utf-8";
            if (Regex.IsMatch(enc, "^gb(2312|k|18030)$", RegexOptions.IgnoreCase))
            {
                try { return Encoding.GetEncoding(enc).GetString(raw); }
                catch { }
            }
            return Encoding.UTF8.GetString(raw);
        }

        private static string Shorten(string s)
        {
            return s != null && s.Length > 90 ? s.Substring(0, 90) + "..." : s;
        }

        public static void Polite()
        {
            Thread.Sleep(MinDelayMs + Rnd.Next(Math.Max(1, MaxDelayMs - MinDelayMs)));
        }

        // ---------------------------------------------------------- HTML 工具

        public static string HtmlDecode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"&#x([0-9a-fA-F]+);", m => char.ConvertFromUtf32(Convert.ToInt32(m.Groups[1].Value, 16)));
            s = Regex.Replace(s, @"&#(\d+);", m => char.ConvertFromUtf32(int.Parse(m.Groups[1].Value)));
            s = s.Replace("&nbsp;", " ").Replace("&lt;", "<").Replace("&gt;", ">")
                 .Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&mdash;", "—")
                 .Replace("&ndash;", "–").Replace("&hellip;", "…").Replace("&amp;", "&");
            return s;
        }

        public static string StripTags(string html)
        {
            if (html == null) return "";
            html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</(p|div|li|h\d)>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<[^>]+>", "");
            return HtmlDecode(html);
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "未命名";
            var sb = new StringBuilder();
            foreach (var c in name)
            {
                if ("\\/:*?\"<>|\r\n\t".IndexOf(c) >= 0) sb.Append('_');
                else sb.Append(c);
            }
            var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
            if (s.Length > 80) s = s.Substring(0, 80);
            return s.Length == 0 ? "未命名" : s;
        }
    }
}
