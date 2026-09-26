using System.Windows;
using System.Windows.Controls;
using AppTunnel.ViewModels;

namespace AppTunnel.Views;

public partial class SettingsTabView : UserControl
{
    public SettingsTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        LocalProxyPasswordField.PasswordChanged += OnLocalProxyPasswordFieldChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
            oldVm.LocalProxyPasswordChanged -= OnViewModelLocalProxyPasswordChanged;

        if (e.NewValue is MainViewModel vm)
        {
            vm.LocalProxyPasswordChanged += OnViewModelLocalProxyPasswordChanged;
            LocalProxyPasswordField.Password = vm.LocalProxyPassword;
        }
    }

    private void OnLocalProxyPasswordFieldChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.LocalProxyPassword != LocalProxyPasswordField.Password)
            vm.LocalProxyPassword = LocalProxyPasswordField.Password;
    }

    private void OnViewModelLocalProxyPasswordChanged(string password)
    {
        Dispatcher.Invoke(() =>
        {
            if (LocalProxyPasswordField.Password != password)
                LocalProxyPasswordField.Password = password;
        });
    }
}
