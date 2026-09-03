; Script de instalação JaClipei
; Compilar via: ISCC /DMyAppVersion=1.0.X /DMyExeName=JaClipei-1.0.X.exe JaClipei.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyExeName
  #define MyExeName "JaClipei-1.0.0.exe"
#endif

[Setup]
AppId={{A7F3C2D1-8B4E-4F5A-9C6D-0E1F2A3B4C5D}
AppName=JaClipei
AppVersion={#MyAppVersion}
AppPublisher=Ranariel
AppPublisherURL=https://jaclipei.com
AppUpdatesURL=https://jaclipei.com/screenshare/api/update/latest
DefaultDirName={autopf}\JaClipei
DefaultGroupName=JaClipei
AllowNoIcons=yes
OutputDir=..\installer
OutputBaseFilename=JaClipeiSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=JaClipei
UninstallDisplayIcon={app}\JaClipei.exe
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar ícone na Área de Trabalho"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\{#MyExeName}"; DestDir: "{app}"; DestName: "JaClipei.exe"; Flags: ignoreversion
Source: "..\publish\JaClipei.Capture.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\JaClipei";             Filename: "{app}\JaClipei.exe"
Name: "{group}\Desinstalar JaClipei"; Filename: "{uninstallexe}"
Name: "{commondesktop}\JaClipei";     Filename: "{app}\JaClipei.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\JaClipei"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\JaClipei.exe"; Description: "Iniciar JaClipei agora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM JaClipei.exe"; Flags: runhidden; RunOnceId: "KillApp"
