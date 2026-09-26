using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AppTunnel.Services;

internal sealed class ProbeProcessExitedException : Exception
{
    public ProbeProcessExitedException(int exitCode)
        : base($"probe process exited early ({exitCode})")
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}

/// <summary>
/// Waits until a loopback port accepts TCP, and stops a short-lived core process used for probing.
/// The port reservation that picked the number must already be released so the core can bind it.
/// </summary>
internal static class LocalPortWait
{
    public static async Task UntilAcceptingAsync(
        int port,
        TimeSpan timeout,
        CancellationToken ct,
        Process? process,
        string timeoutMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfExited(process);

            try
            {
                using var tcp = new TcpClient();
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromMilliseconds(400));
                await tcp.ConnectAsync(IPAddress.Loopback, port, connectCts.Token);
                ThrowIfExited(process);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The short connect attempt timed out. Keep waiting for the process to listen.
            }
            catch (SocketException)
            {
                // Nothing is listening yet.
            }

            await Task.Delay(200, ct);
        }

        ThrowIfExited(process);
        throw new TimeoutException(timeoutMessage);
    }

    private static void ThrowIfExited(Process? process)
    {
        if (process is { HasExited: true })
            throw new ProbeProcessExitedException(process.ExitCode);
    }
}

internal static class ProbeProcess
{
    public static async Task StopAsync(Process? process)
    {
        if (process == null)
            return;

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    try { process.Kill(); } catch { /* already gone */ }
                }

                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch
        {
            // The probe process is best-effort cleanup.
        }
        finally
        {
            try { process.Dispose(); } catch { /* ignored */ }
        }
    }

    public static void AppendLogTail(StringBuilder tail, string line)
    {
        lock (tail)
        {
            if (tail.Length >= 400)
                return;
            tail.AppendLine(line);
        }
    }

    public static string FormatExitMessage(string localizedExitMessage, StringBuilder tail)
    {
        string extra;
        lock (tail)
            extra = tail.ToString().Trim();

        if (string.IsNullOrWhiteSpace(extra))
            return localizedExitMessage;

        extra = extra.Replace("\r", " ").Replace("\n", " ").Trim();
        if (extra.Length > 240)
            extra = extra[^240..];

        return localizedExitMessage + " — " + extra;
    }
}
