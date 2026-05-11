using System.Windows;
using System.Windows.Input;
using AppTunnel.ViewModels;

namespace AppTunnel.Views;

public partial class AppsTabView : System.Windows.Controls.UserControl
{
    public AppsTabView()
    {
        InitializeComponent();
    }

    private void OnAvailableAppClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is AppItemViewModel app
            && DataContext is MainViewModel vm)
        {
            vm.AddAppToTunnel(app);
        }
    }

    private void OnSearchBoxPreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox)
        {
            textBox.SelectAll();
            e.Handled = true;
        }
    }
}
