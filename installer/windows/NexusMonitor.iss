; Nexus System Monitor — Inno Setup Installer Script
; =====================================================================

#ifndef AppVersion
  #define AppVersion "0.8.2"
#endif
#ifndef AppArch
  #define AppArch "x64"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\dist\stage\win-x64-ui"
#endif
#ifndef CliPublishDir
  #define CliPublishDir "..\..\dist\stage\win-x64-cli"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif
#ifndef OutputFilename
  #if AppArch == "arm64"
    #define OutputFilename "NexusMonitor-Windows-ARM-Installer-" + AppVersion
  #else
    #define OutputFilename "NexusMonitor-Windows-Installer-" + AppVersion
  #endif
#endif

[Setup]
AppId={{B7C3A2E1-9F5D-4D8A-A1B2-C3D4E5F60001}
AppName=Nexus System Monitor
AppVersion={#AppVersion}
AppPublisher=Josh Sutcliff
AppPublisherURL=https://github.com/joshuadsutcliff/nexus-system-monitor
AppSupportURL=https://github.com/joshuadsutcliff/nexus-system-monitor/issues
AppUpdatesURL=https://github.com/joshuadsutcliff/nexus-system-monitor/releases
DefaultDirName={autopf}\Nexus System Monitor
DefaultGroupName=Nexus System Monitor
AllowNoIcons=yes
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename={#OutputFilename}
SetupIconFile=..\..\crates\nexus-ui\assets\nexus-icon.ico
UninstallDisplayIcon={app}\nexus-monitor.exe
UninstallDisplayName=Nexus System Monitor {#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
#if AppArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64
#endif
MinVersion=10.0.17763
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "Add nexus CLI to PATH"; GroupDescription: "Additional options:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#CliPublishDir}\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
    ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\cli"; \
    Flags: preservestringtype; Check: NeedsAddPath('{app}\cli'); Tasks: addtopath

[Icons]
Name: "{group}\Nexus System Monitor"; Filename: "{app}\nexus-monitor.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,Nexus System Monitor}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\Nexus System Monitor"; Filename: "{app}\nexus-monitor.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\nexus-monitor.exe"; Description: "{cm:LaunchProgram,Nexus System Monitor}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\NexusMonitor"
Type: filesandordirs; Name: "{userappdata}\NexusSystemMonitor"

[Code]
function NeedsAddPath(Param: string): boolean;
var
  OldPath: string;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE,
    'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', OldPath)
  then begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Param + ';', ';' + OldPath + ';') = 0;
end;
