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
    private const string SingleInstanceMutexNameGlobal = @"Global\TunnelX.SingleInstance";
    private const string SingleInstanceMutexNameLocal = @"Local\TunnelX.SingleInstance";
    private const string BringToFrontEventNameGlobal = @"Global\TunnelX.BringToFront";
    private const string BringToFrontEventNameLocal = @"Local\TunnelX.BringToFront";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _bringToFrontEvent;
    private RegisteredWaitHandle? _bringToFrontRegistration;
    private string? _activeBringToFrontEventName;

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
        var orphanHosts = TunnelXHostProcess.CountOtherSamePathHosts();
        if (orphanHosts > 1)
        {
            Logger.Warning($"[STARTUP] Found {orphanHosts} extra TunnelX hosts; terminating orphans.");
            TunnelXHostProcess.TerminateOtherHosts();
        }

        if (!TryAcquireSingleInstance())
        {
            Logger.Info("[STARTUP] Another TunnelX instance is already running; forwarding show request.");
            SignalExistingInstanceToShow();
            Shutdown(0);
            return;
        }

        if (TunnelXHostProcess.CountOtherSamePathHosts() > 0)
        {
            Logger.Warning("[STARTUP] Removing leftover TunnelX host processes from a previous session.");
            TunnelXHostProcess.TerminateOtherHosts();
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
        NativeEngineSupport.EnsureAppNativeLibsExtracted();

        // Load the language before the main window is created so the first
        // rendered frame follows the saved setting or the system UI language.
        LocalizationService.Instance.Initialize(new ProfileService().LoadAppSettings().Language);
        Resources["FlagEmojiFont"] = FlagFontFamily.Instance;
        TextInputBehavior.Register();

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
        mainWindow.Dispatcher.BeginInvoke(
            () => mainWindow.EnsureStartupVisible(),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        Logger.Info("TunnelX application started");

        // Clean up engines left by a previous crash/kill. Keep off the UI thread.
        _ = Task.Run(() => CleanupStaleArtifacts("startup background", includeVpnProfiles: false));

        // Safety net: if the process is killed, try to disconnect VPN.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { CleanupStaleArtifacts("process exit", includeVpnProfiles: true, timeoutSeconds: 8); } catch { }
        };

        // Global exception handlers for debugging
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Logger.Error("Unhandled exception in AppDomain", ex);
            try { CleanupStaleArtifacts("fatal exception", includeVpnProfiles: true, timeoutSeconds: 5); } catch { }
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("TunnelX application exiting");

        try { CleanupStaleArtifacts("app exit", includeVpnProfiles: true, timeoutSeconds: 12); } catch { }

        ReleaseSingleInstanceResources();

        base.OnExit(e);
    }

    /// <summary>
    /// Ends the process immediately after cleanup. Prefer this over <see cref="Shutdown"/>
    /// while a connection attempt is still running in the background.
    /// </summary>
    internal static void ForceTerminateApplication()
    {
        try { TunnelXHostProcess.TerminateOtherHosts(); } catch { }

        if (Current is App app)
        {
            try { app.ReleaseSingleInstanceResources(); } catch { }
        }

        Environment.Exit(0);
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            if (!TryCreateSingleInstanceMutex(out var createdNew))
            {
                var alive = IsAnotherTunnelXProcessAlive() || TunnelXHostProcess.IsAnotherTunnelXHostAlive();
                Logger.Warning(alive
                    ? "[STARTUP] Single-instance mutex unavailable and another host is alive; refusing second instance."
                    : "[STARTUP] Single-instance mutex unavailable; no other host detected, continuing.");
                return !alive;
            }

            if (!createdNew)
            {
                if (TunnelXHostProcess.TryTerminateUnresponsiveHosts())
                {
                    TryCreateSingleInstanceMutex(out createdNew);
                    Logger.Warning("[STARTUP] Recovered from an unresponsive TunnelX instance.");
                }

                if (TunnelXHostProcess.IsAnotherTunnelXHostAlive())
                    return false;

                if (!createdNew)
                    Logger.Warning("[STARTUP] Recovering abandoned TunnelX single-instance lock.");
            }

            if (!SetupSingleInstanceCallbacks())
            {
                Logger.Warning("[STARTUP] Bring-to-front event unavailable; enforcing single-instance using process probe.");
                return !IsAnotherTunnelXProcessAlive();
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[STARTUP] Single-instance lock failed; falling back to process probe: {ex.Message}");
            return !IsAnotherTunnelXProcessAlive();
        }
    }

    private static bool IsAnotherTunnelXProcessAlive()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            string? currentPath = null;
            try { currentPath = current.MainModule?.FileName; } catch { }

            foreach (var p in Process.GetProcessesByName(current.ProcessName))
            {
                if (p.Id == current.Id)
                    continue;

                if (currentPath == null)
                    return true;

                try
                {
                    var otherPath = p.MainModule?.FileName;
                    if (string.Equals(otherPath, currentPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // If we cannot inspect the process path (permissions/race), ignore it.
                }
            }

            return false;
        }
        catch
        {
            // If process enumeration fails, avoid false positives and let mutex/event logic decide.
            return false;
        }
    }

    private bool TryCreateSingleInstanceMutex(out bool createdNew)
    {
        createdNew = false;
        var names = new[]
        {
            SingleInstanceMutexNameGlobal,
            SingleInstanceMutexNameLocal,
            "TunnelX.SingleInstance"
        };

        foreach (var name in names)
        {
            try
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = new Mutex(true, name, out createdNew);
                Logger.Info($"[STARTUP] Single-instance mutex initialized with '{name}'.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[STARTUP] Mutex '{name}' failed: {ex.Message}");
            }
        }

        return false;
    }

    private bool SetupSingleInstanceCallbacks()
    {
        var names = new[]
        {
            BringToFrontEventNameGlobal,
            BringToFrontEventNameLocal,
            "TunnelX.BringToFront"
        };

        foreach (var name in names)
        {
            try
            {
                _bringToFrontEvent?.Dispose();
                _bringToFrontEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name);
                _activeBringToFrontEventName = name;
                _bringToFrontRegistration?.Unregister(null);
                _bringToFrontRegistration = ThreadPool.RegisterWaitForSingleObject(
                    _bringToFrontEvent,
                    (_, _) => Dispatcher.BeginInvoke(new Action(BringMainWindowToFront)),
                    null,
                    Timeout.Infinite,
                    false);

                Logger.Info($"[STARTUP] Bring-to-front event initialized with '{name}'.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[STARTUP] Bring-to-front event '{name}' failed: {ex.Message}");
            }
        }

        Logger.Warning("[STARTUP] Bring-to-front event setup failed for all scopes.");
        return false;
    }

    private void ReleaseSingleInstanceResources()
    {
        try { _bringToFrontRegistration?.Unregister(null); } catch { }
        try { _bringToFrontEvent?.Dispose(); } catch { }
        _bringToFrontRegistration = null;
        _bringToFrontEvent = null;

        try
        {
            if (_singleInstanceMutex != null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                _singleInstanceMutex.Dispose();
            }
        }
        catch { }
        finally
        {
            _singleInstanceMutex = null;
        }
    }

    private static void SignalExistingInstanceToShow()
    {
        var names = new[]
        {
            BringToFrontEventNameGlobal,
            BringToFrontEventNameLocal,
            "TunnelX.BringToFront"
        };

        foreach (var name in names)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(name);
                evt.Set();
                Logger.Info($"[STARTUP] Foreground signal sent via '{name}'.");
                return;
            }
            catch
            {
                // Try next scope.
            }
        }

        TryBringExistingProcessWindowToFront();
    }

    private static void TryBringExistingProcessWindowToFront()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            var target = Process.GetProcessesByName(current.ProcessName)
                .Where(p => p.Id != current.Id)
                .OrderBy(p => p.StartTime)
                .FirstOrDefault();
            if (target == null)
                return;

            var hwnd = target.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                return;

            ShowWindowAsync(hwnd, SwShow);
            ShowWindowAsync(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // Best-effort fallback.
        }
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
    private static void CleanupStaleArtifacts(string reason, bool includeVpnProfiles, int timeoutSeconds = 10)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            TunnelXCleanupService.CleanupAllAsync(reason, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Logger.Warning($"[CLEANUP] Timed out after {timeoutSeconds}s during '{reason}'");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[CLEANUP] Central cleanup failed: {ex.Message}");
        }

        if (!includeVpnProfiles)
            return;

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

    private const int SwRestore = 9;
    private const int SwShow = 5;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

}
