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
// 1.5.4：修掉「有的词搜不出来」—— 关键词检查整批误杀真结果。
//        · 现象（用户报"搜万妖圣祖搜不到"）：同一个站，搜《万相之王》能搜到、
//          搜《万妖圣祖》就"无结果"，用户完全无法理解。
//        · 实测真相：笔尖中文搜「万妖圣祖」时结果页里**有 50 条书籍链接**，
//          但页面**不回显关键词**（站点是模糊匹配，结果标题里也不含那四个字）
//          → 被 SearchResultMustContainKeyword 判成"无结果"，50 条全丢。
//        · 根因：那个检查拿"整页 HTML 里有没有这几个字"当作
//          "站点有没有按关键词检索"，而前者只等于"站点有没有回显搜索框内容"。
//          它本来是为挡梦书中文（搜什么都返回《长乐歌》）加的，但对正常站点
//          会误杀。→ 关掉（梦书中文本来就是被排除的源）。
//        · 顺带：UiInvoke 原来 `catch { }` 静默吞掉界面刷新异常，
//          导致"日志说搜到 N 条、列表却是空的"这种排查地狱。现在会记日志。
//        · 顺带：任务队列搜到多条时原来无脑取 found[0]（静默下错书），
//          改成按标题精确匹配，对不上就跳过并列出候选。
// 1.5.3：修掉**界面日志区中文变方框**（1.5.2 修的是控制台，不是这个）。
//        · 上一版方向判错了：用户截图里 ASCII 完全正常（18:39:21、curl、
//          settings.ini），**只有中文是方框** —— 那是 tofu，不是乱码；
//          乱码的话 ASCII 会一起坏。方框的成因是**字体里没有那个字的字形**。
//        · 真凶：日志控件写死了 `new Font("Consolas", 8.5F)`，
//          **Consolas 是纯拉丁字体、不含任何汉字字形**，平时靠 Windows 的
//          字体链接(font linking)兜底，那在部分机器上不生效。
//          实测（GDI GetGlyphIndicesW 数 0xFFFF = 缺字形）：
//            Consolas            30/30 字缺字形 → 全变方框
//            Microsoft YaHei UI   0/30        → 全覆盖
//            MS Gothic           10/30        → 部分缺
//        · 修法：新增 src/Common/UiFont.cs，按"优先等宽、但不牺牲中文"
//          挑字体并带回退；把两处写死的 Consolas（日志区、多本书输入框）换掉。
//        · 加了 10 条回归断言（TestUiFont），其中直接挖 MainForm 的 txtLog
//          字段查它实际用的字体 —— 故意改回 Consolas 实测会红 5 条。
// 1.5.2：修掉**控制台**中文乱码 + GUI --selftest 无输出。
//        · 根因：程序写的是**正确的 UTF-8 字节**，但 Windows 控制台默认按
//          GBK(936) 解码 → 乱码。而"真实控制台"与"重定向到文件"是**两条
//          不同的编码路径**，原来只处理了后者（只设 Console.OutputEncoding）：
//            重定向     → .NET 按 OutputEncoding 编码字节    ✔ 一直是对的
//            真实控制台 → Windows 按**控制台代码页**翻译    ✘ 936 下全是乱码
//        · 修法：新增 ConsoleSetup.UseUtf8()，把**控制台代码页**（Win32
//          SetConsoleOutputCP）与 Console.OutputEncoding **一起**切到 UTF-8，
//          并统一到全部 9 个入口（GUI + 8 个测试/探针 exe）。
//        · 顺带修掉 GUI 的 `--selftest` **一个字都不输出**：
//          winexe 进程没有控制台，Windows 还会把它的 stdout 句柄置空
//          （实测 GetStdHandle 返回 0、AttachConsole 返回 ACCESS_DENIED）。
//          现在改成：能挂父控制台就挂；挂不上就把结果**同时**写一份
//          "自测结果.txt"（exe 同目录，不可写则退 %TEMP%）。
//        · 弹窗提示只在"真的没有控制台窗口"时才弹 —— 否则模态框会把
//          自动化流程挂死（实测挂过 573 秒才被强杀）。
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
[assembly: AssemblyVersion("1.5.4.0")]
[assembly: AssemblyFileVersion("1.5.4.0")]
[assembly: AssemblyInformationalVersion("1.5.4")]
