using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using AppTunnel.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AppTunnel;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\TunnelX.SingleInstance";
    private const string BringToFrontEventName = @"Global\TunnelX.BringToFront";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _bringToFrontEvent;
    private RegisteredWaitHandle? _bringToFrontRegistration;

    /// <summary>
    /// Persistent data directory: %LOCALAPPDATA%\TunnelX\
    /// All user data (profiles, config, native DLLs) is stored here so that
    /// the application can run from any read-only or temporary location and
    /// multiple instances share the same data folder.
    /// </summary>
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TunnelX");

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryAcquireSingleInstance())
        {
            SignalExistingInstanceToShow();
            Shutdown(0);
            return;
        }

        // Ensure the data directory exists before anything else.
        try
        {
            Directory.CreateDirectory(AppDataDir);
        }
        catch (Exception ex)
        {
            // Without the data directory we cannot extract native libraries
            // or persist any user data — there is nothing useful the app can
            // do, so report a clear error and exit instead of crashing later
            // with a confusing P/Invoke failure.
            MessageBox.Show(
                $"TunnelX cannot create its data directory:\n{AppDataDir}\n\n{ex.Message}\n\nPlease check that you have write permission to %LOCALAPPDATA%.",
                "TunnelX — خطای راه‌اندازی",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Extract WinDivert/wintun native files from embedded resources into
        // AppDataDir.  Must happen BEFORE the NativeLibrary resolver is
        // registered and before any DllImport call is made.
        EnsureNativeLibsExtracted();

        // Register a resolver so that DllImport("WinDivert.dll") and
        // DllImport("wintun.dll") load from AppDataDir rather than relying on
        // the default search order (which would only find them next to the exe).
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, asm, searchPath) =>
        {
            var candidate = Path.Combine(AppDataDir, name);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
            // Fall back to default resolution (regular build side-by-side DLLs).
            return IntPtr.Zero;
        });

        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        // Clean up any stale VPN connection left by a previous crash/kill.
        CleanupStaleVpn();

        // Safety net: if the process is killed, try to disconnect VPN.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { CleanupStaleVpn(); } catch { }
        };

        // Global exception handlers for debugging
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Logger.Error("Unhandled exception in AppDomain", ex);
            try { CleanupStaleVpn(); } catch { }
            MessageBox.Show(
                $"Critical error occurred:\n{ex?.Message}\n\nCheck Debug Log for details.",
                "TunnelX - Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (sender, args) =>
        {
            Logger.Error("Unhandled exception in Dispatcher", args.Exception);
            MessageBox.Show(
                $"UI error occurred:\n{args.Exception.Message}\n\nCheck Debug Log for details.",
                "TunnelX - Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true; // Prevent crash
        };

        Logger.Info("TunnelX application started");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("TunnelX application exiting");

        try { _bringToFrontRegistration?.Unregister(null); } catch { }
        try { _bringToFrontEvent?.Dispose(); } catch { }
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }

        base.OnExit(e);
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
                return false;

            _bringToFrontEvent = new EventWaitHandle(false, EventResetMode.AutoReset, BringToFrontEventName);
            _bringToFrontRegistration = ThreadPool.RegisterWaitForSingleObject(
                _bringToFrontEvent,
                (_, _) => Dispatcher.BeginInvoke(new Action(BringMainWindowToFront)),
                null,
                Timeout.Infinite,
                false);

            return true;
        }
        catch
        {
            return true;
        }
    }

    private static void SignalExistingInstanceToShow()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(BringToFrontEventName);
            evt.Set();
        }
        catch { }
    }

    private void BringMainWindowToFront()
    {
        if (MainWindow is MainWindow win)
        {
            win.BringToForeground();
            return;
        }

        if (Application.Current.MainWindow is MainWindow fallback)
            fallback.BringToForeground();
    }

    /// <summary>
    /// Disconnects and removes the VPN connection if it exists.
    /// Called on startup (to clean up after crash) and on fatal errors.
    /// Also removes duplicates like "TunnelX 2", "TunnelX 3" that Windows
    /// sometimes creates when the original wasn't fully removed.
    /// </summary>
    private static void CleanupStaleVpn()
    {
        // ── 0. Kill any stray sing-box process left over from a crash ──────────
        try
        {
            var killSingBox = new ProcessStartInfo
            {
                FileName               = "taskkill",
                Arguments              = "/F /IM sing-box.exe",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            using var kp = Process.Start(killSingBox);
            kp?.WaitForExit(3000);
        }
        catch { }

        const string vpnName = "TunnelX";
        try
        {
            // Disconnect if active
            var disconnectPsi = new ProcessStartInfo
            {
                FileName = "rasdial",
                Arguments = $"\"{vpnName}\" /disconnect",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var dp = Process.Start(disconnectPsi);
            dp?.WaitForExit(5000);

            // Find and remove all VPN connections matching "TunnelX" or "TunnelX N"
            var findPsi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"Get-VpnConnection -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^TunnelX' } | Select-Object -ExpandProperty Name\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var fp = Process.Start(findPsi);
            var output = fp?.StandardOutput.ReadToEnd() ?? "";
            fp?.WaitForExit(8000);

            var names = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => n.Length > 0).ToList();

            foreach (var name in names)
            {
                // Disconnect each variant
                var dPsi = new ProcessStartInfo
                {
                    FileName = "rasdial",
                    Arguments = $"\"{name}\" /disconnect",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var ddp = Process.Start(dPsi);
                ddp?.WaitForExit(3000);

                // Remove VPN profile
                var safeName = name.Replace("'", "''");
                var rmPsi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -Command \"Remove-VpnConnection -Name '{safeName}' -Force -ErrorAction SilentlyContinue\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var rp = Process.Start(rmPsi);
                rp?.WaitForExit(5000);
            }

            if (names.Count > 0)
                Logger.Info($"[CLEANUP] Removed {names.Count} stale VPN profile(s): {string.Join(", ", names)}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[CLEANUP] Stale VPN cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts WinDivert.dll, WinDivert64.sys and wintun.dll from embedded
    /// assembly resources into <see cref="AppDataDir"/>.
    /// For regular (non-single-file) builds the DLLs live next to the exe and
    /// are not embedded, so GetManifestResourceStream returns null — no-op.
    /// Each file is re-extracted only when it is missing or has a different
    /// size (i.e. a new version was just deployed).
    /// </summary>
    private static void EnsureNativeLibsExtracted()
    {
        var asm = Assembly.GetExecutingAssembly();

        foreach (var name in new[] { "WinDivert.dll", "WinDivert64.sys", "wintun.dll" })
        {
            try
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue; // not embedded → regular build, skip

                var destPath = Path.Combine(AppDataDir, name);

                // Skip if already extracted with the same size.
                if (File.Exists(destPath) && new FileInfo(destPath).Length == stream.Length)
                    continue;

                using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.CopyTo(fs);
                Logger.Info($"[NATIVE] Extracted {name} → {destPath}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[NATIVE] Could not extract {name}: {ex.Message}");
            }
        }
    }
}
