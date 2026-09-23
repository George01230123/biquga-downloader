# ============================================================
#  启动小说下载器
#
#  两种目录结构都要认：
#    · 源码仓库里：exe 在 dist\ 下（build.bat 编出来就在那）
#    · Release 压缩包里：exe 和本脚本平铺在同一层
#
#  为什么用 .ps1 而不是 .bat：cmd.exe 用系统 OEM 代码页(GBK)解析 .bat，
#  而源码按 UTF-8 保存，.bat 里的中文文件名会被逐字节解释成乱码，
#  甚至连带把下一行的 %~dp0 一起弄坏（真实踩过的坑）。
#  本文件必须用 UTF-8 **带 BOM** 保存，PowerShell 靠 BOM 判定编码。
# ============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-Gui([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return $null }
    # 小说*.exe 是主程序；带 ^ 的是 Windows 复制重名时生成的（小说下载器^1.exe）；
    # NovelDownloader.exe 是 dotnet build 那一路的 ASCII 名字。
    Get-ChildItem -LiteralPath $dir -Filter '*.exe' -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -notlike '_*' -and
            ($_.Name -like '小说*' -or $_.Name -like '*^*' -or $_.Name -eq 'NovelDownloader.exe')
        } |
        Sort-Object Name | Select-Object -First 1
}

$exe = Find-Gui (Join-Path $root 'dist')
if (-not $exe) { $exe = Find-Gui $root }

if (-not $exe) {
    Write-Host "[ERROR] 没有找到下载器主程序（小说下载器.exe）。" -ForegroundColor Red
    Write-Host "        如果你在用源码：请先运行 build.bat 编译。" -ForegroundColor Yellow
    Write-Host "        如果你在用 Release 包：请确认 exe 和本脚本在同一层（或 dist 子目录里）。" -ForegroundColor Yellow
    Read-Host '按回车退出'
    exit 1
}

Write-Host "启动：$($exe.FullName)" -ForegroundColor Green
Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName
