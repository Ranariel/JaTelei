using System.Windows.Controls;
using JaTelei.Client.ViewModels;

namespace JaTelei.Client.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        PwdBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is LoginViewModel vm)
                vm.Password = PwdBox.Password;
        };
    }
}
