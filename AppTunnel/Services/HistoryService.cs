using System.IO;
using System.Text;
using System.Text.Json;
using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// Manages connection history persistence.
/// </summary>
public class HistoryService
{
    private static readonly string HistoryDir = AppTunnel.App.AppDataDir;
    private static readonly string HistoryFile = Path.Combine(HistoryDir, "history.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const int MaxHistoryEntries = 100;

    public List<ConnectionHistoryEntry> LoadHistory()
    {
        if (!File.Exists(HistoryFile))
            return new List<ConnectionHistoryEntry>();

        try
        {
            var json = File.ReadAllText(HistoryFile, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<ConnectionHistoryEntry>>(json, JsonOptions)
                ?? new List<ConnectionHistoryEntry>();
        }
        catch
        {
            return new List<ConnectionHistoryEntry>();
        }
    }

    public void SaveHistory(IEnumerable<ConnectionHistoryEntry> history)
    {
        Directory.CreateDirectory(HistoryDir);

        // Keep only last N entries
        var entries = history.OrderByDescending(h => h.ConnectedAt).Take(MaxHistoryEntries).ToList();
        
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(HistoryFile, json, Encoding.UTF8);
    }

    public void AddEntry(ConnectionHistoryEntry entry)
    {
        var history = LoadHistory();
        history.Insert(0, entry);
        SaveHistory(history);
    }

    public void ClearHistory()
    {
        if (File.Exists(HistoryFile))
            File.Delete(HistoryFile);
    }
}
