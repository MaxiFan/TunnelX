using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// Manages saving and loading connection profiles to/from a local JSON file.
/// Passwords and PSKs are encrypted using DPAPI (Windows Data Protection).
/// </summary>
public class ProfileService
{
    // All user data files live in %LOCALAPPDATA%\TunnelX\ — same folder used
    // for native DLLs and sing-box config.  This keeps the exe directory clean
    // and works correctly whether the app runs from Program Files, Desktop, or
    // as a self-contained single-file bundle from any arbitrary path.
    private static readonly string ProfileDir = AppTunnel.App.AppDataDir;

    // LegacyDir: where profiles lived before this change (next to the exe).
    // Kept only to migrate existing data on first run.
    private static readonly string LegacyDir =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static readonly string ProfileFile = Path.Combine(ProfileDir, "profiles.json");
    private static readonly string ExcludesFile = Path.Combine(ProfileDir, "excludes.json");
    private static readonly string IncludesFile = Path.Combine(ProfileDir, "includes.json");
    private static readonly string TunnelAppsFile = Path.Combine(ProfileDir, "tunnelapps.json");
    private static readonly string AppSettingsFile = Path.Combine(ProfileDir, "appsettings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static ProfileService()
    {
        MigrateIfNeeded(Path.Combine(LegacyDir, "profiles.json"), ProfileFile);
        MigrateIfNeeded(Path.Combine(LegacyDir, "excludes.json"), ExcludesFile);
        MigrateIfNeeded(Path.Combine(LegacyDir, "includes.json"), IncludesFile);
        MigrateIfNeeded(Path.Combine(LegacyDir, "tunnelapps.json"), TunnelAppsFile);
    }

    private static void MigrateIfNeeded(string legacyPath, string newPath)
    {
        if (!File.Exists(newPath) && File.Exists(legacyPath))
        {
            try { File.Copy(legacyPath, newPath); }
            catch { /* ignore — old data inaccessible */ }
        }
    }

    /// <summary>
    /// Load global application settings from disk.
    /// </summary>
    public AppSettings LoadAppSettings()
    {
        if (!File.Exists(AppSettingsFile))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(AppSettingsFile, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Save global application settings to disk.
    /// </summary>
    public void SaveAppSettings(AppSettings settings)
    {
        Directory.CreateDirectory(ProfileDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(AppSettingsFile, json, Encoding.UTF8);
    }

    /// <summary>
    /// Global application settings (startup + auto-connect preferences).
    /// </summary>
    public class AppSettings
    {
        public bool StartWithWindows { get; set; } = false;
        public bool AutoConnectOnStartup { get; set; } = false;
        public string? LastActiveProfileId { get; set; } = null;
        public string Language { get; set; } = LocalizationService.AutoLanguage;
        public long? GitHubAppDownloadCount { get; set; } = null;
        /// <summary>Tray toasts for connection/app status (not updates or Telegram promos).</summary>
        public bool EnableInformationalNotifications { get; set; } = true;

        /// <summary>
        /// Custom connection health-check targets, one URL, hostname, or IP per line.
        /// Empty keeps the built-in google.com and cloudflare.com probes.
        /// </summary>
        public string HealthCheckEndpoints { get; set; } = "";

        /// <summary>
        /// When false, google.com and cloudflare.com are not probed.
        /// Ignored when no valid custom target is configured, so health checks still have a destination.
        /// </summary>
        public bool IncludeDefaultHealthCheckEndpoints { get; set; } = true;
    }

    /// <summary>
    /// Load all saved profiles from disk.
    /// </summary>
    public List<ConnectionProfile> LoadProfiles()
    {
        if (!File.Exists(ProfileFile))
            return new List<ConnectionProfile>();

        try
        {
            var json = File.ReadAllText(ProfileFile, Encoding.UTF8);
            var stored = JsonSerializer.Deserialize<List<StoredProfile>>(json, JsonOptions)
                ?? new List<StoredProfile>();

            return stored.Select(s => new ConnectionProfile
            {
                Id = s.Id,
                Name = s.Name,
                CreatedAt = s.CreatedAt,
                LastUsedAt = s.LastUsedAt,
                ServerAddress = s.ServerAddress,
                Username = s.Username,
                Password = DecryptString(s.EncryptedPassword),
                PreSharedKey = DecryptString(s.EncryptedPsk),
                TunnelType = s.TunnelType,
                V2RayConfig = s.V2RayConfig,
                OpenVpnConfig = s.OpenVpnConfig,
                OpenVpnConfigPath = !string.IsNullOrWhiteSpace(s.OpenVpnConfigPath)
                    ? s.OpenVpnConfigPath
                    : (s.OpenVpnExePath.EndsWith(".ovpn", StringComparison.OrdinalIgnoreCase) ? s.OpenVpnExePath : ""),
                OpenVpnUsername = s.OpenVpnUsername,
                OpenVpnPassword = DecryptString(s.EncryptedOpenVpnPassword),
                OpenVpnPrivateKeyPassword = DecryptString(s.EncryptedOpenVpnPrivateKeyPassword),
                WireGuardConfig = s.WireGuardConfig,
                WireGuardConfigPath = s.WireGuardConfigPath,
                ProxyProtocol = s.ProxyProtocol,
                ProxyServerAddress = s.ProxyServerAddress,
                ProxyPort = s.ProxyPort > 0 ? s.ProxyPort : 1080,
                ProxyUsername = s.ProxyUsername,
                ProxyPassword = DecryptString(s.EncryptedProxyPassword),
                MixedProxyPort = s.Socks5Port > 0 ? s.Socks5Port : 1080,
                AutoTuneMtu = s.AutoTuneMtu,
                EnableDnsOptimization = s.EnableDnsOptimization,
                EnableGameMode = s.EnableGameMode
            }).ToList();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[PROFILE] Failed to load profiles: {ex.Message}");
            return new List<ConnectionProfile>();
        }
    }

    /// <summary>
    /// Save all profiles to disk.
    /// </summary>
    public void SaveProfiles(IEnumerable<ConnectionProfile> profiles)
    {
        Directory.CreateDirectory(ProfileDir);

        var stored = profiles.Select(p => new StoredProfile
        {
            Id = p.Id,
            Name = p.Name,
            CreatedAt = p.CreatedAt,
            LastUsedAt = p.LastUsedAt,
            ServerAddress = p.ServerAddress,
            Username = p.Username,
            EncryptedPassword = EncryptString(p.Password),
            EncryptedPsk = EncryptString(p.PreSharedKey),
            TunnelType = p.TunnelType,
            V2RayConfig = p.V2RayConfig,
            OpenVpnConfig = p.OpenVpnConfig,
            OpenVpnConfigPath = p.OpenVpnConfigPath,
            OpenVpnUsername = p.OpenVpnUsername,
            EncryptedOpenVpnPassword = EncryptString(p.OpenVpnPassword),
            EncryptedOpenVpnPrivateKeyPassword = EncryptString(p.OpenVpnPrivateKeyPassword),
            WireGuardConfig = p.WireGuardConfig,
            WireGuardConfigPath = p.WireGuardConfigPath,
            ProxyProtocol = p.ProxyProtocol,
            ProxyServerAddress = p.ProxyServerAddress,
            ProxyPort = p.ProxyPort,
            ProxyUsername = p.ProxyUsername,
            EncryptedProxyPassword = EncryptString(p.ProxyPassword),
            Socks5Port = p.MixedProxyPort,
            AutoTuneMtu = p.AutoTuneMtu,
            EnableDnsOptimization = p.EnableDnsOptimization,
            EnableGameMode = p.EnableGameMode
        }).ToList();

        var json = JsonSerializer.Serialize(stored, JsonOptions);
        File.WriteAllText(ProfileFile, json, Encoding.UTF8);
    }

    /// <summary>
    /// Encrypt a string using Windows DPAPI (CurrentUser scope).
    /// Only the same Windows user on the same machine can decrypt.
    /// </summary>
    private static string EncryptString(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string DecryptString(string encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64)) return string.Empty;
        try
        {
            var encrypted = Convert.FromBase64String(encryptedBase64);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            // DPAPI fails if the data was encrypted by a different Windows
            // user (machine swap, profile migration). Returning an empty
            // password silently is confusing — log so the user can see why
            // the saved password no longer works.
            Logger.Warning($"[CRYPTO] DPAPI decrypt failed (saved password may need to be re-entered): {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Internal storage format — passwords stored encrypted.
    /// </summary>
    private class StoredProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime LastUsedAt { get; set; }
        public string ServerAddress { get; set; } = "";
        public string Username { get; set; } = "";
        public string EncryptedPassword { get; set; } = "";
        public string EncryptedPsk { get; set; } = "";
        public TunnelType TunnelType { get; set; } = TunnelType.L2tpIpsec;
        public string V2RayConfig { get; set; } = "";
        public string OpenVpnConfig { get; set; } = "";
        public string OpenVpnConfigPath { get; set; } = "";
        // Legacy field: early OpenVPN test builds accidentally stored .ovpn path here.
        public string OpenVpnExePath { get; set; } = "";
        public string OpenVpnUsername { get; set; } = "";
        public string EncryptedOpenVpnPassword { get; set; } = "";
        public string EncryptedOpenVpnPrivateKeyPassword { get; set; } = "";
        public string WireGuardConfig { get; set; } = "";
        public string WireGuardConfigPath { get; set; } = "";
        public ProxyProtocol ProxyProtocol { get; set; } = ProxyProtocol.Socks5;
        public string ProxyServerAddress { get; set; } = "";
        public int ProxyPort { get; set; } = 1080;
        public string ProxyUsername { get; set; } = "";
        public string EncryptedProxyPassword { get; set; } = "";
        [JsonPropertyName("socks5Port")]
        public int Socks5Port { get; set; } = 1080;
        public bool AutoTuneMtu { get; set; } = true;
        public bool EnableDnsOptimization { get; set; } = true;
        public bool EnableGameMode { get; set; } = false;
    }

    /// <summary>
    /// Load the global exclude list from disk.
    /// </summary>
    public List<string> LoadExcludes()
    {
        if (!File.Exists(ExcludesFile))
            return new List<string>();
        try
        {
            var json = File.ReadAllText(ExcludesFile, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Save the global exclude list to disk.
    /// </summary>
    public void SaveExcludes(IEnumerable<string> excludes)
    {
        Directory.CreateDirectory(ProfileDir);
        var json = JsonSerializer.Serialize(excludes.ToList(), JsonOptions);
        File.WriteAllText(ExcludesFile, json, Encoding.UTF8);
    }

    /// <summary>
    /// Load the global include list from disk.
    /// </summary>
    public List<string> LoadIncludes()
    {
        if (!File.Exists(IncludesFile))
            return new List<string>();
        try
        {
            var json = File.ReadAllText(IncludesFile, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Save the global include list to disk.
    /// </summary>
    public void SaveIncludes(IEnumerable<string> includes)
    {
        Directory.CreateDirectory(ProfileDir);
        var json = JsonSerializer.Serialize(includes.ToList(), JsonOptions);
        File.WriteAllText(IncludesFile, json, Encoding.UTF8);
    }

    /// <summary>
    /// Load the global tunnel apps list from disk.
    /// </summary>
    public List<ProfileApp> LoadTunnelApps()
    {
        if (!File.Exists(TunnelAppsFile))
            return new List<ProfileApp>();
        try
        {
            var json = File.ReadAllText(TunnelAppsFile, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<ProfileApp>>(json, JsonOptions) ?? new();
        }
        catch
        {
            return new List<ProfileApp>();
        }
    }

    /// <summary>
    /// Save the global tunnel apps list to disk.
    /// </summary>
    public void SaveTunnelApps(IEnumerable<ProfileApp> apps)
    {
        Directory.CreateDirectory(ProfileDir);
        var json = JsonSerializer.Serialize(apps.ToList(), JsonOptions);
        File.WriteAllText(TunnelAppsFile, json, Encoding.UTF8);
    }
}
