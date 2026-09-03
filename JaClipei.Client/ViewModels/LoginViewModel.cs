using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JaClipei.Client.Services;

namespace JaClipei.Client.ViewModels;

public partial class LoginViewModel(ApiService api) : ObservableObject
{
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
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
                var res = await api.RegisterAsync(Username, Email, Password);
                if (res is null) { ErrorMessage = "Erro ao criar conta."; return; }
                // Após registro, já faz login
                IsRegisterMode = false;
            }

            var login = await api.LoginAsync(Email, Password);
            if (login is null) { ErrorMessage = "Email ou senha incorretos."; return; }

            LoginSuccess?.Invoke();
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void ToggleMode()
    {
        IsRegisterMode = !IsRegisterMode;
        ErrorMessage = string.Empty;
    }
}
