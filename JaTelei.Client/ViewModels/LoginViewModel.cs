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

    public event Action? LoginSuccess;

    [RelayCommand]
    private async Task Submit()
    {
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
                // Após registro faz login automático
                IsRegisterMode = false;
            }

            var (login, loginErr) = await api.LoginAsync(Username, Password);
            if (login is null) { ErrorMessage = loginErr ?? "Apelido ou senha incorretos."; return; }

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
