; Build with Inno Setup 6.7.3 or later. All payload files are bundled offline.
#define ProductName "CYD PC Monitor"
#define ProductVersion "1.2.0"
#ifdef InstallerTest
#define UninstallKey "CYD-PC-Monitor-Installer-Test_is1"
#else
#define UninstallKey "{912B549F-F14F-4BEB-B2A6-7914797FEA83}_is1"
#endif

[Setup]
#ifdef InstallerTest
AppId=CYD-PC-Monitor-Installer-Test
AppName={#ProductName} Installer Test
DefaultDirName={localappdata}\Programs\CYD-PC-Monitor-Installer-Test
DefaultGroupName=CYD PC Monitor Installer Test
OutputBaseFilename=CYD-PC-Monitor-Setup-Test
#else
AppId={{912B549F-F14F-4BEB-B2A6-7914797FEA83}
AppName={#ProductName}
DefaultDirName={localappdata}\Programs\CYD PC Monitor
DefaultGroupName={#ProductName}
OutputBaseFilename=CYD-PC-Monitor-Setup-{#ProductVersion}
#endif
AppVersion={#ProductVersion}
AppVerName={#ProductName} {#ProductVersion}
VersionInfoVersion={#ProductVersion}
AppPublisher=TongChai
SourceDir=..
OutputDir=..\Installer
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
UninstallDisplayIcon={app}\CYD-PC-Monitor.exe
UninstallDisplayName={#ProductName}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
SetupIconFile=Source\Monitor.ico

[Languages]
Name: "thai"; MessagesFile: "compiler:Languages\Thai.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
thai.DesktopShortcut=สร้างทางลัดบนเดสก์ท็อป
thai.OpenApplication=เปิด CYD PC Monitor
thai.QuickStart=คู่มือเริ่มใช้งาน
thai.NetRequired=ต้องมี .NET Framework 4.7.2 หรือใหม่กว่าก่อนติดตั้ง%ntรวจ Windows Update หรือติดตั้งจากเว็บไซต์ Microsoft:%nhttps://dotnet.microsoft.com/download/dotnet-framework
english.DesktopShortcut=Create a desktop shortcut
english.OpenApplication=Open CYD PC Monitor
english.QuickStart=Quick start guide
english.NetRequired=.NET Framework 4.7.2 or newer is required.%nUse Windows Update or the official Microsoft download:%nhttps://dotnet.microsoft.com/download/dotnet-framework
thai.OldUninstallFailed=ถอน CYD PC Monitor รุ่นเดิมไม่สำเร็จ กรุณาปิดแอปรุ่นเดิมแล้วลองติดตั้งอีกครั้ง (รหัส %1) การตั้งค่าส่วนตัวจะถูกเก็บไว้
english.OldUninstallFailed=Could not uninstall the previous CYD PC Monitor. Close the old app and try again (code %1). Personal settings are preserved.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "Windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Installer\QuickStart.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "CYD_PCMonitor\CYD_PCMonitor.ino"; DestDir: "{app}\Firmware\CYD_PCMonitor"; Flags: ignoreversion
Source: "Firmware\CYD_PCMonitor.ino.bin"; DestDir: "{app}\Firmware\Binaries"; Flags: ignoreversion
Source: "Firmware\CYD_PCMonitor.ino.bootloader.bin"; DestDir: "{app}\Firmware\Binaries"; Flags: ignoreversion
Source: "Firmware\CYD_PCMonitor.ino.partitions.bin"; DestDir: "{app}\Firmware\Binaries"; Flags: ignoreversion
Source: "Firmware\boot_app0.bin"; DestDir: "{app}\Firmware\Binaries"; Flags: ignoreversion

[Icons]
#ifdef InstallerTest
Name: "{app}\TestLinks\CYD PC Monitor"; Filename: "{app}\CYD-PC-Monitor.exe"; WorkingDir: "{app}"
Name: "{app}\TestLinks\Quick start"; Filename: "{app}\QuickStart.html"
#else
Name: "{userprograms}\{#ProductName}\{#ProductName}"; Filename: "{app}\CYD-PC-Monitor.exe"; WorkingDir: "{app}"
Name: "{userprograms}\{#ProductName}\{cm:QuickStart}"; Filename: "{app}\QuickStart.html"
Name: "{userdesktop}\{#ProductName}"; Filename: "{app}\CYD-PC-Monitor.exe"; WorkingDir: "{app}"; Tasks: desktopicon
#endif

[Run]
#ifndef InstallerTest
Filename: "{app}\CYD-PC-Monitor.exe"; Description: "{cm:OpenApplication}"; Flags: nowait postinstall skipifsilent
#endif

[Code]
var
  PreviousRemoved: Boolean;
  RestoreStartup: Boolean;

const
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
#ifdef InstallerTest
  StartupName = 'CYD PC Monitor Installer Test';
#else
  StartupName = '{#ProductName}';
#endif

function StartupCommand(const InstallDir: String): String;
begin
  Result := '"' + AddBackslash(InstallDir) + 'CYD-PC-Monitor.exe" --startup';
end;

function RemovePreviousFromRoot(Root: Integer): String;
var
  Key, Command, Filename, Startup: String;
  ExitCode: Integer;
begin
  Result := '';
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#UninstallKey}';
  if not RegKeyExists(Root, Key) then exit;
  if not RegQueryStringValue(Root, Key, 'UninstallString', Command) then begin
    Result := FmtMessage(ExpandConstant('{cm:OldUninstallFailed}'), ['missing uninstaller']);
    exit;
  end;
  // Our Inno installers register a quoted executable path, without arguments.
  Filename := RemoveQuotes(Trim(Command));
  if not FileExists(Filename) then begin
    Result := FmtMessage(ExpandConstant('{cm:OldUninstallFailed}'), ['missing file']);
    exit;
  end;
  Log('Removing previous CYD PC Monitor: ' + Filename);
  if RegQueryStringValue(HKCU, StartupKey, StartupName, Startup) then
    if CompareText(Startup, StartupCommand(ExtractFileDir(Filename))) = 0 then
      RestoreStartup := True;
  if not Exec(Filename, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
    Result := FmtMessage(ExpandConstant('{cm:OldUninstallFailed}'), [IntToStr(ExitCode)]);
    exit;
  end;
  if (ExitCode <> 0) or RegKeyExists(Root, Key) then
    Result := FmtMessage(ExpandConstant('{cm:OldUninstallFailed}'), [IntToStr(ExitCode)])
  else
    Log('Previous CYD PC Monitor removed successfully; personal settings preserved.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if PreviousRemoved then exit;
  Result := RemovePreviousFromRoot(HKCU64);
  if Result = '' then Result := RemovePreviousFromRoot(HKCU32);
  if Result = '' then Result := RemovePreviousFromRoot(HKLM64);
  if Result = '' then Result := RemovePreviousFromRoot(HKLM32);
  PreviousRemoved := Result = '';
end;

function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net472, 0);
  if not Result then
    SuppressibleMsgBox(ExpandConstant('{cm:NetRequired}'), mbCriticalError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and RestoreStartup then
    if not RegWriteStringValue(HKCU, StartupKey, StartupName, StartupCommand(ExpandConstant('{app}'))) then
      Log('Could not restore Windows startup entry');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Startup: String;
begin
  if CurUninstallStep = usPostUninstall then
    if RegQueryStringValue(HKCU, StartupKey, StartupName, Startup) then
      if CompareText(Startup, StartupCommand(ExpandConstant('{app}'))) = 0 then
        RegDeleteValue(HKCU, StartupKey, StartupName);
end;

// Personal settings are outside {app}; updates and uninstall preserve them.
