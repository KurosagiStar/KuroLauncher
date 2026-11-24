[Setup]
AppId={{8E7B4F2C-9D31-4B6E-A123-KUROLAUNCHER01}
AppName=Kuro Launcher
AppVersion=1.0.0
AppPublisher=Kuro
DefaultDirName={localappdata}\KuroLauncher
DefaultGroupName=Kuro Launcher
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=release
OutputBaseFilename=KuroLauncher-Setup
SetupIconFile=Assets\app-icon.ico
UninstallDisplayIcon={app}\KuroLauncher.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "release\KuroLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Kuro Launcher"; Filename: "{app}\KuroLauncher.exe"; IconFilename: "{app}\KuroLauncher.exe"
Name: "{group}\Desinstalar Kuro Launcher"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Kuro Launcher"; Filename: "{app}\KuroLauncher.exe"; IconFilename: "{app}\KuroLauncher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\KuroLauncher.exe"; Description: "{cm:LaunchProgram,Kuro Launcher}"; Flags: nowait postinstall skipifsilent
