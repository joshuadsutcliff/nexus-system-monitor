; Nexus System Monitor — Inno Setup Installer Script
; =====================================================================
; Usage (CI — jrsoftware/iscc@v1):
;   iscc NexusMonitor.iss /DAppVersion=0.1.0 /DAppArch=x64
;        /DPublishDir=..\..\src\NexusMonitor.UI\publish\win-x64
;        /DCliPublishDir=..\..\src\NexusMonitor.CLI\publish\cli-win-x64
;        /DOutputDir=..\..\dist
;        /DOutputFilename=NexusMonitor-Windows-Installer-0.1.0
;
; Usage (local — Inno Setup must be installed):
;   iscc installer\windows\NexusMonitor.iss /DAppVersion=0.1.0 /DAppArch=x64
;        /DPublishDir=src\NexusMonitor.UI\publish\win-x64
;        /DCliPublishDir=src\NexusMonitor.CLI\publish\cli-win-x64
;        /DOutputDir=dist
;        /DOutputFilename=NexusMonitor-Windows-Installer-0.1.0
;
; Optional code signing of the packaged installer EXE happens in the calling
; workflow (release.yml), after iscc produces it — see docs/signing-setup.md.
; =====================================================================

; Defaults for local/manual runs (CI passes these via /D flags)
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef AppArch
  #define AppArch "x64"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\src\NexusMonitor.UI\publish\win-x64"
#endif
#ifndef CliPublishDir
  #define CliPublishDir "..\..\src\NexusMonitor.CLI\publish\cli-win-x64"
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
AppPublisher=TheBlackSwordsman
AppPublisherURL=https://github.com/brass458/nexus-system-monitor
AppSupportURL=https://github.com/brass458/nexus-system-monitor/issues
AppUpdatesURL=https://github.com/brass458/nexus-system-monitor/releases
DefaultDirName={autopf}\Nexus System Monitor
DefaultGroupName=Nexus System Monitor
AllowNoIcons=yes
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename={#OutputFilename}
SetupIconFile=..\..\src\NexusMonitor.UI\Assets\nexus-icon.ico
UninstallDisplayIcon={app}\NexusMonitor.exe
UninstallDisplayName=Nexus System Monitor {#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Architecture-specific settings
#if AppArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64
#endif
; Minimum Windows 10 1803 (required by .NET 8 + net8.0-windows10.0.17763.0)
MinVersion=10.0.17763
; Required so Setup broadcasts WM_SETTINGCHANGE after the [Registry] PATH
; entry below is written (see [Registry] + NeedsAddPath in [Code]).
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "Add nexus CLI to PATH"; GroupDescription: "Additional options:"

[Files]
; Copy all published files
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; CLI (nexus.exe + its dependencies) into a subdirectory
Source: "{#CliPublishDir}\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; Append {app}\cli to the machine-wide PATH, only if it's not already there.
; Note (documented, not automated): uninstall intentionally leaves this PATH
; entry in place. `uninsdeletevalue` is not usable here — it would delete the
; ENTIRE system Path value, not just our appended segment. Uninstalling this
; app therefore leaves a stale (harmless) {app}\cli entry on PATH; a future
; reinstall to the same path or a manual PATH edit is the clean-up path.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
    ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\cli"; \
    Flags: preservestringtype; Check: NeedsAddPath('{app}\cli'); Tasks: addtopath

[Icons]
Name: "{group}\Nexus System Monitor"; Filename: "{app}\NexusMonitor.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,Nexus System Monitor}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\Nexus System Monitor"; Filename: "{app}\NexusMonitor.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\NexusMonitor.exe"; Description: "{cm:LaunchProgram,Nexus System Monitor}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
; Remove runtime data left by the app (settings, metrics DB)
Type: filesandordirs; Name: "{localappdata}\NexusMonitor"

[Code]
// Standard Inno Setup "add to PATH" check: returns True if Param is not
// already present as a segment of the machine PATH (case-sensitive substring
// match on ';'-delimited segments, padded so entries at the very start/end
// of the string are still matched correctly).
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
