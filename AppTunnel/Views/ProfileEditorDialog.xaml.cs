using System.IO;
using System.Windows;
using System.Windows.Controls;
using AppTunnel.Helpers;
using AppTunnel.Models;
using AppTunnel.Services;

namespace AppTunnel.Views;

public partial class ProfileEditorDialog : Window
{
    private readonly ConnectionProfile _profile;
    private readonly string _titleKey;

    public ProfileEditorDialog(ConnectionProfile profile, string title)
    {
        _profile = profile;
        _titleKey = title;
        InitializeComponent();
        Loaded += (_, _) => ApplyDialogLayout();
        DataContext = profile;
        DialogTitleText.Text = LocalizationService.Instance.T(title);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        var loc = LocalizationService.Instance;
        DialogTitleText.Text = loc.T(_titleKey);
        DialogSubtitleText.Text = loc.T("تنظیمات این کانفیگ بعد از ذخیره در لیست پروفایل‌ها نمایش داده می‌شود.");
        ApplyDialogLayout();
        RefreshOpenVpnProfileUi();
    }

    private void ApplyDialogLayout()
    {
        var loc = LocalizationService.Instance;
        FlowDirection = loc.FlowDirection;
        LocalizationLayoutHelper.ApplyTo(this);
        loc.ApplyTo(this);
    }

    public static bool? Show(ConnectionProfile profile, string title, Window? owner)
    {
        var dialog = new ProfileEditorDialog(profile, title)
        {
            Owner = owner
        };
        return dialog.ShowDialog();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDialogLayout();
        L2tpPasswordField.Password = _profile.Password;
        PskField.Password = _profile.PreSharedKey;
        OpenVpnPasswordField.Password = _profile.OpenVpnPassword;
        OpenVpnPrivateKeyPasswordField.Password = _profile.OpenVpnPrivateKeyPassword;
        ProxyPasswordField.Password = _profile.ProxyPassword;
        RefreshOpenVpnProfileUi();
    }

    private void RefreshOpenVpnProfileUi()
    {
        var loc = LocalizationService.Instance;
        var flow = loc.FlowDirection;
        var align = loc.TextAlignment;
        OpenVpnIntroTextBlock.Text = loc.T("فایل .ovpn و اطلاعات احراز هویت OpenVPN را وارد کنید. TunnelX بر اساس محتوای فایل مشخص می‌کند کدام فیلدها اجباری است.");
        OpenVpnFileLabelTextBlock.Text = loc.T("فایل OpenVPN (.ovpn)");
        OpenVpnBrowseButton.Content = loc.T("انتخاب فایل");
        OpenVpnScenarioTitleTextBlock.Text = OpenVpnProfileAnalyzer.GetScenarioTitle(_profile.OpenVpnConfig);
        OpenVpnScenarioHintTextBlock.Text = OpenVpnProfileAnalyzer.GetScenarioHint(_profile.OpenVpnConfig);
        OpenVpnUsernameLabelTextBlock.Text = OpenVpnProfileAnalyzer.GetUsernameFieldLabel(_profile.OpenVpnConfig);
        OpenVpnPasswordLabelTextBlock.Text = OpenVpnProfileAnalyzer.GetPasswordFieldLabel(_profile.OpenVpnConfig);
        OpenVpnSecretLabelTextBlock.Text = OpenVpnProfileAnalyzer.GetSecretFieldLabel(_profile.OpenVpnConfig);

        foreach (var block in new TextBlock[]
                 {
                     DialogTitleText, DialogSubtitleText, OpenVpnIntroTextBlock, OpenVpnFileLabelTextBlock,
                     OpenVpnScenarioTitleTextBlock, OpenVpnScenarioHintTextBlock, OpenVpnUsernameLabelTextBlock,
                     OpenVpnPasswordLabelTextBlock, OpenVpnSecretLabelTextBlock, ProfileNameValidationText,
                     ValidationText
                 })
        {
            block.FlowDirection = flow;
            block.TextAlignment = align;
            block.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        }

        ProfileNameField.FlowDirection = flow;
        ProfileNameField.TextAlignment = align;
    }

    private void OnOpenVpnCredentialChanged(object sender, RoutedEventArgs e)
    {
        if (_profile.TunnelType != TunnelType.OpenVpn)
            return;

        RefreshOpenVpnProfileUi();
        if (OpenVpnProfileAnalyzer.TryGetProfileValidationError(
                _profile.OpenVpnConfig,
                _profile.OpenVpnUsername,
                OpenVpnPasswordField.Password,
                OpenVpnPrivateKeyPasswordField.Password,
                out var hint))
            ValidationText.Text = hint;
        else
            ValidationText.Text = "";
    }

    private void OnBrowseOpenVpnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.Instance.T("انتخاب فایل OpenVPN"),
            Filter = LocalizationService.Instance.T("OpenVPN config (*.ovpn)|*.ovpn|All files (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _profile.OpenVpnConfigPath = dialog.FileName;
            _profile.OpenVpnConfig = File.ReadAllText(dialog.FileName);
            RefreshOpenVpnProfileUi();
            if (OpenVpnProfileAnalyzer.TryGetProfileValidationError(
                    _profile.OpenVpnConfig,
                    _profile.OpenVpnUsername,
                    OpenVpnPasswordField.Password,
                    OpenVpnPrivateKeyPasswordField.Password,
                    out var hint))
                ValidationText.Text = hint;
            else
                ValidationText.Text = "";
        }
        catch (Exception ex)
        {
            ValidationText.Text = LocalizationService.Instance.Format("خواندن فایل OpenVPN ناموفق بود: {0}", ex.Message);
        }
    }

    private void OnBrowseWireGuardClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.Instance.T("انتخاب فایل WireGuard"),
            Filter = LocalizationService.Instance.T("WireGuard config (*.conf)|*.conf|All files (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _profile.WireGuardConfigPath = dialog.FileName;
            _profile.WireGuardConfig = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            ValidationText.Text = LocalizationService.Instance.Format("خواندن فایل WireGuard ناموفق بود: {0}", ex.Message);
        }
    }

    private void OnPasteV2RayClick(object sender, RoutedEventArgs e) => PasteConfigFromClipboardIntoEditor();

    private void OnPasteWireGuardClick(object sender, RoutedEventArgs e) => PasteConfigFromClipboardIntoEditor();

    private void PasteConfigFromClipboardIntoEditor()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
                return;

            ApplyImportedConfigText(System.Windows.Clipboard.GetText());
        }
        catch (Exception ex)
        {
            ValidationText.Text = LocalizationService.Instance.Format("خواندن کلیپ‌بورد ناموفق بود: {0}", ex.Message);
        }
    }

    private void ApplyImportedConfigText(string? rawText)
    {
        var drafts = ConfigImportService.ParseClipboard(rawText);
        var valid = drafts.Where(d => string.IsNullOrWhiteSpace(d.SkipReason)).ToList();

        if (valid.Count > 1)
        {
            ValidationText.Text = LocalizationService.Instance.T("چند کانفیگ پیدا شد؛ از دکمه «چسباندن کانفیگ» در تب اتصال استفاده کنید.");
            return;
        }

        if (valid.Count == 1)
        {
            ApplyDraftToProfile(valid[0]);
            if (string.IsNullOrWhiteSpace(_profile.Name))
                ProfileNameField.Text = valid[0].SuggestedName;
            ValidationText.Text = "";
            RefreshOpenVpnProfileUi();
            return;
        }

        ValidationText.Text = drafts.FirstOrDefault()?.SkipReason
                              ?? LocalizationService.Instance.T("فرمت کانفیگ شناخته نشد");
    }

    private void ApplyDraftToProfile(ImportedConfigDraft draft)
    {
        _profile.TunnelType = draft.TunnelType;
        switch (draft.TunnelType)
        {
            case TunnelType.V2Ray:
                _profile.V2RayConfig = draft.ConfigText;
                break;
            case TunnelType.OpenVpn:
                _profile.OpenVpnConfig = draft.ConfigText;
                _profile.OpenVpnConfigPath = "";
                break;
            case TunnelType.WireGuard:
                _profile.WireGuardConfig = draft.ConfigText;
                _profile.WireGuardConfigPath = "";
                break;
            case TunnelType.SocksProxy:
                var socks = ConfigImportService.CreateProfile(draft);
                _profile.ProxyProtocol = socks.ProxyProtocol;
                _profile.ProxyServerAddress = socks.ProxyServerAddress;
                _profile.ProxyPort = socks.ProxyPort;
                _profile.ProxyUsername = socks.ProxyUsername;
                ProxyPasswordField.Password = socks.ProxyPassword;
                break;
        }

        if (string.IsNullOrWhiteSpace(_profile.Name))
            _profile.Name = draft.SuggestedName;
    }

    private void OnClearV2RayClick(object sender, RoutedEventArgs e)
    {
        _profile.V2RayConfig = "";
    }

    private void OnClearWireGuardClick(object sender, RoutedEventArgs e)
    {
        _profile.WireGuardConfig = "";
        _profile.WireGuardConfigPath = "";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _profile.Name = (_profile.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(_profile.Name))
        {
            ShowProfileNameError();
            return;
        }

        _profile.Password = L2tpPasswordField.Password;
        _profile.PreSharedKey = PskField.Password;
        _profile.OpenVpnPassword = OpenVpnPasswordField.Password;
        _profile.OpenVpnPrivateKeyPassword = OpenVpnPrivateKeyPasswordField.Password;
        _profile.ProxyPassword = ProxyPasswordField.Password;

        if (!ValidateProfile(out var message))
        {
            ValidationText.Text = message;
            return;
        }

        DialogResult = true;
        Close();
    }

    private bool ValidateProfile(out string message)
    {
        if (string.IsNullOrWhiteSpace(_profile.Name))
        {
            message = LocalizationService.Instance.T("نام پروفایل را وارد کنید");
            return false;
        }

        switch (_profile.TunnelType)
        {
            case TunnelType.L2tpIpsec when string.IsNullOrWhiteSpace(_profile.ServerAddress):
                message = LocalizationService.Instance.T("آدرس سرور L2TP را وارد کنید");
                return false;
            case TunnelType.V2Ray when string.IsNullOrWhiteSpace(_profile.V2RayConfig):
                message = LocalizationService.Instance.T("کانفیگ V2Ray/Xray را وارد کنید");
                return false;
            case TunnelType.OpenVpn when OpenVpnProfileAnalyzer.TryGetProfileValidationError(
                _profile.OpenVpnConfig,
                _profile.OpenVpnUsername,
                OpenVpnPasswordField.Password,
                OpenVpnPrivateKeyPasswordField.Password,
                out message):
                return false;
            case TunnelType.SocksProxy when string.IsNullOrWhiteSpace(_profile.ProxyServerAddress):
                message = LocalizationService.Instance.T("آدرس سرور پراکسی را وارد کنید");
                return false;
            case TunnelType.SocksProxy when _profile.ProxyPort <= 0 || _profile.ProxyPort > 65535:
                message = LocalizationService.Instance.T("پورت پراکسی باید بین 1 تا 65535 باشد");
                return false;
            case TunnelType.WireGuard when !WireGuardConfigParser.TryParse(_profile.WireGuardConfig, out _, out var wireGuardError):
                message = wireGuardError;
                return false;
        }

        message = "";
        return true;
    }

    private void OnProfileNameTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ProfileNameField.Text))
            ClearProfileNameError();
    }

    private void ShowProfileNameError()
    {
        var loc = LocalizationService.Instance;
        var warningBrush = TryFindResource("WarningBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Orange;
        ProfileNameField.BorderBrush = warningBrush;
        ProfileNameField.BorderThickness = new Thickness(2);
        ProfileNameValidationText.Text = loc.T("نام پروفایل را وارد کنید");
        ProfileNameValidationText.FlowDirection = loc.FlowDirection;
        ProfileNameValidationText.TextAlignment = loc.TextAlignment;
        ProfileNameValidationText.Visibility = Visibility.Visible;
        ValidationText.Text = "";
        ProfileNameField.Focus();
    }

    private void ClearProfileNameError()
    {
        ProfileNameField.ClearValue(BorderBrushProperty);
        ProfileNameField.ClearValue(BorderThicknessProperty);
        ProfileNameValidationText.Visibility = Visibility.Collapsed;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
