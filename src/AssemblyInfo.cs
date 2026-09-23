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
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
