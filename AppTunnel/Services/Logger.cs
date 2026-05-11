using System.Text;
using System.Text.RegularExpressions;

namespace AppTunnel.Services;

public static class Logger
{
    // Bounded in-memory log buffer.  Without a cap, a long-lived connection
    // accumulates ReportStats() lines (every 5s) and packet-rewrite logs and
    // grows the WPF debug-log window's backing buffer indefinitely.  We
    // truncate the oldest half whenever the buffer exceeds MaxLogChars so the
    // most-recent diagnostics stay available without unbounded memory use.
    private const int MaxLogChars = 1_000_000; // ~1 MB of text
    private const int TruncateTo  =   500_000; // keep last ~500 KB after trim

    private static readonly StringBuilder _logs = new();
    private static readonly object _lock = new();
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static string? _lastLevel;
    private static string? _lastMessage;
    private static DateTime _lastMessageAtUtc;
    private static int _repeatCount;

    private static readonly object _procNoiseLock = new();
    private static DateTime _xrayAcceptedWindowStartUtc = DateTime.UtcNow;
    private static int _xrayAcceptedCount;
    private static string _xrayAcceptedSample = "";
    private static DateTime _singBoxResetWindowStartUtc = DateTime.UtcNow;
    private static int _singBoxResetCount;
    private static string _singBoxResetSample = "";

    public static event Action<string>? LogAdded;

    public static void Info(string message)
    {
        Log("INFO", message);
    }

    public static void Warning(string message)
    {
        Log("WARN", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        var fullMessage = exception != null 
            ? $"{message}\nException: {exception.GetType().Name}: {exception.Message}\nStackTrace: {exception.StackTrace}"
            : message;
        Log("ERROR", fullMessage);
    }

    public static void Debug(string message)
    {
        Log("DEBUG", message);
    }

    public static void ProcessOutput(string source, string line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var cleaned = NormalizeMessage(line);
        if (TryHandleNoisyProcessLine(source, cleaned))
            return;

        if (isError)
            Warning($"{source} {cleaned}");
        else
            Info($"{source} {cleaned}");
    }

    private static void Log(string level, string message)
    {
        message = NormalizeMessage(message);

        FlushNoisyProcessSummariesIfDue(force: false);

        var nowUtc = DateTime.UtcNow;
        if (string.Equals(_lastLevel, level, StringComparison.Ordinal) &&
            string.Equals(_lastMessage, message, StringComparison.Ordinal) &&
            (nowUtc - _lastMessageAtUtc).TotalSeconds <= 2)
        {
            _repeatCount++;
            _lastMessageAtUtc = nowUtc;
            return;
        }

        FlushRepeatSummary(nowUtc);

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logEntry = $"[{timestamp}] [{level}] {message}";

        lock (_lock)
        {
            _logs.AppendLine(logEntry);

            if (_logs.Length > MaxLogChars)
            {
                // Drop the oldest characters and align to the next line boundary
                // so we don't leave a partial line at the top of the buffer.
                int dropCount = _logs.Length - TruncateTo;
                int newline = _logs.ToString(dropCount, Math.Min(2048, _logs.Length - dropCount)).IndexOf('\n');
                if (newline >= 0) dropCount += newline + 1;
                _logs.Remove(0, dropCount);
            }
        }

        LogAdded?.Invoke(logEntry);
        _lastLevel = level;
        _lastMessage = message;
        _lastMessageAtUtc = nowUtc;
    }

    public static string GetAllLogs()
    {
        FlushNoisyProcessSummariesIfDue(force: true);
        FlushRepeatSummary(DateTime.UtcNow);

        lock (_lock)
        {
            return _logs.ToString();
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _logs.Clear();
        }
        _lastLevel = null;
        _lastMessage = null;
        _lastMessageAtUtc = DateTime.MinValue;
        _repeatCount = 0;
    }

    private static string NormalizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;
        var withoutAnsi = AnsiRegex.Replace(message, string.Empty);
        return withoutAnsi
            .Replace("\r\n", " | ")
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();
    }

    private static void FlushRepeatSummary(DateTime nowUtc)
    {
        if (_repeatCount <= 0)
            return;

        string summary = $"[LOG-DEDUP] previous message repeated {_repeatCount} times";
        _repeatCount = 0;
        AppendRaw("INFO", summary, nowUtc);
    }

    private static bool TryHandleNoisyProcessLine(string source, string line)
    {
        lock (_procNoiseLock)
        {
            var nowUtc = DateTime.UtcNow;
            bool isXrayAccepted =
                source.StartsWith("[xray]", StringComparison.OrdinalIgnoreCase) &&
                line.Contains(" accepted ", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains(" accepted tcp:", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains(" accepted udp:", StringComparison.OrdinalIgnoreCase));
            if (isXrayAccepted)
            {
                if ((nowUtc - _xrayAcceptedWindowStartUtc).TotalSeconds > 10)
                    FlushXrayAcceptedSummary(nowUtc);

                _xrayAcceptedCount++;
                _xrayAcceptedSample = ExtractAcceptedTarget(line);
                if (_xrayAcceptedCount % 50 == 0)
                    FlushXrayAcceptedSummary(nowUtc);
                return true;
            }

            bool isSingBoxRemoteReset =
                source.Contains("sing-box", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("forcibly closed by the remote host", StringComparison.OrdinalIgnoreCase);
            if (isSingBoxRemoteReset)
            {
                if ((nowUtc - _singBoxResetWindowStartUtc).TotalSeconds > 30)
                    FlushSingBoxResetSummary(nowUtc);

                _singBoxResetCount++;
                _singBoxResetSample = line;
                if (_singBoxResetCount % 10 == 0)
                    FlushSingBoxResetSummary(nowUtc);
                return true;
            }

            return false;
        }
    }

    private static string ExtractAcceptedTarget(string line)
    {
        int idx = line.IndexOf(" accepted ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return line;
        var value = line[(idx + " accepted ".Length)..];
        int endTag = value.IndexOf(" [", StringComparison.Ordinal);
        if (endTag > 0)
            value = value[..endTag];
        return value.Trim();
    }

    private static void FlushNoisyProcessSummariesIfDue(bool force)
    {
        lock (_procNoiseLock)
        {
            var nowUtc = DateTime.UtcNow;
            if (force || (nowUtc - _xrayAcceptedWindowStartUtc).TotalSeconds > 10)
                FlushXrayAcceptedSummary(nowUtc);
            if (force || (nowUtc - _singBoxResetWindowStartUtc).TotalSeconds > 30)
                FlushSingBoxResetSummary(nowUtc);
        }
    }

    private static void FlushXrayAcceptedSummary(DateTime nowUtc)
    {
        if (_xrayAcceptedCount > 0)
        {
            AppendRaw(
                "INFO",
                $"[xray] [TRAFFIC] accepted={_xrayAcceptedCount}/10s sample={_xrayAcceptedSample}",
                nowUtc);
        }
        _xrayAcceptedCount = 0;
        _xrayAcceptedSample = "";
        _xrayAcceptedWindowStartUtc = nowUtc;
    }

    private static void FlushSingBoxResetSummary(DateTime nowUtc)
    {
        if (_singBoxResetCount > 0)
        {
            var level = _singBoxResetCount >= 20 ? "WARN" : "INFO";
            AppendRaw(
                level,
                $"[sing-box] [TRANSIENT] remote-closed={_singBoxResetCount}/30s (usually upstream reset). sample={_singBoxResetSample}",
                nowUtc);
        }
        _singBoxResetCount = 0;
        _singBoxResetSample = "";
        _singBoxResetWindowStartUtc = nowUtc;
    }

    private static void AppendRaw(string level, string message, DateTime nowUtc)
    {
        var timestamp = nowUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var logEntry = $"[{timestamp}] [{level}] {message}";
        lock (_lock)
        {
            _logs.AppendLine(logEntry);
            if (_logs.Length > MaxLogChars)
            {
                int dropCount = _logs.Length - TruncateTo;
                int newline = _logs.ToString(dropCount, Math.Min(2048, _logs.Length - dropCount)).IndexOf('\n');
                if (newline >= 0) dropCount += newline + 1;
                _logs.Remove(0, dropCount);
            }
        }
        LogAdded?.Invoke(logEntry);
    }
}
