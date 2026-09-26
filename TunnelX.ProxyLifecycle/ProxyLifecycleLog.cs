using System.Globalization;
using System.Text;

namespace TunnelX.ProxyLifecycle;

/// <summary>
/// Append-only lifecycle log consumers can tail. Each line is
/// <c>{utc-timestamp} {host}:{port} is now connected|disconnected</c>.
/// </summary>
public static class ProxyLifecycleLog
{
    public const string FileName = "proxy-lifecycle.log";
    public const int DefaultMaxBytes = 256 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object Gate = new();

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TunnelX");

    public static string DefaultPath => Path.Combine(DefaultDirectory, FileName);

    public static void Append(string announcement, string? path = null, int maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrWhiteSpace(announcement))
            return;

        var target = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH':'mm':'ss'.'fff'Z'", CultureInfo.InvariantCulture);
        var line = $"{stamp} {announcement.Trim()}\n";
        lock (Gate)
        {
            File.AppendAllText(target, line, Utf8NoBom);
            TrimIfNeeded(target, maxBytes);
        }
    }

    private static void TrimIfNeeded(string path, int maxBytes)
    {
        if (maxBytes <= 0)
            return;

        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= maxBytes)
            return;

        var text = File.ReadAllText(path, Utf8NoBom);
        var keep = Math.Max(1, maxBytes / 2);
        if (text.Length <= keep)
            return;

        var slice = text[^keep..];
        var newline = slice.IndexOf('\n');
        if (newline >= 0 && newline < slice.Length - 1)
            slice = slice[(newline + 1)..];

        File.WriteAllText(path, slice, Utf8NoBom);
    }
}
