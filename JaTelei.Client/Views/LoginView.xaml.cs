using System.Windows.Controls;
using System.Windows.Input;
using JaTelei.Client.ViewModels;

namespace JaTelei.Client.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();

        // Sincroniza PasswordBox → ViewModel (PasswordBox não suporta binding por segurança)
        PwdBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is LoginViewModel vm)
                vm.Password = PwdBox.Password;
        };

        // Carrega credenciais salvas assim que o DataContext estiver disponível
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LoginViewModel vm)
            {
                vm.TryLoadSavedCredentials();
                if (!string.IsNullOrEmpty(vm.SavedPassword))
                    PwdBox.Password = vm.SavedPassword;
            }
        };

        // Enter em qualquer campo do formulário aciona o botão Entrar/Criar
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not LoginViewModel vm) return;
        if (!vm.SubmitCommand.CanExecute(null)) return;

        e.Handled = true;
        vm.SubmitCommand.Execute(null);
    }
}
