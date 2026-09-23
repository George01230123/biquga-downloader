using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 极简配置：一个 settings.ini（键=值），放在 exe 同目录。
    /// 为什么不用 json/注册表：.NET Framework 自带的配置体系太重，
    /// 而这个工具只有几个数字要存，纯文本用户还能自己改。
    /// 读取失败/文件损坏一律回退默认值，绝不因为配置问题起不来。
    /// </summary>
    public class AppSettings
    {
        public const string FileName = "settings.ini";

        // ---- 默认值（实测调出来的，见 README 的实测数据一节）----
        /// <summary>移动版「离线模式」并发线程：整本正文预抓，8 线程实测 1000 章约 10 分钟</summary>
        public int BiqugaOfflineWorkers = 8;
        /// <summary>移动版「非离线」并发线程</summary>
        public int BiqugaOnlineWorkers = 6;
        /// <summary>PC 版并发线程（PC 版正文要串行走 prev 链，主要影响目录遍历）</summary>
        public int BiqugaPcWorkers = 6;
        /// <summary>每次请求之间的最小/最大随机间隔（毫秒），避免把站点打疼</summary>
        public int MinDelayMs = 60;
        public int MaxDelayMs = 180;
        /// <summary>整本首次遍历目录的超时保护（分钟），超过就中止</summary>
        public int CrawlTimeoutMinutes = 60;
        /// <summary>失败章节自动重试轮数：整本下完后，只对失败的那几章再跑一遍</summary>
        public int RetryPasses = 1;

        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        private static AppSettings _current;

        /// <summary>当前生效的配置（首次访问时从磁盘加载）</summary>
        public static AppSettings Current
        {
            get
            {
                if (_current == null) _current = Load(DefaultPath);
                return _current;
            }
        }

        public static AppSettings Load(string path)
        {
            var s = new AppSettings();
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        var t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                        int eq = t.IndexOf('=');
                        if (eq <= 0) continue;
                        var k = t.Substring(0, eq).Trim().ToLowerInvariant();
                        var v = t.Substring(eq + 1).Trim();
                        int n;
                        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                        s.Set(k, n);
                    }
                }
            }
            catch { /* 配置坏了就用默认值，不影响启动 */ }
            s.Clamp();
            return s;
        }

        /// <summary>把值夹到安全范围：并发 0 或 999 都不是用户想要的</summary>
        public void Clamp()
        {
            BiqugaOfflineWorkers = ClampInt(BiqugaOfflineWorkers, 1, 32);
            BiqugaOnlineWorkers = ClampInt(BiqugaOnlineWorkers, 1, 32);
            BiqugaPcWorkers = ClampInt(BiqugaPcWorkers, 1, 32);
            MinDelayMs = ClampInt(MinDelayMs, 0, 5000);
            MaxDelayMs = ClampInt(MaxDelayMs, MinDelayMs, 5000);
            CrawlTimeoutMinutes = ClampInt(CrawlTimeoutMinutes, 1, 600);
            RetryPasses = ClampInt(RetryPasses, 0, 3);
        }

        private static int ClampInt(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private void Set(string key, int value)
        {
            switch (key)
            {
                case "biqugaofflineworkers": BiqugaOfflineWorkers = value; break;
                case "biqugaonlineworkers": BiqugaOnlineWorkers = value; break;
                case "biqugapcworkers": BiqugaPcWorkers = value; break;
                case "mindelayms": MinDelayMs = value; break;
                case "maxdelayms": MaxDelayMs = value; break;
                case "crawltimeoutminutes": CrawlTimeoutMinutes = value; break;
                case "retrypasses": RetryPasses = value; break;
                // 未知键忽略：老版本写的键，新版本不该因此报错
            }
        }

        public void Save()
        {
            Save(DefaultPath);
        }

        public void Save(string path)
        {
            Clamp();
            var sb = new StringBuilder();
            sb.AppendLine("# 小说下载器配置（这个文件可以手改，改完重启程序生效）");
            sb.AppendLine("# 并发数越大越快，但太大会被站点限速；8 是实测比较稳的值。");
            sb.AppendLine("BiqugaOfflineWorkers=" + BiqugaOfflineWorkers);
            sb.AppendLine("BiqugaOnlineWorkers=" + BiqugaOnlineWorkers);
            sb.AppendLine("BiqugaPcWorkers=" + BiqugaPcWorkers);
            sb.AppendLine("MinDelayMs=" + MinDelayMs);
            sb.AppendLine("MaxDelayMs=" + MaxDelayMs);
            sb.AppendLine("CrawlTimeoutMinutes=" + CrawlTimeoutMinutes);
            sb.AppendLine("RetryPasses=" + RetryPasses);
            try
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                _current = this;
            }
            catch (Exception ex)
            {
                throw new Exception("保存配置失败：" + ex.Message + "（目录可能不可写，例如装在 Program Files 下）");
            }
        }

        public override string ToString()
        {
            var c = new List<string>();
            c.Add("离线并发=" + BiqugaOfflineWorkers);
            c.Add("在线并发=" + BiqugaOnlineWorkers);
            c.Add("PC并发=" + BiqugaPcWorkers);
            c.Add("间隔=" + MinDelayMs + "~" + MaxDelayMs + "ms");
            c.Add("重试=" + RetryPasses);
            return string.Join("，", c.ToArray());
        }
    }
}
