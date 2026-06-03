using System.Globalization;
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
    private static readonly Regex[] LeadingTimestampPatterns =
    [
        new(@"^(?:[+-]\d{4}\s+)?\d{4}[-/]\d{2}[-/]\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?\s+", RegexOptions.Compiled),
        new(@"^\[\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?\]\s+", RegexOptions.Compiled),
        new(@"^[A-Z][a-z]{2}\s+[A-Z][a-z]{2}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}\s+\d{4}\s+", RegexOptions.Compiled),
    ];
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
    private static DateTime _singBoxEndpointWindowStartUtc = DateTime.UtcNow;
    private static int _singBoxEndpointCount;
    private static string _singBoxEndpointSample = "";
    private static DateTime _singBoxTimeoutWindowStartUtc = DateTime.UtcNow;
    private static int _singBoxTimeoutCount;
    private static string _singBoxTimeoutSample = "";
    private static DateTime _singBoxBindWindowStartUtc = DateTime.UtcNow;
    private static int _singBoxBindCount;
    private static string _singBoxBindSample = "";
    private static DateTime _singBoxConnClosedWindowStartUtc = DateTime.UtcNow;
    private static int _singBoxConnClosedCount;
    private static string _singBoxConnClosedSample = "";

    private const int HotspotDiagnosticExportMaxLines = 480;
    private static readonly Regex SingBoxConnIdRegex = new(@"\[\d+\s+[\d.]+[a-z]*\]", RegexOptions.Compiled);
    private static readonly Regex SingBoxErrorAgeRegex = new(@"ERROR\[\d+\]", RegexOptions.Compiled);

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

        var cleaned = StripLeadingTimestamp(NormalizeMessage(line));
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
        string? collapseKey = TryGetLogCollapseKey(message);
        string compareMessage = collapseKey ?? message;

        if (string.Equals(_lastLevel, level, StringComparison.Ordinal) &&
            string.Equals(_lastMessage, compareMessage, StringComparison.Ordinal) &&
            (nowUtc - _lastMessageAtUtc).TotalSeconds <= (collapseKey != null ? 8 : 2))
        {
            _repeatCount++;
            _lastMessageAtUtc = nowUtc;
            return;
        }

        FlushRepeatSummary(nowUtc);

        var logEntry = FormatLogEntry(level, message, nowUtc);
        AppendLogEntry(logEntry);
        _lastLevel = level;
        _lastMessage = compareMessage;
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

    /// <summary>Full buffer with repetitive lines collapsed — for clipboard export.</summary>
    public static string GetAllLogsCompact(int maxLines = 600)
    {
        FlushNoisyProcessSummariesIfDue(force: true);
        FlushRepeatSummary(DateTime.UtcNow);
        FlushSingBoxBindSummary(DateTime.UtcNow);

        var lines = GetAllLogs()
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var collapsed = CollapseRepetitiveLogLines(lines);
        if (collapsed.Count > maxLines)
        {
            collapsed = collapsed.Take(maxLines - 1).ToList();
            collapsed.Add($"[LOG-EXPORT] truncated to {maxLines - 1} lines — use Copy Hotspot for hotspot-only export");
        }

        collapsed.Add($"[LOG-EXPORT] lines={collapsed.Count} bufferLines≈{lines.Count}");
        return string.Join(Environment.NewLine, collapsed);
    }

    /// <summary>
    /// Returns log lines related to Mobile Hotspot sharing (WinRT, ICS, routes, captive, etc.).
    /// </summary>
    public static string GetHotspotLogs(string? snapshotHeader = null)
        => GetHotspotDiagnosticLogs(snapshotHeader);

    /// <summary>
    /// Hotspot-focused log export: tagged hotspot lines plus routing/VPN context when a hotspot
    /// session appears in the buffer. Optional <paramref name="snapshotHeader"/> is prepended.
    /// </summary>
    public static string GetHotspotDiagnosticLogs(string? snapshotHeader = null)
    {
        var raw = GetAllLogs();
        var lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        bool sessionHasHotspot = lines.Any(IsHotspotPrimaryLogLine);

        var selected = new List<string>(capacity: Math.Min(lines.Length, 4096));
        if (!string.IsNullOrWhiteSpace(snapshotHeader))
        {
            selected.Add(snapshotHeader.TrimEnd());
            selected.Add(string.Empty);
        }

        int rawMatched = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (IsHotspotPrimaryLogLine(line) ||
                (sessionHasHotspot && IsHotspotContextLogLine(line)))
            {
                selected.Add(line);
                rawMatched++;
            }
        }

        if (selected.Count == 0)
            return string.Empty;

        var collapsed = CollapseRepetitiveLogLines(selected);
        int suppressed = Math.Max(0, rawMatched - collapsed.Count);
        int bindStormLines = CountBindStormExportLines(collapsed, out int bindStormErrors);
        int connAbortLines = collapsed.Count(l =>
            l.Contains("[CONN-ABORT]", StringComparison.OrdinalIgnoreCase));

        if (collapsed.Count > HotspotDiagnosticExportMaxLines)
        {
            int keep = HotspotDiagnosticExportMaxLines - 1;
            collapsed = collapsed.Take(keep).Concat(
                new[]
                {
                    $"[HOTSPOT-DIAG-EXPORT] export truncated: showing first {keep} collapsed lines " +
                    $"(of {collapsed.Count}); copy again after repro for tail"
                }).ToList();
        }

        collapsed.Add(string.Empty);
        if (bindStormErrors > 0)
        {
            collapsed.Add(
                $"[LOG-COLLAPSE] sing-box bind-invalid: ~{bindStormErrors} suppressed errors " +
                $"({bindStormLines} summary line(s) in export)");
        }

        collapsed.Add(
            $"[HOTSPOT-DIAG-EXPORT] lines={collapsed.Count} rawMatched={rawMatched} collapsedAway={suppressed} " +
            $"bindStormErrors≈{bindStormErrors} connAbortLines={connAbortLines} " +
            $"sessionHasHotspot={sessionHasHotspot} bufferChars≈{raw.Length}");
        return string.Join(Environment.NewLine, collapsed);
    }

    public static bool IsHotspotRelatedLogLine(string line)
        => IsHotspotPrimaryLogLine(line) || IsHotspotContextLogLine(line);

    private static bool IsHotspotPrimaryLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        if (line.Contains("[HOTSPOT", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[CAPTIVE]", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.Contains("[STATS]", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("hotspot=", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.Contains("[ENGINE]", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains("hotspot", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("Soft-restarting sing-box", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("Pausing sing-box", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (line.Contains("[CONFIG]", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains("bind_interface", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("outbound bind", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("for LAN server", StringComparison.OrdinalIgnoreCase)))
            return true;

        return IsSingBoxLanTunnelLogLine(line);
    }

    private static bool IsHotspotContextLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        if (line.Contains("[NIC-BASELINE]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[ROUTE+]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[ROUTE-GC]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[ROUTE-DIAG]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[ROUTE-AFTER]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[VPN-ROUTE]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[LOG-DEDUP]", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.Contains("[ROUTE]", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("default route", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.Contains("[DIAG]", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains("defaultRoutes", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("primaryNic", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (line.Contains("[CONN-CHECK]", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains("tunnel server", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("192.168.", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("10.", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (line.Contains("[NET-STATS]", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("NAT", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.Contains("[STATS]", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.Contains("[PREREQ]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[CORE]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("V2Ray tunnel up", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("TrafficRouter starting", StringComparison.OrdinalIgnoreCase))
            return true;

        return IsSingBoxLanTunnelLogLine(line);
    }

    private static bool IsSingBoxLanTunnelLogLine(string line)
    {
        if (!line.Contains("sing-box", StringComparison.OrdinalIgnoreCase))
            return false;

        return line.Contains("dial tcp 192.168.", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("dial tcp 10.", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("dial tcp 172.", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("bind:", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("bind ", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("missing default interface", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("aborted by the software in your host machine", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("unreachable host", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("connection download closed", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("connection upload closed", StringComparison.OrdinalIgnoreCase);
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

        if (_repeatCount < 2)
        {
            _repeatCount = 0;
            return;
        }

        string summary = _repeatCount == 2
            ? "[LOG-DEDUP] previous message repeated once (same text within dedup window)"
            : $"[LOG-DEDUP] previous message repeated {_repeatCount} times (same text within dedup window)";
        _repeatCount = 0;
        AppendRaw("INFO", summary, nowUtc);
    }

    /// <summary>Stable key for near-duplicate sing-box / route lines so live dedup can merge them.</summary>
    private static string? TryGetLogCollapseKey(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        if (message.Contains("[sing-box", StringComparison.OrdinalIgnoreCase))
            return NormalizeSingBoxCollapseKey(message);

        if (message.Contains("[HOTSPOT-ROUTE] Purged ", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.Replace(
                message,
                @"iface=[^\s]+",
                "iface=*",
                RegexOptions.IgnoreCase);
        }

        if (message.Contains("[STATS]", StringComparison.OrdinalIgnoreCase))
            return "stats:" + Regex.Replace(message, @"\d+", "#");

        if (message.Contains("[HOTSPOT-DETECT] selected ifIdx=", StringComparison.OrdinalIgnoreCase))
            return "hotspot-detect:selected";

        if (message.Contains("[HOTSPOT-WINRT] Upstream scoring", StringComparison.OrdinalIgnoreCase))
            return "hotspot-winrt:upstream-scoring";

        if (message.Contains("[HOTSPOT-WINRT] Upstream candidates=", StringComparison.OrdinalIgnoreCase))
            return "hotspot-winrt:upstream-candidates";

        if (message.Contains("[HOTSPOT-WINRT] CreateFromConnectionProfile OK", StringComparison.OrdinalIgnoreCase))
            return "hotspot-winrt:profile-ok";

        if (message.Contains("[HOTSPOT-WINRT] TetheringOperationalState=", StringComparison.OrdinalIgnoreCase))
            return "hotspot-winrt:operational-state";

        if (message.Contains("[HOTSPOT-WINRT] ARP map entries=", StringComparison.OrdinalIgnoreCase))
            return "hotspot-winrt:arp-map";

        if (message.Contains("[HOTSPOT-WINRT] GetTetheringClients count=", StringComparison.OrdinalIgnoreCase))
            return "hotspot-winrt:clients";

        if (message.Contains("[DIAG] defaultRoutes=", StringComparison.OrdinalIgnoreCase))
            return "diag:default-routes:" + Regex.Replace(message, @"\d+", "#");

        if (message.Contains("[DNS-RULE] Query ", StringComparison.OrdinalIgnoreCase))
            return "dns-rule:" + Regex.Replace(message, @"'[^']*'", "'*'");

        return null;
    }

    private static string NormalizeSingBoxCollapseKey(string message)
    {
        string text = message;
        int connIdx = text.IndexOf("connection:", StringComparison.OrdinalIgnoreCase);
        if (connIdx >= 0)
            text = text[connIdx..];
        else
        {
            int netIdx = text.IndexOf("network:", StringComparison.OrdinalIgnoreCase);
            if (netIdx >= 0)
                text = text[netIdx..];
        }

        text = SingBoxConnIdRegex.Replace(text, "[*]");
        text = SingBoxErrorAgeRegex.Replace(text, "ERROR[*]");
        text = Regex.Replace(text, @"\b\d{1,3}(?:\.\d{1,3}){3}\b", "<ip>");

        if (text.Contains("bind:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("missing default interface", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unreachable host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no route to host", StringComparison.OrdinalIgnoreCase))
            return "sb:bind-invalid";

        return "sb:" + text.Trim();
    }

    private static List<string> CollapseRepetitiveLogLines(IReadOnlyList<string> lines)
    {
        var result = new List<string>(lines.Count);
        string? currentKey = null;
        string? firstLine = null;
        string? lastLine = null;
        int runCount = 0;

        void FlushRun()
        {
            if (runCount <= 0 || firstLine == null)
                return;

            result.Add(firstLine);
            if (runCount > 1)
            {
                string tail = lastLine != null && !string.Equals(lastLine, firstLine, StringComparison.Ordinal)
                    ? $" Last: {ExtractLogMessageBody(lastLine)}"
                    : "";
                result.Add(
                    $"[LOG-COLLAPSE] ↑ repeated {runCount} times (same error pattern).{tail}");
            }

            currentKey = null;
            firstLine = null;
            lastLine = null;
            runCount = 0;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushRun();
                result.Add(line);
                continue;
            }

            if (line.StartsWith("[HOTSPOT-DIAG-EXPORT]", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("=== ", StringComparison.Ordinal) ||
                line.StartsWith("Captured:", StringComparison.Ordinal) ||
                line.StartsWith("App:", StringComparison.Ordinal) ||
                line.StartsWith("VPN:", StringComparison.Ordinal) ||
                line.StartsWith("Sharing:", StringComparison.Ordinal) ||
                line.StartsWith("Windows AP:", StringComparison.Ordinal) ||
                line.StartsWith("Subnet:", StringComparison.Ordinal) ||
                line.StartsWith("Phase:", StringComparison.Ordinal) ||
                line.StartsWith("Datapath:", StringComparison.Ordinal) ||
                line.StartsWith("Upstream:", StringComparison.Ordinal) ||
                line.StartsWith("Compat:", StringComparison.Ordinal) ||
                line.StartsWith("Status:", StringComparison.Ordinal) ||
                line.StartsWith("WinRT debug", StringComparison.Ordinal) ||
                line.Equals("=== Log (hotspot-tagged + session context) ===", StringComparison.OrdinalIgnoreCase))
            {
                FlushRun();
                result.Add(line);
                continue;
            }

            string? key = TryGetExportCollapseKey(line);
            if (key == null)
            {
                FlushRun();
                result.Add(line);
                continue;
            }

            if (string.Equals(currentKey, key, StringComparison.Ordinal))
            {
                runCount++;
                lastLine = line;
                continue;
            }

            FlushRun();
            currentKey = key;
            firstLine = line;
            lastLine = line;
            runCount = 1;
        }

        FlushRun();
        return result;
    }

    private static string? TryGetExportCollapseKey(string line)
    {
        string body = ExtractLogMessageBody(line);
        if (string.IsNullOrWhiteSpace(body))
            return null;

        if (body.StartsWith("[LOG-DEDUP]", StringComparison.OrdinalIgnoreCase) ||
            body.StartsWith("[LOG-COLLAPSE]", StringComparison.OrdinalIgnoreCase))
            return null;

        if (body.Contains("[BIND-STORM]", StringComparison.OrdinalIgnoreCase))
            return "bindstorm";

        if (body.Contains("[CONN-ABORT]", StringComparison.OrdinalIgnoreCase))
            return "conn-abort";

        if (body.Contains("[sing-box", StringComparison.OrdinalIgnoreCase))
            return "sb:" + NormalizeSingBoxCollapseKey(body);

        if (body.Contains("[STATS]", StringComparison.OrdinalIgnoreCase))
            return "stats:" + Regex.Replace(body, @"\d+", "#", RegexOptions.None);

        if (body.Contains("[HOTSPOT-ROUTE] Purged ", StringComparison.OrdinalIgnoreCase))
            return "purge:" + Regex.Replace(body, @"\d{1,3}(?:\.\d{1,3}){3}", "<ip>");

        if (body.Contains("bind: The requested address is not valid", StringComparison.OrdinalIgnoreCase))
            return "bind-invalid:" + NormalizeSingBoxCollapseKey(body);

        return null;
    }

    private static int CountBindStormExportLines(List<string> lines, out int totalErrors)
    {
        totalErrors = 0;
        int summaryLines = 0;
        foreach (var line in lines)
        {
            if (line.Contains("[LOG-COLLAPSE]", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("repeated", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("[BIND-STORM]", StringComparison.OrdinalIgnoreCase))
            {
                summaryLines++;
                var m = Regex.Match(line, @"repeated\s+(\d+)\s+times", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    totalErrors += n;
                continue;
            }

            if (!line.Contains("[BIND-STORM]", StringComparison.OrdinalIgnoreCase))
                continue;

            summaryLines++;
            var em = Regex.Match(line, @"errors=(\d+)/", RegexOptions.IgnoreCase);
            if (em.Success && int.TryParse(em.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int e))
                totalErrors += e;
        }

        return summaryLines;
    }

    private static string ExtractLogMessageBody(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return line;

        int levelClose = line.IndexOf("] ", StringComparison.Ordinal);
        if (levelClose > 0 && line.StartsWith('['))
        {
            int levelOpen = line.LastIndexOf('[', levelClose - 1);
            if (levelOpen >= 0 && levelClose > levelOpen)
                return line[(levelClose + 2)..].Trim();
        }

        return line.Trim();
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
                 line.Contains(" accepted udp:", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains(" accepted //", StringComparison.OrdinalIgnoreCase));
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

            bool isSingBoxEndpointTransient =
                source.Contains("sing-box", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("endpoint not connected", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("report handshake success: connection refused", StringComparison.OrdinalIgnoreCase));
            if (isSingBoxEndpointTransient)
            {
                if ((nowUtc - _singBoxEndpointWindowStartUtc).TotalSeconds > 30)
                    FlushSingBoxEndpointSummary(nowUtc);

                _singBoxEndpointCount++;
                _singBoxEndpointSample = line;
                return true;
            }

            bool isSingBoxTimeoutTransient =
                source.Contains("sing-box", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("report handshake success: connection timed out", StringComparison.OrdinalIgnoreCase) ||
                 (line.Contains("using outbound/direct", StringComparison.OrdinalIgnoreCase) &&
                  line.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase)));
            if (isSingBoxTimeoutTransient)
            {
                if ((nowUtc - _singBoxTimeoutWindowStartUtc).TotalSeconds > 30)
                    FlushSingBoxTimeoutSummary(nowUtc);

                _singBoxTimeoutCount++;
                _singBoxTimeoutSample = line;
                return true;
            }

            bool isSingBoxUnreachableHost =
                source.Contains("sing-box", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("dial tcp", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("unreachable host", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("no route to host", StringComparison.OrdinalIgnoreCase));
            if (isSingBoxUnreachableHost)
            {
                if ((nowUtc - _singBoxBindWindowStartUtc).TotalSeconds > 30)
                    FlushSingBoxBindSummary(nowUtc);

                _singBoxBindCount++;
                if (_singBoxBindCount == 1 || _singBoxBindSample != line)
                    _singBoxBindSample = line;
                return true;
            }

            bool isSingBoxBindStorm =
                source.Contains("sing-box", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("bind:", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("missing default interface", StringComparison.OrdinalIgnoreCase) ||
                 (line.Contains("dial tcp", StringComparison.OrdinalIgnoreCase) &&
                  line.Contains(": bind:", StringComparison.OrdinalIgnoreCase)));
            if (isSingBoxBindStorm)
            {
                if ((nowUtc - _singBoxBindWindowStartUtc).TotalSeconds > 30)
                    FlushSingBoxBindSummary(nowUtc);

                _singBoxBindCount++;
                if (_singBoxBindCount == 1 || _singBoxBindSample != line)
                    _singBoxBindSample = line;
                if (_singBoxBindCount >= 50)
                    FlushSingBoxBindSummary(nowUtc);
                return true;
            }

            bool isSingBoxConnAborted =
                source.Contains("sing-box", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("connection download closed", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("connection upload closed", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("aborted by the software in your host machine", StringComparison.OrdinalIgnoreCase));
            if (isSingBoxConnAborted)
            {
                if ((nowUtc - _singBoxConnClosedWindowStartUtc).TotalSeconds > 30)
                    FlushSingBoxConnClosedSummary(nowUtc);

                _singBoxConnClosedCount++;
                _singBoxConnClosedSample = line;
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
            if (force || (nowUtc - _singBoxEndpointWindowStartUtc).TotalSeconds > 30)
                FlushSingBoxEndpointSummary(nowUtc);
            if (force || (nowUtc - _singBoxTimeoutWindowStartUtc).TotalSeconds > 30)
                FlushSingBoxTimeoutSummary(nowUtc);
            if (force || (nowUtc - _singBoxBindWindowStartUtc).TotalSeconds > 30)
                FlushSingBoxBindSummary(nowUtc);
            if (force || (nowUtc - _singBoxConnClosedWindowStartUtc).TotalSeconds > 30)
                FlushSingBoxConnClosedSummary(nowUtc);
        }
    }

    private static void FlushSingBoxBindSummary(DateTime nowUtc)
    {
        if (_singBoxBindCount <= 0)
            return;

        AppendRaw(
            "WARN",
            $"[sing-box] [BIND-STORM] errors={_singBoxBindCount}/30s pattern=sb:bind-invalid sample={_singBoxBindSample}",
            nowUtc);

        _singBoxBindCount = 0;
        _singBoxBindSample = "";
        _singBoxBindWindowStartUtc = nowUtc;
    }

    private static void FlushSingBoxConnClosedSummary(DateTime nowUtc)
    {
        if (_singBoxConnClosedCount <= 0)
            return;

        AppendRaw(
            "WARN",
            $"[sing-box] [CONN-ABORT] host-aborted={_singBoxConnClosedCount}/30s sample={_singBoxConnClosedSample}",
            nowUtc);

        _singBoxConnClosedCount = 0;
        _singBoxConnClosedSample = "";
        _singBoxConnClosedWindowStartUtc = nowUtc;
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

    private static void FlushSingBoxEndpointSummary(DateTime nowUtc)
    {
        if (_singBoxEndpointCount > 0)
        {
            var level = _singBoxEndpointCount >= 60 ? "WARN" : "INFO";
            AppendRaw(
                level,
                $"[sing-box] [TRANSIENT] endpoint-not-connected={_singBoxEndpointCount}/30s sample={_singBoxEndpointSample}",
                nowUtc);
        }
        _singBoxEndpointCount = 0;
        _singBoxEndpointSample = "";
        _singBoxEndpointWindowStartUtc = nowUtc;
    }

    private static void FlushSingBoxTimeoutSummary(DateTime nowUtc)
    {
        if (_singBoxTimeoutCount > 0)
        {
            var level = _singBoxTimeoutCount >= 40 ? "WARN" : "INFO";
            AppendRaw(
                level,
                $"[sing-box] [TRANSIENT] handshake-timeout={_singBoxTimeoutCount}/30s sample={_singBoxTimeoutSample}",
                nowUtc);
        }
        _singBoxTimeoutCount = 0;
        _singBoxTimeoutSample = "";
        _singBoxTimeoutWindowStartUtc = nowUtc;
    }

    private static void AppendRaw(string level, string message, DateTime nowUtc)
    {
        AppendLogEntry(FormatLogEntry(level, message, nowUtc));
    }

    private static readonly CultureInfo LogCulture = CultureInfo.InvariantCulture;

    private static string FormatLogEntry(string level, string message, DateTime utcNow)
    {
        // Technical logs always use Gregorian dates and Western digits, regardless of UI language.
        var timestamp = utcNow.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", LogCulture);
        return $"\u200E[{timestamp}] [{level}] {message}";
    }

    private static void AppendLogEntry(string logEntry)
    {
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

    private static string StripLeadingTimestamp(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        foreach (var pattern in LeadingTimestampPatterns)
        {
            if (pattern.IsMatch(message))
                return pattern.Replace(message, "").TrimStart();
        }

        return message;
    }
}
