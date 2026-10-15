using System.Reflection;
using System.Runtime.InteropServices;

// ============================================================
//  程序集信息 —— 这个文件必须用 UTF-8 **带 BOM** 保存
//  （csc 在没有 BOM 时可能按系统代码页 GBK 解析源码，
//   中文会变成乱码，右键属性里就会显示乱码）
//  发版时改这里的版本号，不要去改 build.bat 里的 /out 名字。
//
//  关于命名：产品名刻意用 ASCII 的 novel-downloader（也是仓库名），
//  中文标题只出现在 AssemblyTitle / Description 里。原因：
//  仓库名和产品名带第三方商标容易被误认为官方工具，见 LICENSE 里的说明。
// ============================================================
[assembly: AssemblyTitle("小说下载器")]
[assembly: AssemblyDescription("笔趣阁小说下载器（免安装单文件 exe，零运行时依赖，导出 UTF-8 TXT）")]
[assembly: AssemblyProduct("novel-downloader")]
[assembly: AssemblyCompany("novel-downloader contributors")]
[assembly: AssemblyCopyright("Copyright (c) 2025 novel-downloader contributors  ·  MIT License")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("7b1c9f42-5d3a-4c88-9e21-0a6f4d2b8c11")]

// 版本号：前两位跟 README / Release tag 对齐
// 1.5.1：按站点实测调默认并发（只改默认值，不动用户已写好的配置）。
//        · 笔尖中文 / 笔趣阁365 默认并发 6 → **12**
//          实测（每档 25 章，限流计数全程为 0）：
//            笔尖中文  4→155ms  8→79ms  **12→60ms**  16→48ms  24→43ms
//            笔趣阁PC  4→979ms  8→1038ms 12→747ms   16→457ms
//          → 1308 章的书，正文下载从 ≈2.1 分降到 ≈1.2 分。
//        · 笔趣阁 PC 保持 6：它的成本大头是**目录遍历**（772 页串行），
//          加正文并发省不了多少；而且换源收益远大于调并发。
//        · 不认识的站点给保守值 6（没有"高并发不会被限流"的实测依据就不冒进）。
//        · 顺带修掉一个会自己过期的测试：评分时间戳用例写死了日期，
//          隔一天再跑就假失败（代码其实是对的，Ticks 完全一致）。
// 1.5.0：多站点 + 自动选最快站点。
//        · 站点注册表（SiteRegistry）：站点列表不再硬编码在界面里，
//          加站点 = 在 SiteRules.cs 填一张表。
//        · 接入 2 个实测可用的书源：笔尖中文（**目录一页给全 1300+ 章、
//          实测 0.65 秒**）、笔趣阁365。实测数据见 docs/站点规则实测.md。
//        · 「自动选最快」：按 搜索+目录+正文 的综合分选站，缓存 24 小时，
//          全失败自动回退到笔趣阁 PC 版。
//        · 性能修复：502 加入可重试、跳过必然失败的 .NET 回退（按主机记）、
//          删掉骗人的死字段 CrawlWorkers、HttpBatch 补 --compressed。
//        · 修掉 3 个站点一多就必然出错的隐患：搜索缓存没有站点维度、
//          跨站同名书会互相覆盖输出、来源表头把新站都印成"笔趣阁"。
// 1.4.3：目录缓存与断点续爬；站点配置.ini；缺失章节报告。
// 1.4.1：下载并发化（实测 5.57×，产物逐字节一致）；正文清洗不再误删正文
//        （原来 15 条正常语料里 11 条被破坏）；移动版广告与反爬水印清理；
//        清洗咽喉点前移（导出 EPUB/Markdown 不再带广告）；
//        EPUB 的 mimetype 改成 stored（合规）；错字检测加对照源探活；
//        `--selftest` 接上（用户手里那个 exe 自己就能跑测试）。
// 1.3.1：修掉目录遍历进度里写死的「共约 772 页」（数字自己打自己）。
// 1.3.0：AI 裁决错字（本地 Ollama / 云端 OpenAI 兼容，默认关闭）。
// 1.2.1：修掉"按钮文字被压成省略号"；标题版本号改成从程序集读。
[assembly: AssemblyVersion("1.5.1.0")]
[assembly: AssemblyFileVersion("1.5.1.0")]
[assembly: AssemblyInformationalVersion("1.5.1")]
