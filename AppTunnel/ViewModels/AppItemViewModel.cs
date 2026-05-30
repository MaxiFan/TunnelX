using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using AppTunnel.Helpers;
using AppTunnel.Models;

namespace AppTunnel.ViewModels;

public class AppItemViewModel : INotifyPropertyChanged
{
    private readonly TunnelApp _app;

    public AppItemViewModel(TunnelApp app) => _app = app;

    public string DisplayName => _app.DisplayName;
    public string ExecutablePath => _app.ExecutablePath;
    public string ExecutableName => _app.ExecutableName;
    public BitmapSource? Icon => _app.Icon;

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); }
    }

    public long BytesSent
    {
        get => _app.BytesSent;
        set
        {
            _app.BytesSent = value;
            OnPropertyChanged();
            NotifyTrafficPropertiesChanged();
        }
    }

    public long BytesReceived
    {
        get => _app.BytesReceived;
        set
        {
            _app.BytesReceived = value;
            OnPropertyChanged();
            NotifyTrafficPropertiesChanged();
        }
    }

    public long TotalTrafficBytes => BytesSent + BytesReceived;

    public string TrafficDisplay => _app.TrafficDisplay;

    public string SentTrafficDisplay => ByteSizeFormatter.Format(BytesSent);

    public string ReceivedTrafficDisplay => ByteSizeFormatter.Format(BytesReceived);

    private void NotifyTrafficPropertiesChanged()
    {
        OnPropertyChanged(nameof(TrafficDisplay));
        OnPropertyChanged(nameof(TotalTrafficBytes));
        OnPropertyChanged(nameof(SentTrafficDisplay));
        OnPropertyChanged(nameof(ReceivedTrafficDisplay));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
