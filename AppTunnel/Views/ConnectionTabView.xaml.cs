using System.Windows;
using AppTunnel.ViewModels;

namespace AppTunnel.Views;

public partial class ConnectionTabView : System.Windows.Controls.UserControl
{
    public ConnectionTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // Wire up PasswordBox (can't bind directly in WPF)
        PasswordField.PasswordChanged += OnPasswordFieldChanged;
        PskField.PasswordChanged += OnPskFieldChanged;

        // When profile changes, update PasswordBox fields
        vm.PasswordChanged += OnViewModelPasswordChanged;

        // Load initial values
        PasswordField.Password = vm.Password;
        PskField.Password = vm.PreSharedKey;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        PasswordField.PasswordChanged -= OnPasswordFieldChanged;
        PskField.PasswordChanged -= OnPskFieldChanged;

        if (DataContext is MainViewModel vm)
            vm.PasswordChanged -= OnViewModelPasswordChanged;
    }

    private void OnPasswordFieldChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.Password != PasswordField.Password)
        {
            vm.Password = PasswordField.Password;
            vm.SaveCurrentState();
        }
    }

    private void OnPskFieldChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.PreSharedKey != PskField.Password)
        {
            vm.PreSharedKey = PskField.Password;
            vm.SaveCurrentState();
        }
    }

    private void OnViewModelPasswordChanged(string password, string psk)
    {
        Dispatcher.Invoke(() =>
        {
            PasswordField.Password = password;
            PskField.Password = psk;
        });
    }

    private void OnProfileNameChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SaveCurrentState();
    }
}
