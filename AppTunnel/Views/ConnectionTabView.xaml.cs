using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AppTunnel.Helpers;
using AppTunnel.Services;
using AppTunnel.ViewModels;
using FlowDirection = System.Windows.FlowDirection;

namespace AppTunnel.Views;

public partial class ConnectionTabView : System.Windows.Controls.UserControl
{
    public ConnectionTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void ApplyTabFlowDirection()
    {
        FlowDirection = LocalizationService.Instance.FlowDirection;
        ApplyProfileCardLayout();
    }

    private void ApplyProfileCardLayout()
    {
        var loc = LocalizationService.Instance;
        var flow = loc.FlowDirection;
        var align = loc.TextAlignment;
        var start = loc.StartHorizontalAlignment;

        ProfileQuickActionsCard.FlowDirection = flow;
        ProfileQuickActionsHeader.FlowDirection = flow;
        ProfileQuickActionsButtons.FlowDirection = flow;
        ProfileQuickActionsButtons.HorizontalAlignment = start;

        foreach (var textBlock in ProfileQuickActionsHeader.Children.OfType<TextBlock>())
        {
            textBlock.FlowDirection = flow;
            textBlock.TextAlignment = align;
            textBlock.HorizontalAlignment = textBlock.TextWrapping == TextWrapping.Wrap
                ? System.Windows.HorizontalAlignment.Stretch
                : start;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ApplyTabFlowDirection();
        LocalizationLayoutHelper.ApplyTo(this);
        Dispatcher.BeginInvoke(() =>
            LocalizationLayoutHelper.RefreshLayoutBindings(this),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTabFlowDirection();
        LocalizationLayoutHelper.ApplyTo(this);
        Dispatcher.BeginInvoke(() =>
            LocalizationLayoutHelper.RefreshLayoutBindings(this),
            System.Windows.Threading.DispatcherPriority.Loaded);
        if (DataContext is not MainViewModel vm) return;

        // Wire up PasswordBox (can't bind directly in WPF)
        PasswordField.PasswordChanged += OnPasswordFieldChanged;
        PskField.PasswordChanged += OnPskFieldChanged;
        OpenVpnPasswordField.PasswordChanged += OnOpenVpnPasswordFieldChanged;
        OpenVpnPrivateKeyPasswordField.PasswordChanged += OnOpenVpnPrivateKeyPasswordFieldChanged;
        ProxyPasswordField.PasswordChanged += OnProxyPasswordFieldChanged;

        // When profile changes, update PasswordBox fields
        vm.PasswordChanged += OnViewModelPasswordChanged;
        vm.OpenVpnPasswordChanged += OnViewModelOpenVpnPasswordChanged;
        vm.OpenVpnPrivateKeyPasswordChanged += OnViewModelOpenVpnPrivateKeyPasswordChanged;
        vm.ProxyPasswordChanged += OnViewModelProxyPasswordChanged;

        // Load initial values
        PasswordField.Password = vm.Password;
        PskField.Password = vm.PreSharedKey;
        OpenVpnPasswordField.Password = vm.OpenVpnPassword;
        OpenVpnPrivateKeyPasswordField.Password = vm.OpenVpnPrivateKeyPassword;
        ProxyPasswordField.Password = vm.ProxyPassword;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        PasswordField.PasswordChanged -= OnPasswordFieldChanged;
        PskField.PasswordChanged -= OnPskFieldChanged;
        OpenVpnPasswordField.PasswordChanged -= OnOpenVpnPasswordFieldChanged;
        OpenVpnPrivateKeyPasswordField.PasswordChanged -= OnOpenVpnPrivateKeyPasswordFieldChanged;
        ProxyPasswordField.PasswordChanged -= OnProxyPasswordFieldChanged;

        if (DataContext is MainViewModel vm)
        {
            vm.PasswordChanged -= OnViewModelPasswordChanged;
            vm.OpenVpnPasswordChanged -= OnViewModelOpenVpnPasswordChanged;
            vm.OpenVpnPrivateKeyPasswordChanged -= OnViewModelOpenVpnPrivateKeyPasswordChanged;
            vm.ProxyPasswordChanged -= OnViewModelProxyPasswordChanged;
        }
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

    private void OnOpenVpnPasswordFieldChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.OpenVpnPassword != OpenVpnPasswordField.Password)
            vm.OpenVpnPassword = OpenVpnPasswordField.Password;
    }

    private void OnOpenVpnPrivateKeyPasswordFieldChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.OpenVpnPrivateKeyPassword != OpenVpnPrivateKeyPasswordField.Password)
            vm.OpenVpnPrivateKeyPassword = OpenVpnPrivateKeyPasswordField.Password;
    }

    private void OnProxyPasswordFieldChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.ProxyPassword != ProxyPasswordField.Password)
            vm.ProxyPassword = ProxyPasswordField.Password;
    }

    private void OnViewModelPasswordChanged(string password, string psk)
    {
        Dispatcher.Invoke(() =>
        {
            PasswordField.Password = password;
            PskField.Password = psk;
        });
    }

    private void OnViewModelOpenVpnPasswordChanged(string password)
    {
        Dispatcher.Invoke(() => OpenVpnPasswordField.Password = password);
    }

    private void OnViewModelOpenVpnPrivateKeyPasswordChanged(string password)
    {
        Dispatcher.Invoke(() => OpenVpnPrivateKeyPasswordField.Password = password);
    }

    private void OnViewModelProxyPasswordChanged(string password)
    {
        Dispatcher.Invoke(() => ProxyPasswordField.Password = password);
    }

    private void OnProfileNameChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SaveCurrentState();
    }

    private static void OnProfileItemRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // ListBox auto-scroll on selection causes visible jumps while wheel-scrolling.
        e.Handled = true;
    }
}
