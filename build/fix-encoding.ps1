# ============================================================
#  源码编码守护脚本（build.bat 第一步调用，CI 也会用）
#
#  为什么需要它：这个项目被“编码”坑过三次 ——
#    · 启动.bat 里写中文，被 cmd 用 GBK 解释成乱码，连 %~dp0 一起弄坏
#    · csc 读源码时按系统代码页解释，中文注释/字符串可能变乱码
#    · 编辑器（含各种自动化改写工具）会把 UTF-8 BOM 悄悄删掉
#  所以规则只有三条，写完就自动纠正，不靠人记得：
#    · 所有 .bat 必须纯 ASCII（绝不能有中文，中文放带 BOM 的 .ps1）
#    · 源码与脚本（.cs/.ps1/.yml）必须是 UTF-8 **带 BOM**（csc/PowerShell/YAML 都靠它判编码）
#    · 文档（.md）与 LICENSE 必须是 **不带 BOM** 的 UTF-8 ——
#      GitHub 渲染 Markdown 时会把 BOM 当正文，标题会掉成文件名（实测过的坑）
# ============================================================
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$bom = New-Object System.Text.UTF8Encoding($true)
$noBom = New-Object System.Text.UTF8Encoding($false)
$fixedCount = 0
$badBat = @()

$bomExt = @('.cs', '.ps1', '.psm1', '.yml', '.yaml')   # 需要 BOM
$plainExt = @('.md')                                   # 不能有 BOM
$files = Get-ChildItem -LiteralPath $root -Recurse -File |
         Where-Object { $_.FullName -notmatch '[\\/](\.git|dist|packages|bin|obj)[\\/]' }

foreach ($f in $files)
{
    $ext = $f.Extension.ToLowerInvariant()
    $isBat = ($ext -eq '.bat')
    $isLicense = ($f.Name -eq 'LICENSE' -or $f.Name -like 'LICENSE.*')
    if (-not $isBat -and -not $isLicense -and $bomExt -notcontains $ext -and $plainExt -notcontains $ext) { continue }
    try
    {
        $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
        if ($bytes.Length -eq 0) { continue }

        if ($isBat)
        {
            # .bat：只检查，不自动改（里面出现中文说明写法本身错了，得人工看）
            $nonAscii = 0
            foreach ($b in $bytes) { if ($b -gt 127) { $nonAscii++ } }
            if ($nonAscii -gt 0) { $badBat += ("{0}（{1} 个非 ASCII 字节）" -f $f.FullName.Substring($root.Length + 1), $nonAscii) }
            continue
        }

        $hasBom = ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        $wantBom = ($bomExt -contains $ext)      # 文档与 LICENSE 不要 BOM

        if ($wantBom -and -not $hasBom)
        {
            [System.IO.File]::WriteAllText($f.FullName, [System.Text.Encoding]::UTF8.GetString($bytes), $bom)
            $fixedCount++
            Write-Host ("  [fix] 补上 UTF-8 BOM: " + $f.FullName.Substring($root.Length + 1))
        }
        elseif (-not $wantBom -and $hasBom)
        {
            [System.IO.File]::WriteAllText($f.FullName,
                [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3), $noBom)
            $fixedCount++
            Write-Host ("  [fix] 去掉 UTF-8 BOM（Markdown 带 BOM 会让标题掉成文件名）: " + $f.FullName.Substring($root.Length + 1))
        }
    }
    catch
    {
        Write-Host ("  [warn] 处理失败（跳过）：" + $f.FullName + " -> " + $_.Exception.Message)
    }
}

if ($badBat.Count -gt 0)
{
    Write-Host "[ERROR] 以下 .bat 含非 ASCII 字符，必须改成纯 ASCII（中文请写进带 BOM 的 .ps1）："
    foreach ($b in $badBat) { Write-Host ("        " + $b) }
    exit 1
}

if ($fixedCount -gt 0) { Write-Host ("  编码已修正：{0} 个文件" -f $fixedCount) }
else { Write-Host "  源码编码检查通过（源码/脚本 UTF-8 带 BOM，文档/License 不带 BOM，.bat 纯 ASCII）" }
exit 0
