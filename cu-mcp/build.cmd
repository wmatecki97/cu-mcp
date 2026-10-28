@echo off
rem Builds cu-mcp.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist "%FW%\csc.exe" (echo .NET Framework 4.x csc.exe not found & exit /b 1)
rem Running servers lock cu-mcp.exe, but Windows still lets it be renamed, so move it aside. They keep the old code until restarted.
if exist "%~dp0cu-mcp.exe" del "%~dp0cu-mcp.exe" >nul 2>&1
if exist "%~dp0cu-mcp.exe" ren "%~dp0cu-mcp.exe" cu-mcp.old-%RANDOM%.exe
"%FW%\csc.exe" /nologo /optimize+ /target:exe /platform:anycpu /out:"%~dp0cu-mcp.exe" ^
  /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  "%~dp0cu-mcp.cs"
if errorlevel 1 exit /b 1
echo Built %~dp0cu-mcp.exe
