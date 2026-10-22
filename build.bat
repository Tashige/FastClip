@echo off
rem Builds FastClip.exe with the C# compiler that ships with Windows.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0FastClip.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0FastClip.cs"