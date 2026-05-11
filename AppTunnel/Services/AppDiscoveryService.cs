using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AppTunnel.Models;
using Microsoft.Win32;

namespace AppTunnel.Services;

/// <summary>
/// Discovers installed applications on the system by scanning:
/// 1. Registry uninstall keys (HKLM + HKCU)
/// 2. Start Menu shortcuts (.lnk — most reliable for all app types incl. Store apps)
/// 3. Windows Store app stubs in WindowsApps folder
/// 4. AppX/MSIX package registry
/// 5. Common program directories
/// </summary>
public static class AppDiscoveryService
{
    public static List<TunnelApp> GetInstalledApps()
    {
        var apps = new Dictionary<string, TunnelApp>(StringComparer.OrdinalIgnoreCase);

        // ── 1. Registry uninstall keys ──────────────────────────────────────
        // HKLM (system-wide installs)
        ScanRegistry(apps, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", useCurrentUser: false);
        ScanRegistry(apps, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", useCurrentUser: false);
        // HKCU (user-level installs: Chrome, Telegram, Spotify, etc.)
        ScanRegistry(apps, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", useCurrentUser: true);
        ScanRegistry(apps, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", useCurrentUser: true);

        // ── 2. Start Menu shortcuts (.lnk) — most reliable across all Windows versions ──
        // Every installed app (traditional + Store) has a Start Menu shortcut.
        // Resolving .lnk files gives us the actual exe path (or WindowsApps stub for Store apps).
        ScanStartMenuShortcuts(apps, Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        ScanStartMenuShortcuts(apps, Environment.GetFolderPath(Environment.SpecialFolder.Programs));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // ── 3. Windows Store app stubs ──────────────────────────────────────
        // These are tiny stub .exe files (< 100 KB) so the normal size filter is bypassed.
        // Scan subdirectories too — on some machines stubs are nested one level deep.
        ScanWindowsAppsStubs(apps, Path.Combine(localAppData, "Microsoft", "WindowsApps"));

        // ── 4. AppX/MSIX package registry ──────────────────────────────────
        // Catches Store apps that may not have WindowsApps stubs or Start Menu shortcuts.
        ScanAppxPackagesRegistry(apps);

        // ── 5. App Paths registry (HKLM + HKCU) ────────────────────────────
        // Every self-registering app (Telegram, WhatsApp, Firefox, etc.) writes its
        // exe path here on install. This is the most reliable cross-machine source.
        ScanAppPaths(apps);

        // ── 6. Currently running apps ───────────────────────────────────────
        // MSIX/Store apps can hide their real exe behind AppUserModelID shortcuts
        // or inaccessible WindowsApps metadata. Running-process discovery catches
        // apps such as Codex reliably and lets the user add them while they are open.
        ScanRunningProcesses(apps);

        // ── 7. Program directories ──────────────────────────────────────────
        ScanDirectory(apps, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        ScanDirectory(apps, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        ScanDirectory(apps, Path.Combine(localAppData, "Programs"));
        ScanDirectory(apps, Path.Combine(localAppData, "Google"));
        ScanDirectory(apps, Path.Combine(localAppData, "WhatsApp"));
        ScanDirectory(apps, Path.Combine(localAppData, "Discord"));
        ScanDirectory(apps, Path.Combine(appData, "Telegram Desktop"));
        ScanDirectory(apps, Path.Combine(appData, "Spotify"));

        return apps.Values
            .Where(a => !string.IsNullOrEmpty(a.ExecutablePath) && File.Exists(a.ExecutablePath))
            .Where(a => !IsSystemOrServiceApp(a.ExecutablePath!))
            .Where(a => IsMainExecutable(a.ExecutablePath!))
            .OrderBy(a => a.DisplayName)
            .ToList();
    }

    /// <summary>
    /// Get ALL related executables from the same installation folder.
    /// When user selects an app, call this to get all related exe files.
    /// </summary>
    public static List<TunnelApp> GetRelatedExecutables(string mainExePath)
    {
        var results = new List<TunnelApp>();
        var folder = Path.GetDirectoryName(mainExePath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) 
            return results;

        try
        {
            // Get all exe files in the app folder (up to 2 levels deep)
            var exeFiles = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetDirectories(folder)
                    .SelectMany(d => 
                    {
                        try { return Directory.GetFiles(d, "*.exe", SearchOption.TopDirectoryOnly); }
                        catch { return Array.Empty<string>(); }
                    }))
                .Where(f => !IsSystemOrServiceApp(f))
                .Where(f => !string.Equals(f, mainExePath, StringComparison.OrdinalIgnoreCase)) // Exclude the main file (already added)
                .ToList();

            foreach (var exePath in exeFiles)
            {
                var app = GetAppFromPath(exePath);
                if (app != null)
                    results.Add(app);
            }
        }
        catch { }

        return results;
    }

    /// <summary>
    /// Check if an executable is likely a "main" app (not a helper/subprocess).
    /// </summary>
    private static bool IsMainExecutable(string exePath)
    {
        var fileName = Path.GetFileName(exePath).ToLowerInvariant();

        // Exclude common subprocess/helper patterns embedded anywhere in the filename.
        // Simple Contains is intentional: "chrome-renderer.exe", "crashpad_handler.exe",
        // "gpu_process.exe" etc. should all be excluded.
        var helperPatterns = new[]
        {
            "gpu", "renderer", "crash", "updater", "helper",
            "nacl", "broker", "utility", "subprocess", "worker"
        };

        if (helperPatterns.Any(p => fileName.Contains(p)))
            return false;

        return true;
    }

    /// <summary>
    /// Filter out Windows services, system utilities, and internal helper executables.
    /// Important: substring matches are intentionally narrow. Earlier broad rules
    /// (e.g. "host", "agent", "service", "launcher", "console") were filtering out
    /// legitimate end-user applications such as game launchers, store apps and
    /// productivity tools whose name happens to contain those substrings.
    /// </summary>
    private static bool IsSystemOrServiceApp(string exePath)
    {
        var fileName = Path.GetFileName(exePath).ToLowerInvariant();
        var fileNameNoExt = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        var dirPath = Path.GetDirectoryName(exePath)?.ToLowerInvariant() ?? "";

        // Exclude Windows system directories
        var systemPaths = new[]
        {
            @"windows\system32",
            @"windows\syswow64",
            @"windows\winsxs",
            @"windows\servicing",
            @"microsoft.net\framework"
        };
        if (systemPaths.Any(p => dirPath.Contains(p)))
            return true;

        // Substring patterns that almost always mark a non-end-user binary.
        // Kept deliberately small — broad terms like "host"/"agent"/"service"
        // are matched only as suffixes below to avoid false positives.
        var excludeSubstrings = new[]
        {
            "unins", "uninst", "crashpad", "crashhandler", "crashreport",
            "telemetry", "msiexec", "regsvr", "rundll", "dllhost",
            "conhost", "svchost", "createdump", "prefetch", ".vshost.",
            ".resources."
        };
        if (excludeSubstrings.Any(p => fileName.Contains(p)))
            return true;

        // Suffix patterns: only match when the substring appears at the END of
        // the filename (e.g. "FooService.exe" but NOT "ServiceNow.exe").
        var excludeSuffixes = new[]
        {
            "service", "agent", "daemon", "subprocess", "host",
            "bootstrapper", "broker", "watchdog"
        };
        if (excludeSuffixes.Any(s => fileNameNoExt.EndsWith(s)))
            return true;

        // Common installer / setup names — these are usually one-off binaries
        // bundled with apps, not the app itself.
        var installerNames = new[]
        {
            "setup", "installer", "install", "vc_redist", "vcredist",
            "dotnetfx", "dxsetup"
        };
        if (installerNames.Any(s => fileNameNoExt == s || fileNameNoExt.StartsWith(s + "_") || fileNameNoExt.StartsWith(s + "-")))
            return true;

        // Exclude specific Windows-built-in apps
        var excludeExact = new[]
        {
            "cmd.exe", "powershell.exe", "pwsh.exe", "wsl.exe",
            "explorer.exe", "taskmgr.exe", "regedit.exe", "mmc.exe",
            "control.exe", "msconfig.exe", "systeminfo.exe", "perfmon.exe"
        };
        if (excludeExact.Contains(fileName))
            return true;

        return false;
    }

    public static TunnelApp? GetAppFromPath(string exePath)
    {
        if (!File.Exists(exePath)) return null;

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
            return new TunnelApp
            {
                DisplayName = versionInfo.FileDescription
                    ?? versionInfo.ProductName
                    ?? Path.GetFileNameWithoutExtension(exePath),
                ExecutablePath = exePath,
                ExecutableName = Path.GetFileName(exePath),
                Icon = ExtractIcon(exePath),
                IsEnabled = true
            };
        }
        catch
        {
            return new TunnelApp
            {
                DisplayName = Path.GetFileNameWithoutExtension(exePath),
                ExecutablePath = exePath,
                ExecutableName = Path.GetFileName(exePath),
                IsEnabled = true
            };
        }
    }

    /// <summary>
    /// Scans Start Menu folders for .lnk shortcuts and resolves their target exe paths.
    /// This is the most reliable cross-machine approach: every installed app (traditional
    /// and Store) creates a Start Menu shortcut. For Store apps the target is the stub
    /// in %LOCALAPPDATA%\Microsoft\WindowsApps\, which is accessible without elevation.
    /// </summary>
    private static void ScanStartMenuShortcuts(Dictionary<string, TunnelApp> apps, string startMenuDir)
    {
        if (!Directory.Exists(startMenuDir)) return;

        try
        {
            foreach (var lnkPath in Directory.EnumerateFiles(startMenuDir, "*.lnk", SearchOption.AllDirectories))
            {
                try
                {
                    var target = ResolveLnkTarget(lnkPath);
                    if (string.IsNullOrEmpty(target)) continue;
                    if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(target)) continue;
                    if (apps.ContainsKey(target)) continue;

                    var app = GetAppFromPath(target);
                    if (app != null)
                        apps[target] = app;
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Resolves a Windows .lnk shortcut to its TargetPath using WScript.Shell COM object.
    /// Returns null if the shortcut cannot be resolved or does not point to a file.
    /// </summary>
    private static string? ResolveLnkTarget(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            var target = shortcut.TargetPath as string;
            // Store app shortcuts may have empty TargetPath (protocol-based) — skip those
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }

    /// <summary>
    /// Scans HKLM and HKCU App Paths registry for all self-registered applications.
    /// This is the most reliable cross-machine source: every installer (Telegram,
    /// WhatsApp desktop, Firefox, Chrome, etc.) writes its exe path here so that
    /// Windows can launch the app by name without knowing the install directory.
    /// </summary>
    private static void ScanAppPaths(Dictionary<string, TunnelApp> apps)
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(keyPath);
                if (key == null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    if (!subKeyName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var sub = key.OpenSubKey(subKeyName);
                        var exePath = sub?.GetValue(null) as string; // default value = full exe path
                        if (string.IsNullOrEmpty(exePath)) continue;
                        exePath = exePath.Trim('"', ' ');
                        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!File.Exists(exePath) || apps.ContainsKey(exePath)) continue;

                        var app = GetAppFromPath(exePath);
                        if (app != null) apps[exePath] = app;
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Scans the AppModel Packages registry under HKCU and HKLM for MSIX/UWP
    /// Store app entries. Falls back gracefully when keys do not exist
    /// (older Windows versions or no Store apps installed).
    /// </summary>
    private static void ScanAppxPackagesRegistry(Dictionary<string, TunnelApp> apps)
    {
        const string appModelKey =
            @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        // Both per-user (HKCU) and system-wide (HKLM) AppX repositories.
        // Some Store apps (especially preinstalled ones) only register under HKLM.
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var root = hive.OpenSubKey(appModelKey);
                if (root == null) continue;

                foreach (var packageName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var pkgKey = root.OpenSubKey(packageName);
                        if (pkgKey == null) continue;

                        // PackageRootFolder points at C:\Program Files\WindowsApps\<package>
                        var pkgRoot = pkgKey.GetValue("PackageRootFolder") as string;

                        foreach (var appKeyName in pkgKey.GetSubKeyNames())
                        {
                            try
                            {
                                using var appKey = pkgKey.OpenSubKey(appKeyName);
                                if (appKey == null) continue;

                                var exePath = appKey.GetValue("Executable") as string;
                                if (string.IsNullOrEmpty(exePath)) continue;

                                // Resolve relative path against the package root,
                                // falling back to the WindowsApps stub folder.
                                if (!Path.IsPathRooted(exePath))
                                {
                                    if (!string.IsNullOrWhiteSpace(pkgRoot))
                                    {
                                        // Do not require Directory.Exists(pkgRoot): WindowsApps may
                                        // deny directory metadata access even when File.Exists on the
                                        // package executable succeeds.
                                        var candidate = Path.Combine(pkgRoot, exePath);
                                        exePath = File.Exists(candidate)
                                            ? candidate
                                            : Path.Combine(
                                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                "Microsoft", "WindowsApps", exePath);
                                    }
                                    else
                                    {
                                        exePath = Path.Combine(
                                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                            "Microsoft", "WindowsApps", exePath);
                                    }
                                }

                                if (!File.Exists(exePath) || apps.ContainsKey(exePath)) continue;

                                var displayName = appKey.GetValue("DisplayName") as string;
                                if (string.IsNullOrWhiteSpace(displayName) ||
                                    displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                                    displayName = null;

                                var app = GetAppFromPath(exePath);
                                if (app == null) continue;

                                if (!string.IsNullOrWhiteSpace(displayName))
                                    app.DisplayName = displayName;

                                apps[exePath] = app;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static void ScanRunningProcesses(Dictionary<string, TunnelApp> apps)
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var exePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(exePath)) continue;
                    if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(exePath)) continue;
                    if (apps.ContainsKey(exePath)) continue;

                    var app = GetAppFromPath(exePath);
                    if (app == null) continue;

                    if (string.IsNullOrWhiteSpace(app.DisplayName) ||
                        app.DisplayName.Equals(app.ExecutableName, StringComparison.OrdinalIgnoreCase))
                    {
                        app.DisplayName = process.ProcessName;
                    }

                    apps[exePath] = app;
                }
                catch
                {
                    // Some elevated/system processes deny MainModule access.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch { }
    }

    private static void ScanRegistry(Dictionary<string, TunnelApp> apps, string keyPath, bool useCurrentUser = false)    {
        try
        {
            var hive = useCurrentUser ? Registry.CurrentUser : Registry.LocalMachine;
            using var key = hive.OpenSubKey(keyPath);
            if (key == null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var displayName = subKey.GetValue("DisplayName") as string;
                    var installLocation = subKey.GetValue("InstallLocation") as string;
                    var displayIcon = subKey.GetValue("DisplayIcon") as string;
                    var systemComponent = subKey.GetValue("SystemComponent");

                    // Skip system components and entries without names
                    if (string.IsNullOrEmpty(displayName) || systemComponent is 1) continue;

                    // Try to find the main executable
                    var exePath = FindExecutable(installLocation, displayIcon, displayName);
                    if (string.IsNullOrEmpty(exePath) || apps.ContainsKey(exePath)) continue;

                    apps[exePath] = new TunnelApp
                    {
                        DisplayName = displayName,
                        ExecutablePath = exePath,
                        ExecutableName = Path.GetFileName(exePath),
                        Icon = ExtractIcon(exePath)
                    };
                }
                catch
                {
                    // Skip problematic entries
                }
            }
        }
        catch
        {
            // Registry access may fail
        }
    }

    private static void ScanDirectory(Dictionary<string, TunnelApp> apps, string directory)
    {
        if (!Directory.Exists(directory)) return;

        try
        {
            foreach (var exeFile in Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories)
                .Take(500)) // Limit scan depth
            {
                if (apps.ContainsKey(exeFile)) continue;

                try
                {
                    var info = FileVersionInfo.GetVersionInfo(exeFile);
                    var name = info.FileDescription ?? info.ProductName
                        ?? Path.GetFileNameWithoutExtension(exeFile);

                    // Skip very small utilities and updaters. Lowered from 100 KB to 30 KB
                    // because some legitimate apps (e.g. small launchers, tray-only tools,
                    // command-style apps) are smaller than the previous threshold.
                    var fileInfo = new FileInfo(exeFile);
                    if (fileInfo.Length < 30_000) continue;
                    var lowerName = Path.GetFileName(exeFile).ToLowerInvariant();
                    if (lowerName.Contains("unins") || lowerName.Contains("update") ||
                        lowerName.Contains("setup") || lowerName.Contains("crash"))
                        continue;

                    apps[exeFile] = new TunnelApp
                    {
                        DisplayName = name,
                        ExecutablePath = exeFile,
                        ExecutableName = Path.GetFileName(exeFile),
                        Icon = ExtractIcon(exeFile)
                    };
                }
                catch
                {
                    // Skip
                }
            }
        }
        catch
        {
            // Directory access may fail
        }
    }

    /// <summary>
    /// Scans %LOCALAPPDATA%\Microsoft\WindowsApps for Store app stub executables.
    /// These are App Execution Aliases (reparse points, typically 0-byte) so
    /// FileVersionInfo may throw — we fall back to the filename in that case.
    /// Per-package subdirectories (PackageFamilyName) are also scanned.
    /// </summary>
    private static void ScanWindowsAppsStubs(Dictionary<string, TunnelApp> apps, string directory)
    {
        if (!Directory.Exists(directory)) return;

        try
        {
            foreach (var exeFile in Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories))
            {
                if (apps.ContainsKey(exeFile)) continue;

                var lowerName = Path.GetFileName(exeFile).ToLowerInvariant();
                if (lowerName.Contains("unins") || lowerName.Contains("setup")) continue;

                // Use GetAppFromPath which gracefully handles App Execution Alias
                // (0-byte reparse point) by falling back to filename if version info
                // cannot be read.
                var app = GetAppFromPath(exeFile);
                if (app != null)
                    apps[exeFile] = app;
            }
        }
        catch { }
    }

    private static string? FindExecutable(string? installLocation, string? displayIcon, string? displayName)
    {
        // Try DisplayIcon path first (often points to exact exe)
        if (!string.IsNullOrEmpty(displayIcon))
        {
            var iconPath = displayIcon.Split(',')[0].Trim('"', ' ');
            if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath))
                return iconPath;
        }

        // Search install location for main exe
        if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
        {
            var exes = Directory.GetFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly);
            if (exes.Length == 1) return exes[0];

            // Try to match exe name with display name
            if (displayName != null)
            {
                var match = exes.FirstOrDefault(e =>
                    Path.GetFileNameWithoutExtension(e)
                        .Contains(displayName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            // Return the largest exe (likely the main one)
            return exes.OrderByDescending(e => new FileInfo(e).Length).FirstOrDefault();
        }

        return null;
    }

    public static BitmapSource? ExtractIcon(string exePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;

            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            
            // Freeze to make it thread-safe and usable across threads
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
