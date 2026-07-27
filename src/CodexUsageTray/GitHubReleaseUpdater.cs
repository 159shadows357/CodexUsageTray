using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexUsageTray;

internal enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    FeedUnavailable
}

internal sealed record GitHubReleaseUpdate(
    Version Version,
    string TagName,
    Uri ReleasePage,
    Uri AssetApiUrl,
    string AssetName,
    string AssetDigest,
    long AssetSize);

internal sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    GitHubReleaseUpdate? Release,
    string Message);

internal sealed record PreparedUpdate(
    Version Version,
    string Directory,
    string SetupExecutable);

internal sealed partial class GitHubReleaseUpdater : IDisposable
{
    internal const string Repository = "159shadows357/CodexUsageTray";
    private const long MaximumDownloadBytes = 200L * 1024 * 1024;
    private const long MaximumExtractedBytes = 300L * 1024 * 1024;
    private readonly string _updatesRoot;
    private readonly HttpClient _apiClient;
    private readonly HttpClient _downloadClient;

    public GitHubReleaseUpdater(string updatesRoot)
    {
        _updatesRoot = Path.GetFullPath(updatesRoot);
        Directory.CreateDirectory(_updatesRoot);
        CleanupOldStagingDirectories();
        _apiClient = CreateClient(new HttpClientHandler { AllowAutoRedirect = false });
        _downloadClient = CreateClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    public static Version CurrentVersion =>
        typeof(GitHubReleaseUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = CreateGitHubRequest(
            HttpMethod.Get,
            new Uri($"https://api.github.com/repos/{Repository}/releases/latest"),
            "application/vnd.github+json");
        using var response = await _apiClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.FeedUnavailable,
                null,
                "The public GitHub release feed is not available.");
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var rateLimited = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
                              values.Contains("0", StringComparer.Ordinal);
            return new UpdateCheckResult(
                UpdateCheckStatus.FeedUnavailable,
                null,
                rateLimited
                    ? "GitHub's anonymous API rate limit was reached. Try again later."
                    : "GitHub did not allow access to the public release feed.");
        }

        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        return ParseLatestRelease(document.RootElement, CurrentVersion);
    }

    public async Task<PreparedUpdate> DownloadAndPrepareAsync(
        GitHubReleaseUpdate release,
        CancellationToken cancellationToken)
    {
        var stagingDirectory = GetSafeStagingDirectory(release.Version);
        SafeDeleteDirectory(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);
        var zipPath = Path.Combine(stagingDirectory, release.AssetName);
        var extractDirectory = Path.Combine(stagingDirectory, "package");

        try
        {
            using var request = CreateGitHubRequest(
                HttpMethod.Get,
                release.AssetApiUrl,
                "application/octet-stream");
            using var response = await _apiClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                throw new IOException("GitHub could not download the public release asset.");
            }

            if (IsRedirect(response.StatusCode))
            {
                var redirect = response.Headers.Location
                    ?? throw new InvalidDataException("GitHub release asset redirect did not include a location.");
                if (!redirect.IsAbsoluteUri) redirect = new Uri(release.AssetApiUrl, redirect);
                if (!IsTrustedDownloadUri(redirect))
                {
                    throw new InvalidDataException("GitHub release asset redirected to an unexpected host.");
                }

                await DownloadFollowingRedirectsAsync(redirect, zipPath, cancellationToken);
            }
            else
            {
                response.EnsureSuccessStatusCode();
                await WriteResponseToFileAsync(response, zipPath, cancellationToken);
            }

            if (new FileInfo(zipPath).Length != release.AssetSize)
            {
                throw new InvalidDataException("Update package size did not match GitHub release metadata.");
            }
            VerifyFileDigest(zipPath, release.AssetDigest);
            var setup = VerifyAndExtractPackage(zipPath, extractDirectory);
            return new PreparedUpdate(release.Version, extractDirectory, setup);
        }
        catch
        {
            SafeDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    public static void StartInstaller(PreparedUpdate update, bool preserveStartupEnabled)
    {
        var startInfo = new ProcessStartInfo(update.SetupExecutable)
        {
            UseShellExecute = true,
            WorkingDirectory = update.Directory
        };
        startInfo.ArgumentList.Add("--silent");
        if (!preserveStartupEnabled)
        {
            startInfo.ArgumentList.Add("--no-startup");
        }

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("Could not start the Codex Usage Tray installer.");
        }
    }

    internal static UpdateCheckResult ParseLatestRelease(JsonElement root, Version currentVersion)
    {
        var tagName = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(tagName) ||
            !Version.TryParse(tagName.Trim().TrimStart('v', 'V'), out var releaseVersion))
        {
            throw new InvalidDataException("GitHub latest release has an invalid version tag.");
        }

        if (releaseVersion <= currentVersion)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.UpToDate,
                null,
                $"Codex Usage Tray {FormatVersion(currentVersion)} is up to date.");
        }

        var expectedAsset = $"CodexUsageTray-{FormatVersion(releaseVersion)}-win-x64.zip";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub latest release does not contain release assets.");
        }

        JsonElement? matchingAsset = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) &&
                string.Equals(name.GetString(), expectedAsset, StringComparison.Ordinal))
            {
                matchingAsset = asset;
                break;
            }
        }

        if (matchingAsset is null)
        {
            throw new InvalidDataException($"GitHub latest release does not contain {expectedAsset}.");
        }

        var assetValue = matchingAsset.Value;
        var assetUrlText = assetValue.TryGetProperty("url", out var assetUrlElement)
            ? assetUrlElement.GetString()
            : null;
        var digest = assetValue.TryGetProperty("digest", out var digestElement)
            ? digestElement.GetString()
            : null;
        var assetSize = assetValue.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var size)
            ? size
            : 0;
        var releasePageText = root.TryGetProperty("html_url", out var releasePageElement)
            ? releasePageElement.GetString()
            : null;
        if (!Uri.TryCreate(assetUrlText, UriKind.Absolute, out var assetUrl) ||
            !IsGitHubApiUri(assetUrl) ||
            !Uri.TryCreate(releasePageText, UriKind.Absolute, out var releasePage) ||
            !IsValidSha256Digest(digest) ||
            assetSize <= 0 ||
            assetSize > MaximumDownloadBytes)
        {
            throw new InvalidDataException("GitHub latest release asset metadata is incomplete or unsafe.");
        }

        var release = new GitHubReleaseUpdate(
            releaseVersion,
            tagName,
            releasePage,
            assetUrl,
            expectedAsset,
            digest!,
            assetSize);
        return new UpdateCheckResult(
            UpdateCheckStatus.UpdateAvailable,
            release,
            $"Codex Usage Tray {FormatVersion(releaseVersion)} is available.");
    }

    internal static string VerifyAndExtractPackage(string zipPath, string extractDirectory)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("Update package was not found.", zipPath);
        if (Directory.Exists(extractDirectory)) Directory.Delete(extractDirectory, true);
        Directory.CreateDirectory(extractDirectory);
        var root = Path.GetFullPath(extractDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long extractedBytes = 0;

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                extractedBytes = checked(extractedBytes + entry.Length);
                if (extractedBytes > MaximumExtractedBytes)
                {
                    throw new InvalidDataException("Update package expands beyond the allowed size.");
                }

                var destination = Path.GetFullPath(Path.Combine(
                    extractDirectory,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Update package contains an unsafe path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
        }

        var setup = Path.Combine(extractDirectory, "CodexUsageTray-Setup.exe");
        var application = Path.Combine(extractDirectory, "CodexUsageTray.exe");
        var sums = Path.Combine(extractDirectory, "SHA256SUMS.txt");
        if (!File.Exists(setup) || !File.Exists(application) || !File.Exists(sums))
        {
            throw new InvalidDataException("Update package is missing setup, application, or SHA256SUMS.txt.");
        }

        var expected = ParsePackageHashes(sums);
        VerifyPackageFile(setup, expected);
        VerifyPackageFile(application, expected);
        return setup;
    }

    internal static void VerifyFileDigest(string path, string digest)
    {
        if (!IsValidSha256Digest(digest))
        {
            throw new InvalidDataException("Update package SHA-256 digest is missing or invalid.");
        }

        using var stream = File.OpenRead(path);
        var actual = SHA256.HashData(stream);
        var expected = Convert.FromHexString(digest["sha256:".Length..]);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException("Update package SHA-256 digest did not match GitHub release metadata.");
        }
    }

    private static Dictionary<string, string> ParsePackageHashes(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var match = PackageHashLineRegex().Match(line);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            if (Path.GetFileName(name) != name) throw new InvalidDataException("SHA256SUMS.txt contains an unsafe path.");
            result[name] = match.Groups["hash"].Value;
        }
        return result;
    }

    private static void VerifyPackageFile(string path, IReadOnlyDictionary<string, string> expected)
    {
        var name = Path.GetFileName(path);
        if (!expected.TryGetValue(name, out var expectedHash))
        {
            throw new InvalidDataException($"SHA256SUMS.txt does not contain {name}.");
        }

        using var stream = File.OpenRead(path);
        var actual = SHA256.HashData(stream);
        var expectedBytes = Convert.FromHexString(expectedHash);
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedBytes))
        {
            throw new InvalidDataException($"{name} did not match SHA256SUMS.txt.");
        }
    }

    private async Task WriteResponseToFileAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
        {
            throw new InvalidDataException("Update package exceeds the allowed download size.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > MaximumDownloadBytes)
            {
                throw new InvalidDataException("Update package exceeds the allowed download size.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (total == 0)
        {
            throw new InvalidDataException("GitHub returned an empty update package.");
        }
    }

    private async Task DownloadFollowingRedirectsAsync(
        Uri initialUri,
        string path,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            if (!IsTrustedDownloadUri(currentUri))
            {
                throw new InvalidDataException("GitHub release asset redirected to an unexpected host.");
            }

            using var response = await _downloadClient.GetAsync(
                currentUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                response.EnsureSuccessStatusCode();
                await WriteResponseToFileAsync(response, path, cancellationToken);
                return;
            }

            var location = response.Headers.Location
                ?? throw new InvalidDataException("GitHub release asset redirect did not include a location.");
            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        }

        throw new InvalidDataException("GitHub release asset exceeded the allowed redirect count.");
    }

    private string GetSafeStagingDirectory(Version version)
    {
        var directory = Path.GetFullPath(Path.Combine(_updatesRoot, FormatVersion(version)));
        var prefix = _updatesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to stage an update outside the update directory.");
        }
        return directory;
    }

    private void SafeDeleteDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = _updatesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a path outside the update directory.");
        }
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
    }

    private void CleanupOldStagingDirectories()
    {
        var currentDirectory = GetSafeStagingDirectory(CurrentVersion);
        foreach (var directory in Directory.EnumerateDirectories(_updatesRoot))
        {
            var fullPath = Path.GetFullPath(directory);
            if (string.Equals(fullPath, currentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                SafeDeleteDirectory(fullPath);
            }
            catch (IOException)
            {
                // A running Setup can temporarily hold files open. Retry on the next start.
            }
            catch (UnauthorizedAccessException)
            {
                // Leave inaccessible staging data untouched and keep the tray application running.
            }
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CodexUsageTray/{FormatVersion(CurrentVersion)}");
        return client;
    }

    internal static HttpRequestMessage CreateGitHubRequest(
        HttpMethod method,
        Uri uri,
        string accept)
    {
        if (!IsGitHubApiUri(uri)) throw new InvalidOperationException("GitHub API requests must use api.github.com.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => (int)statusCode is 301 or 302 or 303 or 307 or 308;
    private static bool IsGitHubApiUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);
    private static bool IsTrustedDownloadUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    private static bool IsValidSha256Digest(string? digest) =>
        digest is not null && Sha256DigestRegex().IsMatch(digest);
    private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    public void Dispose()
    {
        _apiClient.Dispose();
        _downloadClient.Dispose();
    }

    [GeneratedRegex("^sha256:[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256DigestRegex();

    [GeneratedRegex("^(?<hash>[A-Fa-f0-9]{64})\\s{2}(?<name>[^\\r\\n]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageHashLineRegex();
}
