; Script de instalação Ja Telei
; Compilar via: ISCC /DMyAppVersion=1.0.X /DMyExeName=JaTelei-1.0.X.exe JaTelei.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyExeName
  #define MyExeName "JaTelei-1.0.0.exe"
#endif

[Setup]
AppId={{A7F3C2D1-8B4E-4F5A-9C6D-0E1F2A3B4C5D}
AppName=Ja Telei
AppVersion={#MyAppVersion}
AppPublisher=Ranariel
AppPublisherURL=https://jaclipei.com
AppUpdatesURL=https://jaclipei.com/screenshare/api/update/latest
DefaultDirName={autopf}\Ja Telei
DefaultGroupName=Ja Telei
AllowNoIcons=yes
OutputDir=..\installer
OutputBaseFilename=JaTeleiSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Ja Telei
UninstallDisplayIcon={app}\JaTelei.exe
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar ícone na Área de Trabalho"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\{#MyExeName}"; DestDir: "{app}"; DestName: "JaTelei.exe"; Flags: ignoreversion
Source: "..\publish\JaTelei.Capture.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; FFmpeg shared libs — only included when present (CI passes /DHasFfmpeg when it finds them)
#ifdef HasFfmpeg
Source: "..\publish\av*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\publish\sw*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
#endif

[Icons]
Name: "{group}\Ja Telei";             Filename: "{app}\JaTelei.exe"
Name: "{group}\Desinstalar JaTelei"; Filename: "{uninstallexe}"
Name: "{commondesktop}\Ja Telei";     Filename: "{app}\JaTelei.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\JaTelei"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\JaTelei.exe"; Description: "Iniciar Ja Telei agora"; Flags: nowait postinstall

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM JaTelei.exe"; Flags: runhidden; RunOnceId: "KillApp"
