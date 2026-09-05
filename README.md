# Já Telei

Aplicativo Windows para compartilhamento de tela em tempo real via WebRTC.

## Estrutura

```
JaTelei.Capture/        C++ DLL — captura via Windows Graphics Capture (WGC/DXGI)
JaTelei.Client/         Aplicativo WPF (.NET 8, win-x64)
setup/                  Script Inno Setup para gerar o instalador
.github/workflows/      CI/CD (GitHub Actions, runner Windows)
```

## Dependências

- .NET 8 SDK
- Visual Studio 2022 com MSVC e C++/WinRT (para a DLL de captura)
- CMake ≥ 3.20
- Inno Setup 6 (para o instalador)
- FFmpeg DLLs — baixadas automaticamente pelo CI (BtbN GPL shared, win64)

## Build local

```powershell
# 1. Compilar a DLL de captura
cmake -B JaTelei.Capture/build -S JaTelei.Capture -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release
cmake --build JaTelei.Capture/build

# 2. Criar appsettings.local.json com as credenciais TURN
@{ Ice = @{ TurnUsername = "user"; TurnCredential = "pass" } } | ConvertTo-Json | Set-Content JaTelei.Client/appsettings.local.json

# 3. Publicar o app
dotnet publish JaTelei.Client/JaTelei.Client.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o publish

# 4. (Opcional) Gerar o instalador
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" setup\JaTelei.iss
```

## Deploy

Feito automaticamente pelo CI a cada push na branch `main`:

1. GitHub Actions (windows-latest) builda a DLL, o app e o instalador
2. Publica uma GitHub Release com o `.exe`
3. O VPS baixa o instalador via `deploy.sh` e atualiza `latest_version.txt`

O endpoint de atualização automática fica em:
```
https://jaclipei.com/screenshare/api/update/latest
```

## Segredos necessários (GitHub Secrets)

| Secret | Uso |
|---|---|
| `TURN_USERNAME` | Credencial do servidor TURN |
| `TURN_CREDENTIAL` | Credencial do servidor TURN |
| `VPS_HOST` | IP/hostname do servidor de deploy |
| `VPS_SSH_KEY` | Chave SSH privada para acesso ao VPS |
