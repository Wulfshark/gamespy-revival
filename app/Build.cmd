@echo off
rem Builds GameSpyRevival.exe with the C# compiler that ships with Windows. Output: ..\GameSpyRevival.exe
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo C# compiler not found > build.log & exit /b 1)
"%CSC%" /nologo /target:winexe /optimize+ /out:..\GameSpyRevival.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /resource:DINPUT.dll,DINPUT.dll /resource:mfnet-relay.exe,mfnet-relay.exe /resource:gamespy-lobby.exe,gamespy-lobby.exe Revival.cs Ui.cs Lobby.cs Voice.cs VoiceUi.cs Directory.cs DiscordUi.cs Shell.cs Wizard.cs Invite.cs MetalFatigue.cs > build.log 2>&1
echo exit=%errorlevel% >> build.log
