using System.IO;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JaTelei.Client.Services;

namespace JaTelei.Client.ViewModels;

public partial class LoginViewModel(ApiService api) : ObservableObject
{
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isRegisterMode;
    [ObservableProperty] private bool _rememberMe;

    /// <summary>
    /// Senha carregada do armazenamento seguro — usada pelo code-behind
    /// para preencher o PasswordBox na inicialização.
    /// </summary>
    public string SavedPassword { get; private set; } = string.Empty;

    public event Action? LoginSuccess;

    // ── Credenciais salvas (DPAPI) ─────────────────────────────────────────

    private static readonly string CredsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JaTelei", "creds.dat");

    /// <summary>
    /// Chamado pelo code-behind no construtor da View para pré-carregar
    /// as credenciais salvas (se existirem).
    /// </summary>
    public void TryLoadSavedCredentials()
    {
        try
        {
            if (!File.Exists(CredsPath)) return;

            var encrypted = File.ReadAllBytes(CredsPath);
            var plain = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));

            var parts = plain.Split('\n', 2);
            if (parts.Length < 2) return;

            Username      = parts[0];
            SavedPassword = parts[1];   // code-behind preenche o PasswordBox
            Password      = parts[1];
            RememberMe    = true;
        }
        catch { /* arquivo corrompido ou de outro usuário — ignora */ }
    }

    private void SaveCredentials()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CredsPath)!);
            var plain     = $"{Username}\n{Password}";
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(CredsPath, encrypted);
        }
        catch { /* best-effort */ }
    }

    private static void DeleteSavedCredentials()
    {
        try { if (File.Exists(CredsPath)) File.Delete(CredsPath); }
        catch { }
    }

    // ── Comandos ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task Submit()
    {
        // Guard contra duplo clique / chamadas concorrentes
        if (IsLoading) return;

        ErrorMessage = string.Empty;
        IsLoading = true;
        try
        {
            if (IsRegisterMode)
            {
                var (reg, regErr) = await api.RegisterAsync(
                    Username,
                    string.IsNullOrWhiteSpace(Email) ? null : Email,
                    Password);
                if (reg is null) { ErrorMessage = regErr ?? "Erro ao criar conta."; return; }
                // Após registro bem-sucedido, faz login automático
                IsRegisterMode = false;
            }

            var (login, loginErr) = await api.LoginAsync(Username, Password);
            if (login is null) { ErrorMessage = loginErr ?? "Apelido ou senha incorretos."; return; }

            // Salva ou apaga credenciais conforme preferência do usuário
            if (RememberMe)
                SaveCredentials();
            else
                DeleteSavedCredentials();

            LoginSuccess?.Invoke();
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void ToggleMode()
    {
        IsRegisterMode = !IsRegisterMode;
        ErrorMessage = string.Empty;
        Email = string.Empty;
    }
}
