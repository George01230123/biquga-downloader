# 发到 GitHub 前的准备清单

> 结论先说：**代码本身已经值得开源**（零依赖、实测数据完整、踩坑记录详细），
> 但**当初直接传上去会有 3 个会被人当场发现的问题**，先按下面的 P0 修完再 push。

---

## 〇、当前进度（本清单里的项大部分已经落地并实测）

| # | 事项 | 状态 | 证据 |
| --- | --- | --- | --- |
| P0-1 | `启动.bat` 编码坏掉，clone 下来第一步就失败 | ✅ 已修 | 改为 `start.bat`（纯 ASCII，0 个非 ASCII 字节）+ `start.ps1`（UTF-8 带 BOM）；仓库内 `dist\` 布局和 Release 平铺布局**都实测能起来** |
| P0-2 | `fanqie-core/config.yml` 泄漏个人绝对路径 | ✅ 已排除 | `.gitignore` 忽略整个 `fanqie-core/`；`TomatoCore.cs` 里硬编码的 `E:\挂\...` 已删 |
| P0-3 | 9.83 MB 第三方 exe + 57 MB 小说正文进仓库的风险 | ✅ 已排除 | `git add -A --dry-run` 只列 33 个文件：源码 + 文档 + 脚本，无 exe / 无 txt / 无缓存 |
| P1 | 目录结构、AssemblyInfo、csproj、CI | ✅ 已做 | `src/ tests/ tools/ build/`；`dotnet build` 本机实测 0 error；`.github/workflows/ci.yml` |
| P1 | 编码守护（防止 BOM 被编辑器吃掉） | ✅ 已做 | `build\fix-encoding.ps1`，build.bat 第一步自动修正并拒绝含中文的 `.bat` |
| P2 | 失败章节报告 + 并发可配 | ✅ 已做 | `.缺失章节.txt`（章节号/原因/地址）；界面「设置」写 `settings.ini` |
| P2 | 不联网的离线单测 | ✅ 已做 | `_offlinetests.exe`，**161 项全绿、exit 0**，CI 直接跑 |
| P2 | 发现并修掉 2 个真 bug | ✅ 已修 | ① 表头变长会吃掉正文开头（真实 10.4 MB 文件实测丢 38 字）；② `Chapters` 未初始化导致程序化调用 NRE |
| P2 | LICENSE / 命名 / 版权人占位符 | ✅ 已定 | `LICENSE` = MIT + 中文补充说明；产品名 `novel-downloader`，窗口标题「小说下载器 v1.0」；`AssemblyInfo` 版权人 = `novel-downloader contributors` |
| P3 | 其余（EPUB、插件化、异步重写） | ⬜ 待做 | 见文末优先级 |

### ✅ 已经发布完成（2026-09-23）

| 项目 | 结果 |
| --- | --- |
| 仓库 | https://github.com/George01230123/biquga-downloader （public，默认分支 main） |
| Release | **v1.0.1** —— https://github.com/George01230123/biquga-downloader/releases/tag/v1.0.1 |
| 附件 | `novel-downloader-v1.0.1-win64.zip`（56,219 字节，6 个文件） |
| CI | run #1~#6 **全部 success**（build + 离线单测 161 项 + release 打包自检） |
| 协议识别 | GitHub 显示 **MIT** ✅（LICENSE 保持标准原文；说明挪到 `NOTICE.md`） |
| 端到端验证 | 走 API 把 release 附件下回来 → 解压 → 双击 `start.bat` → 界面起来 ✅ |

发布过程中踩到的三个坑（都已修）：

1. **中文描述变成乱码**：PowerShell 的 `ConvertTo-Json` + `curl --data-binary` 会把中文按系统
   代码页(GBK)编码，GitHub 收到是「绗旇叮闃佸皬璇?」。必须显式 `UTF8Encoding($false)` 写成字节再传。
2. **`license=NOASSERTION`**：MIT 原文后面追加自定义说明会让 GitHub 的 licensee 认不出来。
   拆成 `LICENSE`（标准原文）+ `NOTICE.md`（中文补充 + 免责）后识别为 MIT。
3. **Release 包漏文件**：v1.0.0 的包里没有 `NOTICE.md`（它是在打 tag 之后才建的）。
   现在 CI 增加 `Verify package contents` 步骤：**包内必须正好 6 个文件，多了少了直接失败**。
   另外 `v1.0.0` 那个空 release 已删除，只保留 v1.0.1，避免用户下到空包。

> 环境提示：这台机器上 `github.com` 会间歇性连不上（`curl: (28) Failed to connect ... after 21s`），
> 但 `api.github.com` 一直通（0.2 秒）。push 失败时先等一会或重试，release 附件也能走 API 直链下载。

### 后续发版的步骤

```powershell
cd "D:\harness work\TomatoBiquga"

# 0) 自检（应输出 通过 161/161，退出码 0）
.\dist\_offlinetests.exe

# 1) 改版本号：src\AssemblyInfo.cs 三处 + MainForm 标题
# 2) 提交并推送
git add -A
git commit -m "fix: ..."
git push origin main

# 3) 打 tag → CI 自动编译 + 包内容自检 + 挂到 Release
git tag v1.0.2
git push origin v1.0.2
```

**仓库设置**（已完成，记录备查）：

| 位置 | 内容 |
| --- | --- |
| About → Description | 笔趣阁小说下载器：中文书名搜索 → 勾选章节 → 导出 UTF-8 TXT。免安装单文件 exe，零运行时依赖。（已填） |
| About → Topics | `biquga` `csharp` `net-framework` `novel-downloader` `scraper` `txt` `winforms`（已填） |
| Settings → Features | Wiki / Projects / Discussions 全部关闭（已设） |
| Issue 模板 | ⬜ 还没加。建议要求填：版本号、站点、书名、目录页 URL、日志末 50 行 |

---

## 一、我实际跑出来的问题（不是猜测）

### 🔴 P0-1：`启动.bat` 在新机器上根本跑不起来

README 第 1 步就是「双击 `启动.bat`」，而它现在是坏的。实测：

```
> cmd /c "D:\harness work\TomatoBiquga\启动.bat"
'p0"' is not recognized as an internal or external command,
The system cannot find the path specified.
```

原因：文件是 **UTF-8（无 BOM）**，里面写了中文的 `小说下载器.exe`。
`cmd.exe` 用系统 OEM 代码页（简体中文 = GBK）解析 `.bat`，UTF-8 的中文字节被当成 GBK
逐字节解释后变成乱码，而且**乱码字节把下一行的 `%~dp0` 也吃掉了**——
所以 `cd /d "%~dp0"` 变成了 `cd /d "` + 垃圾，整条命令行从中间断开。

这正好和 README 第六节第 1 条写的坑是同一个坑，只是当时只改了 `build.bat`，
`启动.bat` 漏了。**任何人 clone 下来第一步就失败，而且失败得很莫名其妙。**

### 🔴 P0-2：`fanqie-core/config.yml` 里是你的个人绝对路径

```
save_path: 'D:/harness work/TomatoBiquga/fanqie-core/下载'
```

`TomatoCore.InstallBundled()` 是按 `AppDomain.CurrentDomain.BaseDirectory` 现算的，
所以这个文件**根本不该提交**（也不能提交，换台机器路径就不对）。

### 🔴 P0-3：`fanqie-core/TomatoNovelDownloader.exe`（9.83 MB）是别人的程序

这个 exe 不是你的代码，是第三方（`dl.zhongbai233.com`）发布的成品。
**把它放进仓库 = 再分发别人的二进制**，你既没有授权也没有它的 LICENSE，
这是最容易被举报/被下架的一条。同理：

| 不该提交 | 体积 | 原因 |
| --- | --- | --- |
| `fanqie-core/TomatoNovelDownloader.exe` | 9.83 MB | 第三方二进制，无再分发授权 |
| `fanqie-core/logs/latest.log` | 10 KB | 日志，含本机信息 |
| `dist/cache/*.json` | ~700 KB | 你的抓取缓存，含站点数据 |
| `dist/下载/**/*.txt` | **~57 MB** | 全本小说正文，版权内容 |
| `dist/_selftest.exe` / `_edgetest.exe` / `_dl.exe` / `_diag.exe` | ~310 KB | 应该由 `build.bat` 现场生成 |
| `_msj.txt` / `_qzg.txt` / `_cyt.txt` / `_sjgs*.txt` | ~10 KB | 我调的临时日志 |
| `fanqie-core.ini` | - | 运行期生成，记的是本机 exe 路径 |

那 4 本已下载的书（牧神记 9.98 MB / 全职高手 15.52 MB / 超神宠兽店 14.05 MB /
神级高手在都市 17.59 MB）**千万不要进 git**。只要进过一次，历史里就永远在。

---

## 二、P0 修复方案（我可以直接帮你改）

### 1. 启动器改成 Unicode 安全的做法

- 删掉 `启动.bat`（中文名 + .bat 的组合天生危险，`CD 启动.bat` 之类写法还会踩坑）
- 新增 **`start.bat`（纯 ASCII，只负责转交）** → 调 `powershell -File start.ps1`
- 新增 **`start.ps1`（UTF-8 **带 BOM** 保存）**：PowerShell 用 BOM 判定编码，
  中文路径不会被 GBK 解释；它在 `dist\` 里用通配找 GUI（`小说*.exe` / `*^*.exe`），
  所以 exe 名字以后改了也不会失效
- `build.bat` 里加一道**自检**：如果任何 `.bat` 含非 ASCII 字节就直接报错退出

> **已经改好并实测通过了**：`start.bat` 全 ASCII（0 个非 ASCII 字节），
> 双击后输出 `启动：D:\harness work\TomatoBiquga\dist\小说下载器.exe`，
> 进程真起来了，窗口标题 `番茄 / 笔趣阁 小说下载器（免安装单文件版）`。

### 2. 明确「不提交哪些东西」

新增 `.gitignore`（我按仓库结构写好了，见第五节）+ `.gitattributes` 统一换行符：

```
* text=auto eol=lf
*.cs   text eol=lf
*.bat  text eol=crlf      # .bat 必须 CRLF，且只能 ASCII
*.ps1  text eol=crlf
*.yml  text eol=lf
*.exe  binary
*.txt  text eol=lf
```

`fanqie-core` 目录**整体忽略**，改为在 README 里说明：
「番茄下载能力需要你自己准备原版工具；工具会引导你选 exe 并自动装到 `fanqie-core\`」。
这样仓库干净、无授权风险，功能一点不少（`InstallBundled()` 本来就是这个流程）。

### 3. 改仓库名 + 加免责声明

- 名字里有 **Tomato** / **番茄** 是字节跳动的商标，仓库名别用。建议：
  `biquga-downloader`（准确、好搜）或 `novel-txt-exporter`
- License 建议 **MIT + 免责声明段**，明确写：
  - 本工具**不破解任何 DRM、不绕过登录/付费**，只解析公开可访问的网页
  - 作者**不提供、不托管任何小说正文**，下载内容版权归原作者/站点
  - 请仅作个人离线阅读，24 小时内删除，勿传播
  - 使用者自行承担合规责任
- README 顶部放同一段（**中文 + 英文各一份**，GitHub 受众两种都有）
- 站点相关章节措辞收敛一点：别叫「官方工具」，叫「第三方工具」
  （本质上就是别人的下载器，而且我们对它做了逆向和本地 API 对接）

---

## 三、建议的仓库结构

```
/
├─ README.md              # 中文主文档
├─ README.en.md           # 英文版（可选，但很加分）
├─ LICENSE                # MIT + 免责声明
├─ .gitignore  .gitattributes  .editorconfig
├─ start.bat  start.ps1   # 启动（ASCII + BOM 各司其职）
├─ build.bat  build.ps1   # 编译（ASCII）
├─ docs/
│   ├─ GITHUB准备清单.md   # 本文件
│   ├─ 逆向笔记.md         # 从 README 第二节搬过来
│   ├─ 站点坑.md           # 从 README 第三节 + 第七节搬过来
│   ├─ 架构.md             # 模块图 + 数据流
│   └─ images/screenshot.png
├─ src/                   # 全部 .cs（现在堆在根目录）
├─ tests/
│   ├─ EdgeTest.cs        # 44 项（含离屏 MainForm 真下载测试）
│   └─ OfflineTests.cs    # ★ 新增：不联网的纯函数单测（给 CI 用）
└─ dist/                  # build 产物，只提交 .gitkeep 或整目录忽略
```

`dist/` 建议**整个忽略**，release 时用 Actions 现编现打包（见 P1-4）。

---

## 四、代码层面值得顺手处理的地方（都不大）

| 优先级 | 位置 | 建议 |
| --- | --- | --- |
| 中 | `TomatoCore.cs:116` | 硬编码了你的旧路径 `E:\挂\番茄小说下载导出工具\...`（**我已经删掉**）。这类「我这台机器上的路径」上传前搜一遍 `D:\` `E:\` `C:\Users` |
| 中 | `MainForm.cs`（45 KB / 1000+ 行） | 至少把「站点差异」抽成接口（现在 `if (site == 移动版)` 散在各处）。拆成 `MainForm.cs` + `Controls/` |
| 中 | `BiqugaMobileSite.Workers = 8`、`MainForm` 里的 `Workers = 8/6` | 魔法数字提到设置界面（现在有「设置」按钮，正好用） |
| 中 | 并发下载 | `Thread.Sleep` 串行节奏 → 建议改成「任务队列 + 有限并发」，失败章节单独进重试队列并**在收尾时报告「哪些章没下到」**（现在只报总数，用户不知道缺哪几章，这是最容易被开 issue 的点） |
| 低 | 命令行参数 | 现在 GUI 是唯一入口。加 `--selftest` / `--download <dir> --count N` / `--no-gui`，别人报 bug 时你能让他跑一条命令复现 |
| 低 | `EDGE` 测试 | `_selftest.exe` 的 4 个模式全都要联网；CI 跑不了。把纯函数（BOM 写入、表头、广告剥离、分页残留清洗、`SafeFileName`、缓存键往返）抽成不联网的 `OfflineTests` |

### 版本信息别忘了

csc 直接编译会生成 `0.0.0.0` 版本号。加一个 `src/AssemblyInfo.cs`：

```csharp
[assembly: AssemblyTitle("小说下载器")]
[assembly: AssemblyProduct("novel-downloader")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
```

否则用户右键属性看到的是一片空白，issue 里问「你用的哪个版本」都答不上来。

### 关于 CI（GitHub Actions）

`csc.exe` 在 GitHub 的 Windows runner 上**不保证存在**（传统 .NET Framework 已不是默认组件）。
两种稳妥做法，选一个：

1. **`Microsoft.NETFramework.ReferenceAssemblies` 包 + SDK 风格 csproj**
   写一个 `src/NovelDownloader.csproj`（`<TargetFramework>net48</TargetFramework>`，
   `LangVersion 7.3`，把 `System.Web.Extensions` 作为 Reference 引进来），
   然后 `dotnet build` / `msbuild` 都能编，任何平台都能跑 CI。
2. 保留 `build.bat` 作为「零依赖本地编译」的备用路径（这是这个项目的卖点，别丢）

`.github/workflows/ci.yml` 骨架：

```yaml
name: ci
on: [push, pull_request]
jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.x' }
      - run: nuget install Microsoft.NETFramework.ReferenceAssemblies -OutputDirectory packages
      - run: dotnet build src/NovelDownloader.csproj -c Release
      - run: dist/_edgetest.exe            # 离线自测，返回非 0 即失败
      - run: dist/_offlinetests.exe        # ★ 建议新增的纯单测
```

---

## 五、`.gitignore`（可直接用）

```gitignore
# ---- 编译产物（release 由 Actions 现场生成）----
dist/*
!dist/.gitkeep
*.pdb  *.user  *.suo  bin/  obj/  packages/

# ---- 第三方工具：不随仓库分发（无再分发授权，且本机路径写死在配置里）----
fanqie-core/
fanqie-core.ini

# ---- 运行期数据：下载的正文有版权，缓存含站点数据，日志含本机信息 ----
下载/
dist/下载/
dist/cache/
*.log
logs/

# ---- 我的临时调试产物 ----
_*.txt  _*.exe  /tmp/
```

---

## 六、README 怎么改（这块投入产出比最高）

现在的 README 内容很好，但**是写给你自己看的**（15 KB 全塞在一个文件里，
读者 30 秒内看不到「这东西能干嘛」）。建议：

1. **开头 3 行讲清楚**：一句话是什么 + 一张截图/GIF + 一条命令能跑
2. **加「特性对比」表**（和同类工具的差异就是你的卖点）：
   - 免安装单文件 exe、零运行时依赖（连 Node.js 都不要）
   - 移动版目录页并发抓取：1000 章约 10 分钟（PC 串行版要 2 小时）
   - 正文自动清洗：行尾广告 / 分页残留 / base64 解码
   - 断点续传：每 20 章落盘、可取消
   - 输出 UTF-8 BOM TXT，记事本 / 手机阅读器直接打开
3. **实测数据单独一节**（你已经有很好的数据了，这是可信度来源）：
   牧神记 1067 章 578 秒 / 全职高手 1763 章 927 秒 / 神级高手在都市 2308 章 1441 秒
4. **把「踩过的坑」单独成文**放 `docs/`，README 里只留链接。
   这篇文档是这个项目最有技术含量的部分（TLS 指纹、next 链接成环、GBK 编码踩坑……），
   它比代码本身更能吸引人 star
5. **加「已知限制」+「如何自己查缺章」**，把「站方本来就缺章」这个结论写清楚
   （牧神记缺 40 章、超神宠兽店缺 12 章都是站点侧空内容，你逐章验证过）
6. **加「本工具为什么不做 XX」**：不内置搜索接口绕过、不提供正文下载源，避免被当成盗版工具

---

## 七、发布与运营

- **打 tag** `v1.0.0`，Release 里放 `novel-downloader-1.0.0-win64.zip`
  （内容：`小说下载器.exe` + `README.md` + `LICENSE`，**不含** `fanqie-core` 和任何 txt）
- Actions 里加 tag 触发的自动发布（`softprops/action-gh-release`）
- 仓库 About 里填描述 + topics：`csharp` `winforms` `net-framework` `novel` `scraper` `biquga` `downloader`
- 开 Issue 模板：`bug_report.yml`（要求填版本号、站点、书名、目录页 URL、日志末 50 行）
  ——「缺哪几章」这类问题没有模板你会被问死
- 如果不想处理 issue，就把 Discussions 关掉，README 里写清楚「仅接受 PR，不答疑」

---

## 八、一句话优先级

1. **P0**：修 `启动.bat`（已修好）、加 `.gitignore`、把 `fanqie-core` 和 `下载` 排除、改仓库名、加免责声明
2. **P1**：`.gitattributes` + `.editorconfig`、`AssemblyInfo.cs`、`start.ps1` 方案落地、README 重写成「3 行入门 + 详细文档」、CI 跑离线自测
3. **P2**：卸掉硬编码路径（已做）、抽出 `ISite` 实现、并发可配、失败章节报告、CLI 参数
4. **P3**：EPUB 输出、有声书、设置界面、多站点插件
