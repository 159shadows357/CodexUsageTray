using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexUsageTray;

internal enum OpenAiServiceHealth
{
    Loading,
    Operational,
    UnderMaintenance,
    DegradedPerformance,
    PartialOutage,
    MajorOutage,
    Unavailable
}

internal sealed record OpenAiStatusSnapshot
{
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
    public bool IsLive { get; init; }
    public OpenAiServiceHealth Codex { get; init; } = OpenAiServiceHealth.Loading;
    public OpenAiServiceHealth ChatGpt { get; init; } = OpenAiServiceHealth.Loading;
    public string? Error { get; init; }

    public static OpenAiStatusSnapshot Unavailable(string error) => new()
    {
        CheckedAt = DateTimeOffset.Now,
        IsLive = false,
        Codex = OpenAiServiceHealth.Unavailable,
        ChatGpt = OpenAiServiceHealth.Unavailable,
        Error = error
    };
}

internal sealed class OpenAiStatusClient : IDisposable
{
    internal const string ComponentsUrl = "https://status.openai.com/api/v2/components.json";

    private static readonly ComponentDefinition[] CodexComponents =
    [
        new("01JVCV8YSWZFRSM1G5CVP253SK", "Codex Web"),
        new("01KMP3KP5M8X0EBTVW6KN327EE", "VS Code extension"),
        new("01KMP3KP5MGE23B80K1EK4S8PV", "Codex API"),
        new("01KMKFAMWKNQ84Z1766MV08ZDE", "CLI")
    ];

    private static readonly ComponentDefinition[] ChatGptComponents =
    [
        new("01KX45G1SHQQ9DTAX9S4W7FV8G", "Sites"),
        new("01KMKFAMWKQ81YWSE1Z18R6VHR", "Codex in ChatGPT Desktop"),
        new("01JMXBNJXG1S2D9V65P1ZZTD94", "Login"),
        new("01JMXBNJXGV1T5GT2M9XA83XNG", "Conversations"),
        new("01JMXBNJXGKKP51D4DEJ2HZJ8Q", "Search"),
        new("01M3Q6NERHVE2WPPAQRHBAC6XB", "dots"),
        new("01JSFK5QX36ZRW0TW0ZV0ZYFXQ", "GPTs"),
        new("01M3Q6NERHS4BG0HQ85VAR1SRH", "Space"),
        new("01JSYVYQSWMJ9QG35XHP08BHA7", "Deep Research"),
        new("01JQ7EKW990MSPSWVXC7VPV2ZJ", "Image Generation"),
        new("01JSG1XMJ9RVJJQ0E85NVSJ2AZ", "Agent"),
        new("01JMXBNJXGGT5SR5DB9J7GYY48", "Voice mode"),
        new("01K6TVGGGDCP0PPGCHXAG3AQX8", "Connectors/Apps"),
        new("01KX45G1SH21AX5DT93D4HMF0P", "ChatGPT Work"),
        new("01JMXBNJXG1YMQPPCPCQX3MPA2", "File uploads")
    ];

    private readonly HttpClient _client;

    public OpenAiStatusClient()
    {
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<OpenAiStatusSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using var request = CreateComponentsRequest();
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseComponents(document.RootElement, DateTimeOffset.Now);
    }

    internal static HttpRequestMessage CreateComponentsRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ComponentsUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    internal static OpenAiStatusSnapshot ParseComponents(JsonElement root, DateTimeOffset checkedAt)
    {
        if (!root.TryGetProperty("components", out var componentsElement) || componentsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("OpenAI status response does not contain a components array.");
        }

        var components = new List<ComponentStatus>();
        foreach (var component in componentsElement.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(component, "id");
            var name = ReadString(component, "name");
            var rawStatus = ReadString(component, "status");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rawStatus)) continue;
            components.Add(new ComponentStatus(id, name, ParseHealth(rawStatus)));
        }

        return new OpenAiStatusSnapshot
        {
            CheckedAt = checkedAt,
            IsLive = true,
            Codex = Aggregate(CodexComponents, components),
            ChatGpt = Aggregate(ChatGptComponents, components)
        };
    }

    internal static string DisplayText(OpenAiServiceHealth status) => status switch
    {
        OpenAiServiceHealth.Loading => "Checking...",
        OpenAiServiceHealth.Operational => "Operational",
        OpenAiServiceHealth.UnderMaintenance => "Maintenance",
        OpenAiServiceHealth.DegradedPerformance => "Degraded performance",
        OpenAiServiceHealth.PartialOutage => "Partial outage",
        OpenAiServiceHealth.MajorOutage => "Major outage",
        OpenAiServiceHealth.Unavailable => "Unavailable",
        _ => "Unavailable"
    };

    internal static bool IsIncident(OpenAiServiceHealth status) => status is
        OpenAiServiceHealth.UnderMaintenance or
        OpenAiServiceHealth.DegradedPerformance or
        OpenAiServiceHealth.PartialOutage or
        OpenAiServiceHealth.MajorOutage;

    private static OpenAiServiceHealth Aggregate(
        IReadOnlyList<ComponentDefinition> expected,
        IReadOnlyList<ComponentStatus> components)
    {
        var selected = new List<OpenAiServiceHealth>(expected.Count);
        var hasUnavailable = false;
        foreach (var definition in expected)
        {
            var match = components.FirstOrDefault(component =>
                string.Equals(component.Id, definition.Id, StringComparison.Ordinal));
            if (match is null)
            {
                var nameMatches = components.Where(component =>
                    string.Equals(component.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
                match = nameMatches.MaxBy(component => Severity(component.Health));
            }

            if (match is null || match.Health == OpenAiServiceHealth.Unavailable)
            {
                hasUnavailable = true;
                continue;
            }
            selected.Add(match.Health);
        }

        if (selected.Count == 0) return OpenAiServiceHealth.Unavailable;

        var worst = selected.MaxBy(Severity);
        if (worst is OpenAiServiceHealth.MajorOutage or OpenAiServiceHealth.PartialOutage or
            OpenAiServiceHealth.DegradedPerformance or OpenAiServiceHealth.UnderMaintenance)
        {
            return worst;
        }
        return hasUnavailable || selected.Count != expected.Count
            ? OpenAiServiceHealth.Unavailable
            : OpenAiServiceHealth.Operational;
    }

    private static OpenAiServiceHealth ParseHealth(string status) => status switch
    {
        "operational" => OpenAiServiceHealth.Operational,
        "under_maintenance" => OpenAiServiceHealth.UnderMaintenance,
        "degraded_performance" => OpenAiServiceHealth.DegradedPerformance,
        "partial_outage" => OpenAiServiceHealth.PartialOutage,
        "major_outage" => OpenAiServiceHealth.MajorOutage,
        _ => OpenAiServiceHealth.Unavailable
    };

    private static int Severity(OpenAiServiceHealth status) => status switch
    {
        OpenAiServiceHealth.Operational => 0,
        OpenAiServiceHealth.UnderMaintenance => 1,
        OpenAiServiceHealth.DegradedPerformance => 2,
        OpenAiServiceHealth.PartialOutage => 3,
        OpenAiServiceHealth.MajorOutage => 4,
        _ => -1
    };

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    public void Dispose() => _client.Dispose();

    private sealed record ComponentDefinition(string Id, string Name);
    private sealed record ComponentStatus(string Id, string Name, OpenAiServiceHealth Health);
}
