using System;
using System.Collections.Generic;

namespace TomatoBiquga
{
    /// <summary>
    /// 站点能力标志。
    ///
    /// 为什么用能力而不是类型：界面里到处是 `site as BiqugaSite` 这种判断
    /// （SetRefresh / ApplySettings / DownloadWorkersFor 各有一份），
    /// 站点一多就是每加一个站改四处。改成"这个站支持什么"之后，
    /// 界面按能力分支，新增站点不用动界面代码。
    /// </summary>
    [Flags]
    public enum SiteCapabilities
    {
        None = 0,
        /// <summary>支持关键词搜索</summary>
        Search = 1,
        /// <summary>支持直接粘地址 / book_id</summary>
        AddressIntake = 2,
        /// <summary>目录页即详情页（一次请求拿全目录）—— 不需要逐页遍历</summary>
        TocInDetailPage = 4,
        /// <summary>正文是 base64，需要解码</summary>
        Base64Content = 8,
        /// <summary>站点拒绝 .NET(Schannel) 的 TLS 握手，必须走 curl 后端</summary>
        NeedsCurl = 16,
        /// <summary>正文风控敏感，并发只会更快撞验证码（番茄就是这样）</summary>
        SerialOnly = 32,
    }

    /// <summary>
    /// 一个站点的描述。这是"站点是什么"的唯一声明处 ——
    /// 下拉框、探活、切换、书架、队列、降级全部从这里读。
    /// </summary>
    public sealed class SiteDescriptor
    {
        /// <summary>稳定标识：写进 BookInfo.Site / 书架 / DirCache / CrawlResume / 站点配置.ini 段名</summary>
        public string Key;
        /// <summary>界面上显示的名字</summary>
        public string DisplayName;
        /// <summary>造一个实例。每次调用都返回新对象（沿用既有行为：切站就换新实例）</summary>
        public Func<ISite> Factory;
        /// <summary>能力标志</summary>
        public SiteCapabilities Caps;
        /// <summary>站点主页，站点配置.ini 里 baseurl 的默认值</summary>
        public string DefaultBase = "";
        /// <summary>规则表站点为 null；内置的番茄/笔趣阁为 null 也表示"不是规则表站点"</summary>
        public object Rule;

        public bool Has(SiteCapabilities c) { return (Caps & c) == c; }

        public override string ToString() { return DisplayName; }
    }

    /// <summary>
    /// 站点注册表 —— 全项目唯一的站点列表真相来源。
    ///
    /// 在此之前，站点列表硬编码在 5 个地方（下拉框字面量、SelectedIndex 三元、
    /// SiteLabels 数组、探活数组、CurrentSite/MakeSite/SiteKeyOf），
    /// 其中 EnsureSiteUsable 的 `cur==1?2:(cur==2?1:-1)` 是"两个笔趣阁互切"的
    /// 成对逻辑 —— 站点超过 3 个就完全失效。全部改为查这里。
    /// </summary>
    public static class SiteRegistry
    {
        private static readonly List<SiteDescriptor> _all = new List<SiteDescriptor>();
        private static readonly Dictionary<string, SiteDescriptor> _byKey =
            new Dictionary<string, SiteDescriptor>(StringComparer.OrdinalIgnoreCase);

        static SiteRegistry()
        {
            Register(new SiteDescriptor
            {
                Key = "fanqie",
                DisplayName = "番茄小说",
                Factory = delegate { return new FanqieSite(); },
                Caps = SiteCapabilities.AddressIntake | SiteCapabilities.SerialOnly,
                DefaultBase = "",
            });

            Register(new SiteDescriptor
            {
                Key = "biquga-m",
                DisplayName = "笔趣阁（移动版·快）",
                Factory = delegate { return new BiqugaMobileSite(); },
                Caps = SiteCapabilities.Search | SiteCapabilities.AddressIntake
                     | SiteCapabilities.NeedsCurl,
                DefaultBase = "https://m.biquga.com",
            });

            Register(new SiteDescriptor
            {
                Key = "biquga",
                DisplayName = "笔趣阁（PC版·慢）",
                Factory = delegate { return new BiqugaSite(); },
                Caps = SiteCapabilities.Search | SiteCapabilities.AddressIntake
                     | SiteCapabilities.NeedsCurl,
                DefaultBase = "https://www.biquga.com",
            });

            // 规则表站点（笔尖中文 / 笔趣阁365 …）。加站点只改 SiteRules.cs。
            // 静态构造顺序：这里触发 SiteRules 的静态初始化，SiteRules 只**构造**
            // 描述对象、然后回调 Register 加进来；它自己的静态字段不反查注册表，
            // 所以不存在"互相等对方初始化完"的死锁。
            SiteRules.RegisterInto();
        }

        internal static void Register(SiteDescriptor d)
        {
            if (d == null) throw new ArgumentNullException("d");
            if (string.IsNullOrEmpty(d.Key)) throw new ArgumentException("站点 Key 不能为空");
            if (_byKey.ContainsKey(d.Key))
                throw new ArgumentException("站点 Key 重复：" + d.Key);

            // 自检：Key 必须和实例自己报的 Key 一致，否则书架/DirCache 会写错地方。
            // 放在这里是为了让不一致在**启动/自测**就炸，而不是等用户下完一本书才发现。
            string actual = SiteKeyOfInstance(d.Factory());
            if (!string.Equals(actual, d.Key, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "站点 Key 与实现不符：注册表写的是 \"" + d.Key + "\"，但实例报的是 \"" + actual + "\"。");

            _all.Add(d);
            _byKey[d.Key] = d;
        }

        /// <summary>全部站点，按注册顺序（下拉框就按这个顺序显示）</summary>
        public static IList<SiteDescriptor> All { get { return _all.AsReadOnly(); } }

        public static int Count { get { return _all.Count; } }

        /// <summary>按 Key 取描述；未知返回 null</summary>
        public static SiteDescriptor ByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            SiteDescriptor d;
            return _byKey.TryGetValue(key, out d) ? d : null;
        }

        /// <summary>按 Key 造实例；未知 Key 返回 null</summary>
        public static ISite Create(string key)
        {
            SiteDescriptor d = ByKey(key);
            return d == null ? null : d.Factory();
        }

        /// <summary>Key → 下拉框下标；-1 = 未找到</summary>
        public static int IndexOf(string key)
        {
            for (int i = 0; i < _all.Count; i++)
                if (string.Equals(_all[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Key → 中文显示名；未知则原样返回 key</summary>
        public static string LabelOf(string key)
        {
            SiteDescriptor d = ByKey(key);
            return d == null ? (key == null ? "" : key) : d.DisplayName;
        }

        /// <summary>实例 → Key。这是判断"这个 ISite 是哪个站"的唯一正确方式</summary>
        public static string KeyOf(ISite site)
        {
            if (site == null) return null;
            return SiteKeyOfInstance(site);
        }

        private static string SiteKeyOfInstance(ISite site)
        {
            // 规则表站点自己带 Key（见 RuleSite）；内置站点按类型映射。
            // 注意顺序：BiqugaMobileSite 必须在 BiqugaSite 之前判断。
            var rs = site as IRuleSite;
            if (rs != null) return rs.Key;
            if (site is FanqieSite) return "fanqie";
            if (site is BiqugaMobileSite) return "biquga-m";
            if (site is BiqugaSite) return "biquga";
            return null;
        }
    }

    /// <summary>
    /// 由规则表驱动的站点（见 Sites/RuleSite.cs）。
    /// 注册表用它判断一个 ISite 是不是规则表站点，并取它的稳定 Key。
    /// </summary>
    public interface IRuleSite
    {
        string Key { get; }
    }
}
