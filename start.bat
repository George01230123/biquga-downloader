@echo off
rem ============================================================
rem  Launcher -- pure ASCII on purpose.
rem  This .bat only hands over to start.ps1 (UTF-8 BOM, so
rem  PowerShell reads the Chinese paths correctly).
rem  Never write Chinese into a .bat: cmd.exe parses .bat with
rem  the OEM codepage (GBK on zh-CN), so UTF-8 Chinese bytes get
rem  mangled and can even corrupt the next line (%~dp0).
rem ============================================================
chcp 65001 >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1"
