@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find .NET Framework C# compiler.
  pause
  exit /b 1
)
echo Building HasladDNS v1...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:HasladDNS_v1.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.Xml.Serialization.dll /reference:System.Runtime.Serialization.dll /win32manifest:app.manifest /win32icon:HasladDNS.ico HasladDNS_v1.cs
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo.
echo Build complete: HasladDNS_v1.exe
echo Run it as Administrator to change DNS settings.
pause
