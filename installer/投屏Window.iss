; 投屏Window 安装包脚本（Inno Setup 6）
; 编译： "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\投屏Window.iss

#define AppName "投屏Window"
; 版本号默认值。**单一来源是 ..\Directory.Build.props 的 <Version>**，
; build.ps1 会用 /DAppVersion=x.y.z 覆盖这里，不要手工同步两处。
#ifndef AppVersion
  #define AppVersion "2.1.0"
#endif
#define AppPublisher "猪猪"
#define AppExe "投屏Window.exe"
#define AppId "{{8E31B7A4-2C5D-4F1B-9A77-6D5C4B3A2101}"

; ---------------------------------------------------------------------------
; 开场视频（Assets\intro\builtin-intro.mp4，约 191MB）是否打进安装包。
; **默认不含**：视频几乎压不动，打进去要多花好几分钟、安装包也从 ~60MB 涨到 ~250MB。
; build.ps1 -Installer 默认传 /DNOINTRO；要带就加 -InstallerWithIntro。
; 注意：绿色版（发布版\投屏Window）**始终**带这份视频，这里只影响安装包。
; 安装后程序找不到素材会自己跳过入场动画（IntroClips.ResolvePath 返回空即跳过）。
; ---------------------------------------------------------------------------
#ifdef NOINTRO
  #define IntroExcludes "Assets\intro\*"
#else
  #define IntroExcludes ""
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments=手机镜像接收端（Miracast / Wi-Fi Display）
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
OutputDir=..\发布版
OutputBaseFilename=投屏Window-Setup-{#AppVersion}
SetupIconFile=..\src\CastReceiver.App\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoCopyright=版权所有 (C) 2026 {#AppPublisher}

; 安装/卸载时若程序正在运行，靠程序自己的单实例互斥体识别（名字见 App.xaml.cs：
; "CastReceiver.App.SingleInstance" + 实例后缀，正常启动时后缀为空）
AppMutex=CastReceiver.App.SingleInstance

; 安装前展示最终用户许可协议。
; ⚠️ 该文件必须存为 **UTF-8 with BOM**，否则 Inno 会按 ANSI 读、中文显示成乱码。
LicenseFile=..\docs\EULA.txt

; ---------------------------------------------------------------------------
; 代码签名钩子：默认**不签名也能构建出安装包**；要签名时由 build.ps1 -Sign 传入
;   /DSIGN /Stsign="<完整的 signtool 命令行，用 $f 代表待签文件>"
; 例如：
;   /Stsign="""C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe"" sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f cert.pfx /p 密码 $f"
; 没有证书时请保持不启用，否则安装包会生成失败。
; ---------------------------------------------------------------------------
#ifdef SIGN
SignTool=tsign
SignedUninstaller=yes
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupAppTitle={#AppName} 安装程序
SetupWindowTitle={#AppName} {#AppVersion} 安装程序
WelcomeLabel1=欢迎安装 [name]
WelcomeLabel2=即将在你的电脑上安装 [name/ver]。%n%n这是一个手机镜像接收端：手机与电脑连同一个 Wi-Fi，用手机自带的「无线投屏 / 投射屏幕」即可把画面镜像到电脑上。%n%n截图与录像会以加密形式保存到本机（只有本程序内、输入账号密码才能查看）；投屏与录制本身不需要密码。%n%n安装包已内含运行环境（.NET 10 桌面运行时 + WPF），装完即可直接使用，无需另外安装任何组件。%n%n继续前请关闭正在运行的 {#AppName}。
SelectDirLabel3=安装程序将把 [name] 安装到下面的文件夹。
SelectDirBrowseLabel=点击「下一步」继续；如需更改位置，点击「浏览」。
SelectStartMenuFolderLabel3=安装程序将在下面的开始菜单文件夹中创建快捷方式。
DiskSpaceMBLabel=至少需要 [mb] MB 的磁盘空间。
ReadyLabel1=安装程序已准备好开始安装 [name]。
ReadyLabel2a=点击「安装」开始安装；如需检查或更改设置，点击「上一步」。
InstallingLabel=正在安装 [name]，请稍候…
FinishedLabel=安装完成，现在就可以直接使用了。%n%n从开始菜单或桌面快捷方式启动 [name]：双击图标后约半秒就会出现启动动画。%n%n首次使用请点界面上的「一键准备」放行防火墙（需要同意一次管理员授权）。
FinishedLabelNoIcons=安装完成。%n%n可以从开始菜单启动 [name]。
ClickFinish=点击「完成」结束安装。
ButtonNext=下一步(&N) >
ButtonBack=< 上一步(&B)
ButtonInstall=安装(&I)
ButtonCancel=取消
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&R)…
ButtonYes=是(&Y)
ButtonNo=否(&N)
UninstallAppFullTitle=卸载 %1
ConfirmUninstall=确定要完全卸载 %1 及其所有组件吗？
UninstalledAll=%1 已成功卸载。
UninstallStatusLabel=正在卸载 %1，请稍候…
SetupFileCorruptOrWrongVer=安装包已损坏，或与当前安装程序版本不匹配。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
Source: "..\发布版\投屏Window\*"; DestDir: "{app}"; Excludes: "{#IntroExcludes}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 法务文本随包安装，「关于」页里有入口可直接打开
Source: "..\docs\EULA.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\隐私政策.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\第三方声明.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

; ============================================================================
; 已经安装过？先问清楚是要卸载还是要覆盖安装（修复/更新）
;   是   = 卸载已安装的版本，然后退出安装程序
;   否   = 直接覆盖安装（保留设置与加密库，加密库在 %LocalAppData% 里不受影响）
;   取消 = 什么都不做
; ============================================================================
[Code]
function FindUninstallIn(Root: Integer; out Value: String): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Sub: String;
  Disp: String;
  Uninst: String;
begin
  Result := False;
  Value := '';
  if not RegGetSubkeyNames(Root, 'Software\Microsoft\Windows\CurrentVersion\Uninstall', Names) then Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    { 只认 Inno 自己的卸载项（键名以 _is1 结尾），再按显示名确认是本程序 }
    if Pos('_is1', Names[I]) = 0 then Continue;
    Sub := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + Names[I];
    if not RegQueryStringValue(Root, Sub, 'DisplayName', Disp) then Continue;
    if Pos('投屏Window', Disp) = 0 then Continue;
    if not RegQueryStringValue(Root, Sub, 'UninstallString', Uninst) then Continue;
    if Uninst = '' then Continue;
    Value := Uninst;
    Result := True;
    Exit;
  end;
end;

function GetUninstallString(): String;
var
  Value: String;
begin
  Result := '';
  { 先看当前用户（本程序是免管理员安装），再看全局 }
  if FindUninstallIn(HKCU, Value) then begin Result := Value; Exit; end;
  if FindUninstallIn(HKLM, Value) then begin Result := Value; Exit; end;
end;

function InitializeSetup(): Boolean;
var
  Choice: Integer;
  UninstallCmd: String;
  ResultCode: Integer;
begin
  Result := True;
  UninstallCmd := GetUninstallString();
  if UninstallCmd = '' then Exit;      // 没装过：正常安装

  Choice := MsgBox('检测到本机已经安装过 {#AppName}。' + #13#10 + #13#10 +
                   '是   —— 先卸载已安装的版本，然后继续安装这一个（推荐）' + #13#10 +
                   '否   —— 不卸载，直接覆盖安装（设置与加密库都会保留）' + #13#10 +
                   '取消 —— 退出，什么都不做',
                   mbConfirmation, MB_YESNOCANCEL);

  if Choice = IDCANCEL then
  begin
    Result := False;
    Exit;
  end;

  if Choice = IDYES then
  begin
    UninstallCmd := RemoveQuotes(UninstallCmd);
    if Exec(UninstallCmd, '/SILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      // 卸载完成后继续安装新版本（一次运行就完成「先卸载旧的、再装新的」）
      Result := True;
      Exit;
    end
    else
    begin
      MsgBox('调用卸载程序失败，请到「设置 → 应用 → 已安装的应用」里手动卸载。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;
