# 多站点 + 站点规则表 + 自动选最快站点 — 设计

日期：2026-10-14
状态：**已实施并验收**（见文末「实施结果」）
影响版本：v1.4.3 → v1.5.0

---

## 1. 背景与目标

用户在对比 SoNovel（Java，`E:\挂\SoNovel`）后提出两件事：

1. **把 SoNovel 的书源接进本项目**，让下载器不再只有「笔趣阁 + 番茄」两个源。
2. **下载时默认选最快的站点**。

同时要求落地 `报告-两个下载器慢的原因.md` 里列出的性能修复。

### 已实测的现状（全部为本机 curl 实测，非推断）

SoNovel `rules/main.json` 共 11 个书源。逐个跑「搜索 → 目录 → 正文」全链路，实测结果：

| 站点 | 规则域名 | 实测结果 |
| --- | --- | --- |
| **笔尖中文** | `xbiquwk.com` | **全链路通**。搜索 662 ms；目录**一页给全 1314 章 / 975 ms**；正文 394 ms，正文干净 |
| **笔趣阁365** | `biquge365.net` | **全链路通**。搜索 647 ms；目录 795 ms / 21 章；正文 650 ms / 2208 字。**不稳定**：3 次里 1 次搜不到 |
| **梦书中文** | `mcxs.la` | 搜索**忽略关键词**，返回固定页（《长乐歌》）。搜索不可用 |
| 书海阁 | `shuhaige.net` | 搜索返回 `错误提示` 页（1444 B） |
| 香书小说 | `xbiqugu.la` → `183.192.65.101` | 搜索返回 `警告` 页 |
| 鸟书网 | `99xs.info` → `99wx.info` | 搜索端点 **404** |
| 悠久小说网 | `ujxsw.org` | 搜索端点 **404** |
| 阅读库 | `yeudusk.com` | 搜索端点 **404** |
| 顶点小说 | `wxsy.net` | 搜索 **403**（反爬） |
| 笔趣阁22 | `22biqu.com` | **停放域名**（加载 `lander.parity.domains` 脚本） |
| 燃文小说网 | `ranwen8.cc` | TLS 被 RST（与 `m.biquga.com` 同款 SNI 拦截） |

**结论：11 个源里只有 2 个今日可用，另有 1 个搜索不可用。**

### 最有价值的发现

本项目「载入目录慢」的根因是笔趣阁 PC 站强制走 772 页串行链（实测约 10 分钟）。
而**笔尖中文把 1314 章放在一个目录页里，975 ms 全量拿到**。
接入它之后，「载入目录」从 ~10 分钟变成 ~1 秒 —— 比修连接复用更治本。

### 目标

1. 引入**站点注册表**，消灭现在分散在 5 处、且在多站点下会坏掉的硬编码站点列表。
2. 引入**规则描述表 + RuleSite**，新增站点 = 填一张表。
3. 接入实测可用的站点（笔尖中文、笔趣阁365）。
4. 提供**自动选最快站点**。
5. 落地性能修复。
6. 修掉两个站点一多就会真坏的隐患。

### 非目标

- 不做反爬对抗（不绕验证码、不伪造签名）——沿用项目既有红线。
- 不引入任何第三方依赖（项目刻意的零依赖姿态）。
- 不接入实测不可用的站点（7 个），也不为它们写「占位」实现。
- 不做书源分发（项目红线）。

---

## 2. 关键约束

| 约束 | 依据 |
| --- | --- |
| **编译器是 C# 5** | `csc.exe` 4.8.9221.0 只支持到 C# 5。**不能用**字符串插值 `$"..."`、`?.`、表达式体成员、自动属性初始化器、`nameof` |
| **零第三方依赖** | 只引 `System.dll / System.Core.dll / System.Drawing.dll / System.Windows.Forms.dll / System.Web.Extensions.dll / System.IO.Compression[.FileSystem].dll` |
| **文件必须在 build.bat 注册** | `build.bat` 用显式文件列表编译 7 个目标。站点源码通过 `set SITES=` 链接进**每一个** target（因为 `DirCache` 依赖 `BiqugaSite` 静态方法） |
| **.bat 必须纯 ASCII** | 中文放带 BOM 的 `.ps1`；`build.bat` 第一步就强制校验 |
| **离线自测必须保持全绿** | 当前 1153/1153。`tests\OfflineTests.cs` 有 3 处**断言恰好 3 个站点配置**，加站点会必然失败，必须同步改 |
| **不做 HTML 库** | 本地无 HtmlAgilityPack/AngleSharp，且添加 NuGet 违反零依赖 |

---

## 3. 架构

### 3.1 站点注册表（唯一真相来源）

现在站点列表硬编码在 5 处，其中 3 处在多站点下**直接坏掉**：

| 位置 | 现状 | 多站点下的问题 |
| --- | --- | --- |
| `MainForm.cs:141` | 下拉框 3 个字面量 | 需手工扩 |
| `MainForm.cs:549-551` | `SelectedIndex==0/1/2` 三元 | 纯按下标，脆弱 |
| `MainForm.cs:2965` | `alt = cur==1?2:(cur==2?1:-1)` | **两两互切**逻辑，>3 站时完全失效 |
| `MainForm.cs:2419-2420, 2445` | `SiteLabels` / 探活数组按下标绑 | 需同步扩 |
| `MainForm.cs:975-980, 943-949, 3285-3290, 2492-2499, 3779-3785, 3570-3578, 3845-3848` | 各自 if/三元 | 5+ 处重复编码同一个列表 |

新增 `src/Common/SiteRegistry.cs`：

```csharp
public sealed class SiteDescriptor
{
    public string Key;              // "xbiquwk" — BookInfo.Site / 书架 / DirCache / CrawlResume 都用它
    public string DisplayName;      // "笔尖中文"
    public Func<ISite> Factory;
    public SiteCapabilities Caps;   // 能力标志，UI 按能力而非类型分支
    public bool BuiltIn;            // 内置 3 站（番茄/笔趣阁）为 true
    public string DefaultBase;      // 站点配置.ini 里 baseurl 的默认值
}

[Flags]
public enum SiteCapabilities
{
    None            = 0,
    Search          = 1,      // 支持关键词搜索
    AddressIntake   = 2,      // 支持直接粘地址/book_id
    TocInDetailPage = 4,      // 目录页即详情页（一页给全）→ 无需遍历
    Base64Content   = 8,      // 正文是 base64（需解码）
    NeedsCurl       = 16,     // 站点拒绝 .NET Schannel 握手，必须 curl 后端
}

public static class SiteRegistry
{
    public static IReadOnlyList<SiteDescriptor> All { get; }
    public static SiteDescriptor ByKey(string key);     // 未知 key → null
    public static string KeyOf(ISite site);             // 类型 → key
    public static ISite Create(string key);             // key → 新实例
    public static int IndexOf(string key);              // key → 下拉下标（-1 = 未找到）
    public static string LabelOf(string key);           // key → 中文名
}
```

**迁移清单**：上表 10 处全部改为查注册表。`EnsureSiteUsable` 的降级逻辑从「1↔2 互切」改为「从 `All` 里挑**当前可用且评分最高**的替代站」。

**下拉框身份**：现在纯按下标，且 `MainForm.cs:2485` 会往标签追加「（当前不可用）」，所以**标签不能当标识**。改为 `cboSite.Items` 存 `SiteDescriptor` 对象（`DisplayMember` 绑 `DisplayName`），切换读对象而非下标。新增顶部项「**自动（最快）**」。

### 3.2 规则描述表 + RuleSite

新增 `src/Sites/RuleSite.cs`，实现 `ISite`（+ `IProbeable`，+ `IChapterUrlProvider` 当能力允许）。

```csharp
public sealed class SiteRule
{
    public string Key, DisplayName, Base;

    // 搜索
    public string SearchUrlTemplate;      // 支持 %s 占位
    public bool   SearchIsPost;
    public string[][] SearchForm;         // POST 字段：{name, value}，value 含 %s 时替换关键词
    public string SearchReferer;          // 默认 Base

    // 搜索结果的书籍链接
    public string[] BookLinkPatterns;     // 正则，按序尝试，第一个命中的用

    // 详情页元信息
    public string BookTitlePattern, BookAuthorPattern, BookIntroPattern, BookCoverPattern;

    // 目录
    public string TocUrlTemplate;         // 空 = 详情页即目录；含 %s 时替换 bookPath
    public string TocLinkPattern;         // 章节链接正则（捕获组 1 = 链接）
    public bool   TocIsAbsolute;          // 链接是绝对地址

    // 正文
    public string ContentPattern;         // 正文块正则（捕获组 1 = HTML 片段）
    public bool   ContentIsBase64;
    public string ContentStripPattern;    // 广告/水印行，正则，可空
    public string Encoding;               // "utf-8" / "gb18030" / "auto"

    public string[] KnownTitles;          // 用于基准测试的固定关键词（至少 1 个）
}
```

**取舍说明（对应用户确认的第 2 节）**：用户选了「小型选择器引擎」，但自写 CSS 选择器引擎（要支持 `div:nth-child(4) > ul > li > a`、属性选择器、XPath）是几百行且易错的部分。因此本设计**用正则承载描述表，而不是 CSS 选择器**：保留「加站点只填一张表」的全部好处，但不背选择器引擎的风险，且与 `BiqugaSite` 现有写法一致。

`RuleSite` 的职责：
- `Search()`：按描述发请求 → 正则抽书籍链接 → 抓详情页补全书名/作者/简介 → 返回 `BookInfo`
- `LoadBook()`：抓详情页 → 抽元信息 → 按 `TocUrlTemplate` 决定是否另取目录页 → 抽章节链接 → 返回 `BookInfo`
  - **目录缓存复用**：沿用 `DirCache`（key = `Key + "_" + bookPath`），命中直接返回
  - `TocInDetailPage` 时**不需要**遍历，因此**不需要 CrawlResume**
- `LoadChapter()`：算正文 URL → 抓 → 按 `ContentPattern` 抽 → 可选 base64 解码 → 去广告 → 返回纯文本
- `ChapterUrlFor()/ChapterTextFromHtml()`：实现 `IChapterUrlProvider`，让**批量并发抓取**（`HttpBatch`）可用
- `Probe()`：抓 `Base` 首页，返回 null/原因

### 3.3 新增站点（仅实测可用）

| Key | 名称 | 能力 | 实测 |
| --- | --- | --- | --- |
| `xbiquwk` | 笔尖中文 | Search, AddressIntake, TocInDetailPage | 搜索 662ms / 目录 975ms·1314章 / 正文 394ms |
| `biquge365` | 笔趣阁365 | Search, AddressIntake, TocInDetailPage | 搜索 647ms / 目录 795ms·21章 / 正文 650ms |

`梦书中文` **不接入**：搜索忽略关键词，接进去只会让用户搜到错书。

加上内置 3 站 → 共 **5 个可选站 + 1 个「自动（最快）」**。

### 3.4 默认选最快站点

沿用既有 `IProbeable` 模式，扩展成 `IRateable`：

```csharp
public interface IRateable { SiteScore Measure(); }

public sealed class SiteScore
{
    public string Key;
    public double SearchMs, TocMs, ChapterMs;   // 失败项 = double.PositiveInfinity
    public double TotalMs { get { return SearchMs + TocMs + ChapterMs; } }
    public bool Ok;
    public string Error;
}
```

**评分定义（用户确认）**：`TotalMs = 搜索 + 目录 + 正文单章`，**升序**取最小者。
每个维度 3 次取中位数，降低抖动。基准关键词用 `SiteRule.KnownTitles[0]`（已验证能命中的书名）。

**行为约束**：
- 基准**只发 3 个请求/站点**（搜索/目录/正文各一），不遍历整本。
- 结果存 `站点配置.ini`（新增 `[site-score]` 段 + `measuredat` 时间戳），**24 h 过期**。
- 下拉框顶部项 **「自动（最快：笔尖中文）」**，名字随实测结果更新。
- 基准**必须能手动触发**（复用「检测站点」按钮），启动时后台跑一次且**不弹窗**。
- **全部失败 → 回退到现有行为**（笔趣阁 PC），绝不降低既有可用性。
- `SuppressDialogs`（自动化/测试）下不跑基准，避免测试联网。

### 3.5 性能修复

来源：`报告-两个下载器慢的原因.md`。按性价比排序：

| # | 改动 | 位置 | 预期 |
| --- | --- | --- | --- |
| 1 | **curl 连接复用** | `Http.FetchWithCurl` | 单页 770ms → ~230ms（**3.4×**）；772 页省 ~7.7 分钟 |
| 2 | 去掉 `req.KeepAlive=false` | `Http.cs:326`、`Http.cs:636` | .NET 路径同样受益 |
| 3 | **502 加入可重试名单** + 跳过必然失败的 .NET 回退 | `Http.cs:368-378`、`Http.cs:394` | 每次 502 从白等 25 s 降到正常退避 |
| 4 | 删掉死代码 `BiqugaSite.CrawlWorkers` | `BiqugaSite.cs:34` + 5 处赋值 + `docs/架构.md:98` | 消除误导 |
| 5 | 连接复用后重估 `Http.Polite()` | `BiqugaSite.cs:286` | 复用后该延迟占比 3% → 13% |

**关于 #1 的技术选择**：`Http.cs:14-18` 说明 biquga 拒绝 .NET Schannel 握手，故必须 curl。
curl 复用连接的可行做法：
- 单进程多 URL（`curl url1 url2 ...`）——实测可行，但目录遍历是**串行依赖链**（下一页地址写在当前页 HTML 里），无法预知全部 URL，**因此对遍历阶段不适用**。
- 因此 #1 实际形态：**给 `Http` 加一个"批量取数"模式**，供**下载阶段**（`HttpBatch`，URL 全部已知）使用；遍历阶段依赖 #3 减少无谓等待。
- 若实测表明单进程批量对站点不友好（实测 10 页单进程耗时 125 s，疑似被站点限速），则 #1 降级为「只对 `HttpBatch` 启用，并加开关」。

> **诚实说明**：报告里「772 页省 7.7 分钟」的推算假设每页省下 595 ms 握手。但 `probe6.py` 实测发现单进程批量反而更慢（125 s/10 页），说明该站点对复用连接有自己的限速策略。**#1 的真实收益必须在实施方案中用真实遍历验证后才写进文档**，不得直接引用推算值。

### 3.6 两个必须同时修的隐患（站点一多就会真坏）

1. **`SearchIndex` 查询缓存无站点维度** — `SearchIndex.cs:166-185`
   现在 `PutQuery(keyword, list)` / `GetQuery(keyword)` 只按关键词。
   后果：A 站搜「沧元图」，切到 B 站再搜，**返回 A 站结果**。3 站时是潜在 bug，5 站时必然出错。
   **改法**：缓存键改为 `siteKey + "\u0000" + keyword`；读旧格式缓存视为未命中（自然过期）。

2. **输出路径不分站点** — `DownloadRunner.ResolvePaths`（`DownloadRunner.cs:85-89`）
   后果：两站有同名书时**互相覆盖**同一份 `.txt` 与目录。
   **改法**：书目录名追加站点后缀，形如 `书名 [笔尖中文]`；**但要兼顾兼容性**——已有下载产物在 `书名` 目录下，改为新命名会让老用户「重复下载」。
   故采用：**同名书冲突时才加后缀**（先探测 `书名` 目录是否存在且属于别的站点）。同时在 `来源：` 头写真实站点名（现在 `DownloadRunner.cs:1100` 是二选一三元，任何新站都印「笔趣阁」）。

---

## 4. 数据流

```
用户选站（或"自动"）
  └─ SiteRegistry.ByKey / ByScore
       └─ RuleSite(rule)
            ├─ Search()  → HTTP → 正则抽书籍链接 → 详情页补全 → BookInfo
            ├─ LoadBook() → 详情页 → 元信息 + 章节链接
            │                └─ TocInDetailPage ? 一页拿全 : 走 TocUrlTemplate
            │                └─ DirCache.Load(key) 命中则直接返回
            └─ LoadChapter() → 正文 URL → HTTP → ContentPattern → [base64] → 去广告 → 文本
                 └─ IChapterUrlProvider 让 HttpBatch 批量并发抓取
```

---

## 5. 错误处理

| 场景 | 行为 |
| --- | --- |
| 搜索无结果 | 返回空列表（不抛异常），日志说明「站点可能改版或无此书」 |
| 详情页解析不到书名 | 抛异常并附**命中的正则与页面片段**，便于定位改版 |
| 目录为空 | 抛异常「站点结构可能已变化」 |
| 正文块匹配为空 | 返回 null 让调用方计入缺失章节（沿用现有机制） |
| 编码判定失败 | 回退 `gb18030`，再回退 UTF-8 替换字符 |
| 基准测试超时 | 该站 `TotalMs = +∞`，不进候选；不影响其他站 |
| 所有站基准失败 | 回退内置笔趣阁 PC，且**不改变**用户当前选择 |

---

## 6. 测试策略

**离线（必须，纳入 `build.bat` 自测门）**
- `SiteRegistry`：`All` 唯一性、`ByKey`/`KeyOf`/`IndexOf` 往返一致、未知 key 返回 null
- 规则表校验：每张表 `Key` 唯一、`KnownTitles` 非空、正则**可编译**、`SearchForm` 字段名非空
- `RuleSite` 解析：用**固定 HTML 样本**（离线，取自本次实测的真实页面片段）验证
  搜索抽取、目录抽取、正文抽取、base64 解码、广告行剔除
- 编码判定：utf-8 / gb18030 样本各一
- 性能修复：`Http.CurlArgsFor` 参数构造的**纯函数**测试（不联网）
- SearchIndex 站点维度：不同站点同关键词**不互相命中**
- ResolvePaths：同名书冲突时加站点后缀；无冲突时**保持原路径**（兼容性回归）
- 更新 `OfflineTests.cs` 里 3 处「恰好 3 站」断言 → 按注册表长度断言

**联网（不进自测门，手动/工具）**
- `tools\LiveProbe.cs` 增加 `sites` 模式：逐站跑基准并打印评分表
- 新增 `tools\SiteBench.cs`：完整评分 + 目录耗时 + 正文抽样

---

## 7. 分节实施与验收

按用户要求**逐节实施、每节验收**：

| 节 | 内容 | 验收方式 |
| --- | --- | --- |
| 1 | 站点注册表 + 迁移 10 处硬编码 | 构建全绿 + 1153 项自测全绿 + 布局探针无越界 |
| 2 | 规则表 + `RuleSite` | 新增离线解析测试全绿（固定 HTML 样本） |
| 3 | 接入 笔尖中文 / 笔趣阁365 | `LiveProbe sites` 实测两站全链路通 |
| 4 | 自动选最快站点 | 实测评分表 + 手动触发可用 + 全失败回退 |
| 5 | 性能修复 5 条 | 真实遍历前后耗时对比（**用实测值，不用推算**） |
| 6 | SearchIndex 站点维度 + 输出路径冲突 | 针对性回归测试 + 老产物不被改名 |
| 7 | 文档与 build.bat 同步 | `build.bat` 7 个目标全绿 |

---

## 8. 风险

| 风险 | 缓解 |
| --- | --- |
| 站点改版导致正则失效 | 解析失败时日志附页面片段；规则表集中，改一处即可 |
| 笔趣阁365 搜索不稳定（实测 1/3 失败） | 基准含成功率；失败多则评分靠后，自动模式自然不用它 |
| C# 5 限制导致写法受限 | 全部用 C# 5 语法；构建门会立刻暴露 |
| 加站点让布局矩阵/文字体检失败 | 已识别 `OfflineTests.cs:976,1937,2001,2013`，同步改 |
| 输出路径改名造成重复下载 | 仅在**同名冲突**时加后缀，并有回归测试保证无冲突时路径不变 |

---

## 9. 未决 / 待实测

- `#1 curl 连接复用` 的真实收益需在实施方案中用**真实遍历**验证；若站点限速导致复用反而更慢，则只对 `HttpBatch` 启用并加开关。
- 笔尖中文 `1314 章一页给全` 是否对所有书成立（本次只验了《剑来》），需在实施时用 2~3 本不同书验证。
- 书架/队列里已存的 `Entry.Site` 字符串（`biquga`/`biquga-m`/`fanqie`）必须保持可解析 —— 注册表 key 与旧值完全一致，不重命名既有站。

---

## 10. 实施结果（2026-10-14 完成）

### 验收数据

| 检查项 | 结果 |
| --- | --- |
| `build.bat` 全部 7 个目标 | **通过**（exit 0） |
| 离线自测 `_offlinetests.exe` | **1345/1345**（开工前 1153 → +192 条断言） |
| 端到端 `_e2e.exe` | **79/79** |
| 接口自测 `_selftest.exe` | **1160/1160** |
| 文字体检 `_textfit.exe` | **234/234** |
| 布局探针 | **没发现越界/重叠** |
| **联网全链路** `_liveprobe.exe sites` | **12/12** |
| **联网评分** `_liveprobe.exe rate` | **6/6** |

### 联网实测（最终值）

| 站点 | 搜索 | 目录 | 正文单章 | 综合分 |
| --- | --- | --- | --- | --- |
| **笔尖中文** | 513 ms | **649 ms / 1308 章** | 635 ms | **1797 ms** |
| 笔趣阁365 | 665 ms | 1944 ms / 300 章 | 1049 ms | 3658 ms |
| 笔趣阁 PC 版 | — | 实测超时（>90 秒） | — | 不可用 |
| 笔趣阁移动版 | — | curl 退出码 35（SNI 被拒） | — | 不可用 |
| 番茄小说 | — | 无可评测的关键词搜索 | — | 不参与 |

### 与计划的偏差（都要记住为什么）

1. **§3.5 的「curl 连接复用」不需要做** —— `HttpBatch` 早就用 `curl --parallel`
   实现了连接复用（注释里写着实测 3.2 倍）。那份性能报告是基于过时理解写的。
   实际做的性能改动是：**502 加入可重试**、**跳过必然失败的 .NET 回退**（按主机记）、
   **删掉死字段 `CrawlWorkers`**、**`HttpBatch` 补 `--compressed`**。

2. **「梦书中文」最终没有接入** —— 计划里还留着"只做搜索"的可能，但实测它
   **忽略关键词、永远返回同一本书**，接进去只会让用户搜到错书。规则里留了
   `SearchResultMustContainKeyword` 开关专门挡这类站点。

3. **规则表用正则而不是 CSS 选择器**（计划 §3.2 已说明取舍），并因此加了
   `TocBlockPattern`（先框住目录容器）—— 不框的话笔尖中文详情页上 7 个
   分类/排行榜链接会被当成章节。

### 实施中发现并修掉的真 bug

| # | 问题 | 表现 | 修法 |
| --- | --- | --- | --- |
| 1 | `CoverFetcher.Absolutize` 是"相对站点根"语义 | 笔尖中文的 `603316.html` 拼成 `/603316.html`（丢掉 `/1_1245/` 层）→ 404 或抓错页 | `RuleSite.ResolveUrl` 实现标准页面相对解析（含 `..` 归一） |
| 2 | 章节标题在**整页**里二次搜 URL 取文字 | 笔趣阁365 目录首项标题变成「开始阅读」（导航链接同 URL 且更靠前）。**Python 验证不会复现**，差点放过 | 标题只从匹配文本自己取；规则用第 2 捕获组直接给标题 |
| 3 | `SearchIndex` 查询缓存无站点维度 | A 站搜完切 B 站，返回 A 站结果，拿着错地址请求 B 站 | 缓存键加 `site`；老缓存（无 site）一律当未命中 |
| 4 | 输出路径不分站点 | 两站同名书互相覆盖 | 仅在**书架明确说目录属于别的站点**时加站点后缀 |
| 5 | 撞车判据过宽（我自己的过度设计） | "目录里有 .txt 但无书架记录"也判撞车 → 再次下载写到新目录，增量/续传/补齐全废，**一次打挂 22 条测试** | 判据收紧到只有一条：书架明确记录归属别的站点 |
| 6 | `SiteRater` 时间戳用 `{4:F0}` 写、当 Ticks 读 | 读回时间早最多 1 秒 → 永远判定过期 → 每次启动重测 | 时间戳无损往返；不可用站点也存时间戳 |
| 7 | 评分的"三项耗时"里未测项留 0 | 0 被当成"瞬间完成"，不可用站点在排名里显示成像"只是有点慢" | 加 `*Measured` 标志，未测一律记 ∞ |
| 8 | 测速读到了**目录缓存** | 笔趣阁 PC 版目录缓存命中只要 36 ms，而真实遍历要 15 分钟 → 排名完全失真 | 加 `SuppressDirCache`，测速时绕开缓存（实测后它如实地报"超时"） |

### 验收方式的教训

中途有 **22 条测试反复失败**，我花了很多轮才定位 —— 真实原因是
**上一轮测试进程没退出、锁住了 exe，导致后续编译静默失败**，
我一直在读旧二进制的输出。教训：**验证前先确认时间戳**，
不要相信"编译成功"的打印（管道会吃掉 exit code）。

### 仍然存在的已知限制

* **规则表站点是第三方聚合站**，随时可能改版或下线。解析失败会附页面片段便于定位；
  改一条规则即可。
* **笔趣阁365 的搜索受站点 15 秒间隔限制**，会间歇性返回 0 条结果 ——
  这是站点行为，不是本工具的 bug（评分已反映，自动模式会少用它）。
* **笔趣阁 PC 版目录遍历约 15 分钟**，是结构性的（反向链条 + 每请求新连接），
  已确认**无法在客户端解决**；正确做法是换源。
* 规则表的 `ChapterPagesAddressableByKey` 对两个新站都设为 `false`：
  它们实测没有章内分页，但保守起见不启用批量路径 ——
  批量路径一旦遇到章内分页会**静默截断正文**（最难发现的那种损坏）。
