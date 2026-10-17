; Inno Setup script for Orbitale. Built by build-installer.ps1, which passes:
;   /DAppVersion=<version from Orbitale.App.csproj>  /DPublishDir=<self-contained publish folder>  /DOutputDir=<dist folder>

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #error PublishDir is not defined: run build-installer.ps1
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; A fixed id ties upgrades and the uninstaller together: never change it.
AppId={{6F3B2A7E-9C41-4F2D-8E5A-0B7C1D2E3F40}
AppName=Orbitale
AppVersion={#AppVersion}
AppVerName=Orbitale {#AppVersion}
AppPublisher=Orbitale
DefaultDirName={autopf}\Orbitale
DefaultGroupName=Orbitale
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; the user can choose "all users" (asks for elevation).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile=..\src\Orbitale.App\Assets\Orbitale.ico
UninstallDisplayIcon={app}\Orbitale.exe
UninstallDisplayName=Orbitale
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=Orbitale-Setup-{#AppVersion}
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Orbitale"; Filename: "{app}\Orbitale.exe"
Name: "{autodesktop}\Orbitale"; Filename: "{app}\Orbitale.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Orbitale.exe"; Description: "{cm:LaunchProgram,Orbitale}"; Flags: nowait postinstall skipifsilent

; Settings and the log (%LOCALAPPDATA%\Orbitale) are kept on uninstall, so a reinstall keeps the user's settings.
