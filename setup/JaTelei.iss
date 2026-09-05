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
; Executable (self-contained — .NET runtime already bundled, no .NET install needed)
Source: "..\publish\{#MyExeName}"; DestDir: "{app}"; DestName: "JaTelei.exe"; Flags: ignoreversion

; C++ screen-capture DLL (static CRT — no VC++ Redistributable needed)
Source: "..\publish\JaTelei.Capture.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; FFmpeg shared libs (avcodec, avutil, swresample, swscale)
; Only present when SIPSorceryMedia.Windows ships them; CI passes /DHasFfmpeg when found.
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

[Code]
// ---------------------------------------------------------------------------
// Verifica pré-requisitos do sistema antes de instalar
// ---------------------------------------------------------------------------

// Checa se a Media Foundation está disponível (ausente no Windows N/KN sem
// o Media Feature Pack). O Ja Telei usa MF para codificação de vídeo H.264.
function MediaFoundationAvailable: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\mfplat.dll'));
end;

// Chamado pelo Inno antes de mostrar as páginas do instalador
function InitializeSetup(): Boolean;
var
  Msg: String;
begin
  Result := True;

  if not MediaFoundationAvailable then
  begin
    Msg := 'Atenção: esta versão do Windows não possui o Media Foundation instalado.' + #13#10 + #13#10 +
           'O Ja Telei pode não conseguir codificar vídeo H.264 corretamente.' + #13#10 + #13#10 +
           'Para corrigir, instale o "Media Feature Pack" em:' + #13#10 +
           'Configurações → Aplicativos → Recursos opcionais.' + #13#10 + #13#10 +
           'Deseja continuar a instalação mesmo assim?';

    if MsgBox(Msg, mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;
