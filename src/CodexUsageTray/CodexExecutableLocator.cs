using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexUsageTray;

public sealed class CodexExecutableLocator(AppLogger logger)
{
    public async Task<string> FindAsync(CancellationToken cancellationToken)
    {
        foreach (var candidate in EnumerateCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await CanRunAsync(candidate, cancellationToken))
            {
                logger.Info($"Using Codex CLI: {candidate}");
                return candidate;
            }
        }

        throw new CodexCliNotFoundException("Codex CLI was not found or could not be executed.");
    }

    public static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments)
    {
        var isCommandFile = string.Equals(Path.GetExtension(executable), ".cmd", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetExtension(executable), ".bat", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo
        {
            FileName = isCommandFile ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (isCommandFile)
        {
            var command = $"\"{executable}\"";
            foreach (var argument in arguments)
            {
                command += $" \"{argument.Replace("\"", "\"\"")}\"";
            }
            info.Arguments = $"/d /s /c \"{command}\"";
        }
        else
        {
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
        }

        return info;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            yield return explicitPath;
        }

        var configuredPath = ReadConfiguredCliPath();
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            yield return configuredPath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var candidate in SafeEnumerate(Path.Combine(localAppData, "OpenAI", "Codex", "bin"), "codex.exe"))
        {
            yield return candidate;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in new[] { "codex.exe", "codex.cmd", "codex.bat" })
            {
                string candidate;
                try { candidate = Path.Combine(directory.Trim('"'), name); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate))
                {
                    if (candidate.Contains("\\Program Files\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    yield return candidate;
                }
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in SafeEnumerate(Path.Combine(userProfile, ".vscode", "extensions"), "codex.exe")
                     .Where(path => path.Contains("openai.chatgpt-", StringComparison.OrdinalIgnoreCase)))
        {
            yield return candidate;
        }
    }

    private static string? ReadConfiguredCliPath()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        try
        {
            foreach (var line in File.ReadLines(config))
            {
                var match = Regex.Match(line, "^\\s*CODEX_CLI_PATH\\s*=\\s*['\\\"](?<path>[^'\\\"]+)['\\\"]\\s*(?:#.*)?$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups["path"].Value;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static IEnumerable<string> SafeEnumerate(string root, string fileName)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
                : [];
        }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }
    }

    private static async Task<bool> CanRunAsync(string candidate, CancellationToken cancellationToken)
    {
        Process? process = null;
        var started = false;
        try
        {
            process = new Process { StartInfo = CreateStartInfo(candidate, ["--version"]) };
            if (!process.Start())
            {
                return false;
            }
            started = true;

            process.StandardInput.Close();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            return process.ExitCode == 0 && output.Contains("codex-cli", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (process is not null)
            {
                if (started && !process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
                    }
                    catch { }
                }
                process.Dispose();
            }
        }
    }
}
