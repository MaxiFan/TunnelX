using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AppTunnel.Helpers;
using AppTunnel.Services;

namespace AppTunnel.Models;

/// <summary>
/// A remote subscription URL whose configs are imported as profiles and refreshed later.
/// </summary>
public class SubscriptionLink : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N")[..8];
    private string _name = "";
    private string _url = "";
    private DateTime? _lastFetchedAt;
    private string _lastError = "";
    private int _nodeCount;
    private long _uploadBytes;
    private long _downloadBytes;
    private long _totalBytes;
    private long _expireUnix;
    private bool _isRefreshing;

    public SubscriptionLink()
    {
        LocalizationService.Instance.LanguageChanged += (_, _) => RefreshLocalization();
    }

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Url
    {
        get => _url;
        set => SetField(ref _url, value);
    }

    public DateTime? LastFetchedAt
    {
        get => _lastFetchedAt;
        set
        {
            if (!SetField(ref _lastFetchedAt, value))
                return;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string LastError
    {
        get => _lastError;
        set
        {
            if (!SetField(ref _lastError, value ?? ""))
                return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
        }
    }

    public int NodeCount
    {
        get => _nodeCount;
        set
        {
            if (!SetField(ref _nodeCount, value))
                return;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public long UploadBytes
    {
        get => _uploadBytes;
        set => SetTraffic(ref _uploadBytes, value);
    }

    public long DownloadBytes
    {
        get => _downloadBytes;
        set => SetTraffic(ref _downloadBytes, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set => SetTraffic(ref _totalBytes, value);
    }

    public long ExpireUnix
    {
        get => _expireUnix;
        set => SetTraffic(ref _expireUnix, value);
    }

    [JsonIgnore]
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            if (!SetField(ref _isRefreshing, value))
                return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
        }
    }

    [JsonIgnore]
    public string StatusText
    {
        get
        {
            if (IsRefreshing)
                return LocalizationService.Instance.T("در حال دریافت...");
            if (!string.IsNullOrWhiteSpace(LastError))
                return LastError;
            if (LastFetchedAt == null)
                return LocalizationService.Instance.T("هنوز دریافت نشده");

            return LocalizationService.Instance.Format(
                "{0} کانفیگ · {1}",
                NodeCount,
                FormatFetchedAt(LastFetchedAt.Value));
        }
    }

    [JsonIgnore]
    public string StatusColor =>
        !IsRefreshing && !string.IsNullOrWhiteSpace(LastError) ? "#E05252" : "#9A9A9A";

    [JsonIgnore]
    public string TrafficText
    {
        get
        {
            if (TotalBytes <= 0 && DownloadBytes <= 0 && UploadBytes <= 0)
                return "";

            var used = UploadBytes + DownloadBytes;
            var quota = TotalBytes > 0
                ? $"{ByteSizeFormatter.Format(used)} / {ByteSizeFormatter.Format(TotalBytes)}"
                : ByteSizeFormatter.Format(used);

            if (ExpireUnix > 0)
            {
                var until = DateTimeOffset.FromUnixTimeSeconds(ExpireUnix).LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return LocalizationService.Instance.Format("مصرف {0} · تا {1}", quota, until);
            }

            return LocalizationService.Instance.Format("مصرف {0}", quota);
        }
    }

    [JsonIgnore]
    public bool HasTrafficText => !string.IsNullOrWhiteSpace(TrafficText);

    public void ApplyTraffic(SubscriptionTrafficInfo? traffic)
    {
        if (traffic == null)
        {
            UploadBytes = 0;
            DownloadBytes = 0;
            TotalBytes = 0;
            ExpireUnix = 0;
            return;
        }

        UploadBytes = traffic.Upload;
        DownloadBytes = traffic.Download;
        TotalBytes = traffic.Total;
        ExpireUnix = traffic.Expire;
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(TrafficText));
        OnPropertyChanged(nameof(HasTrafficText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetTraffic(ref long field, long value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetField(ref field, value, propertyName))
            return;
        OnPropertyChanged(nameof(TrafficText));
        OnPropertyChanged(nameof(HasTrafficText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private static string FormatFetchedAt(DateTime fetched)
    {
        var ago = DateTime.Now - fetched;
        if (ago < TimeSpan.FromMinutes(1))
            return LocalizationService.Instance.T("همین الان");
        if (ago < TimeSpan.FromHours(1))
            return LocalizationService.Instance.Format("{0} دقیقه پیش", Math.Max(1, (int)ago.TotalMinutes));
        if (ago < TimeSpan.FromDays(1))
            return LocalizationService.Instance.Format("{0} ساعت پیش", Math.Max(1, (int)ago.TotalHours));
        return fetched.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
