@echo off
rem ============================================================
rem  Build script for the novel downloader (no Visual Studio needed).
rem
rem  IMPORTANT: this file is kept pure ASCII on purpose.
rem  cmd.exe reads .bat files with the OEM codepage (GBK on zh-CN),
rem  so UTF-8 Chinese text inside a .bat gets mangled and can even
rem  split a command line in half (it eats the next line's %~dp0).
rem  All Chinese output is printed by the compiled programs instead.
rem
rem  Source layout:
rem    src\            UI entry (Program.cs, MainForm.cs, AssemblyInfo.cs)
rem    src\Common\      shared: models, http, cache, download runner
rem    src\Sites\       site adapters: biquga PC / biquga mobile / fanqie
rem    tests\           EdgeTest, TestMain (command line self tests)
rem    tools\           QuickDownload, DiagMobile (dev helpers)
rem ============================================================
setlocal
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" goto nocsc

if not exist "dist" mkdir "dist"

echo [1/6] Normalizing source encodings (.bat ASCII only, others UTF-8 with BOM)...
powershell -NoProfile -ExecutionPolicy Bypass -File "build\fix-encoding.ps1"
if errorlevel 1 goto failed

rem /codepage:65001 -> read sources as UTF-8 even without a BOM
set FLAGS=/nologo /platform:anycpu /optimize+ /codepage:65001 /nowarn:1591,0618
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll
set COMMON=src\Common\Models.cs src\Common\Http.cs src\Common\AppSettings.cs src\Common\DirCache.cs src\Common\FontMap.cs src\Common\DownloadRunner.cs
set SITES=src\Sites\BiqugaSite.cs src\Sites\BiqugaMobileSite.cs src\Sites\FanqieSite.cs src\Sites\TomatoCore.cs

echo [2/6] Building main program (GUI)...
"%CSC%" %FLAGS% /target:winexe /out:"dist\_build_tmp.exe" %REFS% ^
 src\AssemblyInfo.cs src\Program.cs src\MainForm.cs %COMMON% %SITES%
if errorlevel 1 goto failed

echo [3/6] Building command line self tests...
"%CSC%" %FLAGS% /target:exe /out:"dist\_selftest.exe" %REFS% ^
 tests\TestMain.cs %COMMON% %SITES%
if errorlevel 1 goto failed

"%CSC%" %FLAGS% /target:exe /main:EdgeTest /out:"dist\_edgetest.exe" %REFS% ^
 tests\EdgeTest.cs src\Program.cs src\MainForm.cs %COMMON% %SITES%
if errorlevel 1 goto failed

echo [4/6] Building offline unit tests (no network needed, used by CI)...
rem DirCache.KeyFor calls a static BiqugaSite helper, so the site sources are linked
rem in as well; the test itself performs no network access.
"%CSC%" %FLAGS% /target:exe /main:TomatoBiquga.OfflineTests /out:"dist\_offlinetests.exe" %REFS% ^
 src\AssemblyInfo.cs tests\OfflineTests.cs %COMMON% %SITES%
if errorlevel 1 goto failed

echo [5/6] Renaming GUI output (Chinese name, done in PowerShell)...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$d=Join-Path (Get-Location) 'dist'; $src=Join-Path $d '_build_tmp.exe'; $dst=Join-Path $d ([char]0x5C0F+[char]0x8BF4+[char]0x4E0B+[char]0x8F7D+[char]0x5668+'.exe'); Move-Item -LiteralPath $src -Destination $dst -Force; Write-Host ('OK -> ' + $dst)"
if errorlevel 1 goto failed

echo [6/6] Done.


echo.
echo Build succeeded. Output folder: dist
dir /b "dist\*.exe"
echo.
pause
exit /b 0

:nocsc
echo [ERROR] csc.exe not found. .NET Framework 4.x is required.
pause
exit /b 1

:failed
echo [ERROR] Build failed. See the messages above.
pause
exit /b 1
