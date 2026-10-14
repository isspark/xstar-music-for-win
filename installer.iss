; Xstar Music - Inno Setup 安装脚本
; 编译: ISCC.exe installer.iss

#define MyAppName "Xstar Music"
#define MyAppExeName "Xstar.Music.App.exe"
#define MyAppVersion "1.3.1"
#define MyAppPublisher "Xstar"

; 可选：把 WebView2 Evergreen 引导程序放到 installer\MicrosoftEdgeWebView2Setup.exe，
; 安装时会静默安装；缺失时安装程序会提示打开下载页面。
#define WebView2BootstrapPath AddBackslash(SourcePath) + "installer\MicrosoftEdgeWebView2Setup.exe"
#if FileExists(WebView2BootstrapPath)
  #define HaveWebView2Bootstrapper
#endif

[Setup]
AppId={{8E4C2A5F-6D3B-4A9E-9B1C-7F2D0A5E8C43}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\XstarMusic
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName}
OutputDir=dist
OutputBaseFilename=XstarMusic-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=src\Xstar.Music.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef HaveWebView2Bootstrapper
Source: "{#WebView2BootstrapPath}"; DestDir: "{tmp}"; Flags: deleteafterinstall
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

function WebView2RuntimeInstalled(): Boolean;
var
  pv: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', pv) and (pv <> '') then
    Result := True
  else if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', pv) and (pv <> '') then
    Result := True
  else if RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', pv) and (pv <> '') then
    Result := True;
end;

procedure EnsureWebView2();
var
  bootstrapper: String;
  resultCode: Integer;
begin
  if WebView2RuntimeInstalled() then
    exit;

  bootstrapper := ExpandConstant('{tmp}\MicrosoftEdgeWebView2Setup.exe');
  if FileExists(bootstrapper) then
  begin
    if Exec(bootstrapper, '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, resultCode) then
      exit;
  end;

  if MsgBox('未检测到 Microsoft Edge WebView2 运行时（3D 模式需要）。' + #13#10 +
            '是否现在打开下载页面？', mbConfirmation, MB_YESNO) = IDYES then
    ShellExec('open', WebView2Url, '', '', SW_SHOWNORMAL, ewNoWait, resultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    EnsureWebView2();
end;
