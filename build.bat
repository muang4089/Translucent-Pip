@echo off
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize /out:"Translucent Pip.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll TranslucentPip.cs
