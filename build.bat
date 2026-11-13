@echo off
rem Builds ForeverManager.exe with the C# compiler that comes with Windows
rem (.NET Framework 4.8). No download, no package, nothing else to install.
rem make_secrets.ps1 puts in the token (..\token.txt) and the CurseForge key
rem (..\curseforge-key.txt), masked; the file it writes is deleted after.
rem Out: ..\dist\ForeverManager.exe; give a path to build somewhere else:
rem build.bat C:\somewhere\ForeverManager.exe
setlocal
cd /d "%~dp0"
set OUT=%~dp0..\dist\ForeverManager.exe
if not "%~1"=="" set OUT=%~1
for %%D in ("%OUT%") do if not exist "%%~dpD" mkdir "%%~dpD"
set SECRETS=%TEMP%\fm-secrets-%RANDOM%%RANDOM%.cs
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make_secrets.ps1" -Out "%SECRETS%"
if errorlevel 1 goto failed
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ ^
  /out:"%OUT%" ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
  /reference:System.Web.Extensions.dll /reference:System.Numerics.dll /reference:System.Security.dll ^
  /win32icon:ForeverManager.ico /resource:ForeverManager.ico,app.ico ^
  ForeverManager.cs "%SECRETS%"
set BUILT=%ERRORLEVEL%
del "%SECRETS%" 2>nul
if not "%BUILT%"=="0" goto failed
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make_secrets.ps1" -Check "%OUT%"
if errorlevel 1 goto failed
echo Built %OUT%
exit /b 0
:failed
echo Build failed.
exit /b 1
