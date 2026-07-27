using System.Text.RegularExpressions;

namespace CodexUsageTray;

public static partial class SecretSanitizer
{
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = AuthorizationRegex().Replace(text, "$1[REDACTED]");
        result = BearerRegex().Replace(result, "$1[REDACTED]");
        result = JsonSecretRegex().Replace(result, "$1[REDACTED]");
        result = ApiKeyRegex().Replace(result, "[REDACTED]");
        result = GitHubTokenRegex().Replace(result, "[REDACTED]");
        return result.Length > 2_000 ? result[..2_000] + "..." : result;
    }

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*bearer\\s+|bearer\\s+)[^\\s,;]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?im)([\\\"]?authorization[\\\"]?\\s*[:=]\\s*)(?:\\\"[^\\\"\\r\\n]*\\\"|'[^'\\r\\n]*'|[^\\r\\n,;]+)")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex("(?i)([\\\"]?(?:access[_-]?token|refresh[_-]?token|api[_-]?key|credential|password)[\\\"]?\\s*[:=]\\s*)(\\\"[^\\\"\\r\\n]*\\\"|'[^'\\r\\n]*'|[^\\s,;}]+)")]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex("(?i)\\bsk-[A-Za-z0-9_-]{12,}\\b")]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex("(?i)\\b(?:github_pat_|gh[opurs]_)[A-Za-z0-9_]{16,}\\b")]
    private static partial Regex GitHubTokenRegex();
}
