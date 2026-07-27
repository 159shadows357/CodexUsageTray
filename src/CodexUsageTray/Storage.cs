using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageTray;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageTray");
        Logs = Path.Combine(Root, "logs");
        Settings = Path.Combine(Root, "settings.json");
        Cache = Path.Combine(Root, "usage-cache.json");
        LocalUsageIndex = Path.Combine(Root, "local-usage-index.json");
        NotificationState = Path.Combine(Root, "notification-state.json");
        Updates = Path.Combine(Root, "updates");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Updates);
    }

    public string Root { get; }
    public string Logs { get; }
    public string Settings { get; }
    public string Cache { get; }
    public string LocalUsageIndex { get; }
    public string NotificationState { get; }
    public string Updates { get; }
}

public sealed class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettings ReadSettings(string path)
    {
        var settings = Read<AppSettings>(path) ?? new AppSettings();
        return settings with
        {
            RefreshIntervalMinutes = Math.Clamp(settings.RefreshIntervalMinutes, 1, 60),
            StaleAfterMinutes = Math.Clamp(settings.StaleAfterMinutes, 2, 120)
        };
    }

    public UsageSnapshot? ReadSnapshot(string path) => Read<UsageSnapshot>(path);

    internal T? ReadValue<T>(string path) => Read<T>(path);

    public void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }

    private static T? Read<T>(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
    }
}

public sealed class AppLogger
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public AppLogger(AppPaths paths)
    {
        _paths = paths;
        DeleteOldLogs();
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {SecretSanitizer.Sanitize(message)}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(Path.Combine(_paths.Logs, $"codex-usage-tray-{DateTime.Now:yyyyMMdd}.log"), line); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void DeleteOldLogs()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_paths.Logs, "*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
