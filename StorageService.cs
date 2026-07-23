using System.Text.Json;

namespace RssReader;

// ─────────────────────────────────────────────────────────────
// StorageService is responsible for one thing only:
// reading from and writing to the subscriptions.json file.
//
// Think of it as the "librarian" of the app — it knows where
// everything is stored and how to save new information.
// ─────────────────────────────────────────────────────────────
public class StorageService
{
    // The file where all data is saved (in the same folder as the app)
    private readonly string _filePath = "subscriptions.json";

    // JSON settings: WriteIndented = true makes the file human-readable
    // (nicely formatted with indentation instead of one long line)
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // ── LOAD ──────────────────────────────────────────────────
    // Reads the JSON file and returns the app data.
    // If the file doesn't exist yet, returns empty data (first run).
    public AppData Load()
    {
        if (!File.Exists(_filePath))
            return new AppData();

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppData>(json, _options) ?? new AppData();
        }
        catch
        {
            return new AppData();
        }
    }

    public async Task<AppData> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return new AppData();

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<AppData>(json, _options) ?? new AppData();
        }
        catch
        {
            return new AppData();
        }
    }

    public void Save(AppData data)
    {
        var json = JsonSerializer.Serialize(data, _options);
        File.WriteAllText(_filePath, json);
    }

    public async Task SaveAsync(AppData data)
    {
        var json = JsonSerializer.Serialize(data, _options);
        await File.WriteAllTextAsync(_filePath, json);
    }
}
