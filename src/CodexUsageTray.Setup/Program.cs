using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace CodexUsageTray.Setup;

internal static partial class Program
{
    private const string ProductName = "Codex Usage Tray";
    private const string ExecutableName = "CodexUsageTray.exe";
    private const string StartupValueName = "CodexUsageTray";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const uint MbIconInformation = 0x00000040;
    private const uint MbIconError = 0x00000010;

    [STAThread]
    private static int Main(string[] args)
    {
        var silent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
        try
        {
            if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            {
                Uninstall();
                ShowMessageUnlessSilent(silent, "Uninstall completed. Settings and logs were preserved.", MbIconInformation);
                return 0;
            }

            var startWithWindows = !args.Contains("--no-startup", StringComparer.OrdinalIgnoreCase);
            Install(startWithWindows);
            var startupText = startWithWindows ? "Start with Windows is enabled." : "Start with Windows is disabled.";
            ShowMessageUnlessSilent(silent, $"Installation completed.\n\n{startupText}", MbIconInformation);
            return 0;
        }
        catch (Exception error)
        {
            ShowMessageUnlessSilent(silent, $"Installation failed.\n\n{error.Message}", MbIconError);
            return 1;
        }
    }

    private static void Install(bool startWithWindows)
    {
        var payload = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        if (!File.Exists(payload))
        {
            throw new FileNotFoundException($"Keep {ExecutableName} in the same folder as the setup file.", payload);
        }

        var installDirectory = GetInstallDirectory();
        var installedExecutable = Path.Combine(installDirectory, ExecutableName);
        StopInstalledApp(installedExecutable);
        Directory.CreateDirectory(installDirectory);

        if (!PathsEqual(payload, installedExecutable))
        {
            ReplaceExecutable(payload, installedExecutable);
        }

        SetStartup(installedExecutable, startWithWindows);
        Process.Start(new ProcessStartInfo(installedExecutable)
        {
            UseShellExecute = true,
            WorkingDirectory = installDirectory
        });
    }

    private static void Uninstall()
    {
        var installDirectory = GetInstallDirectory();
        var installedExecutable = Path.Combine(installDirectory, ExecutableName);
        StopInstalledApp(installedExecutable);
        SetStartup(installedExecutable, false);

        if (File.Exists(installedExecutable)) File.Delete(installedExecutable);
        var temporaryExecutable = installedExecutable + ".new";
        if (File.Exists(temporaryExecutable)) File.Delete(temporaryExecutable);
        if (Directory.Exists(installDirectory) && !Directory.EnumerateFileSystemEntries(installDirectory).Any())
        {
            Directory.Delete(installDirectory);
        }
    }

    private static void StopInstalledApp(string installedExecutable)
    {
        if (!File.Exists(installedExecutable)) return;

        try
        {
            using var request = Process.Start(new ProcessStartInfo(installedExecutable, "--exit")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(installedExecutable)!
            });
            request?.WaitForExit(5000);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process check below gives the actionable result.
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (IsInstalledAppRunning(installedExecutable) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }
        if (IsInstalledAppRunning(installedExecutable))
        {
            throw new IOException("Codex Usage Tray did not exit within 10 seconds. Exit it from the tray menu and run setup again.");
        }
    }

    private static bool IsInstalledAppRunning(string installedExecutable)
    {
        foreach (var process in Process.GetProcessesByName("CodexUsageTray"))
        {
            using (process)
            {
                try
                {
                    if (PathsEqual(process.MainModule?.FileName, installedExecutable)) return true;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Ignore processes whose executable path cannot be inspected.
                }
            }
        }
        return false;
    }

    private static void SetStartup(string installedExecutable, bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");
        if (enabled)
        {
            runKey.SetValue(StartupValueName, $"\"{installedExecutable}\" --autostart", RegistryValueKind.String);
        }
        else
        {
            runKey.DeleteValue(StartupValueName, false);
        }
    }

    private static void VerifyCopy(string source, string destination)
    {
        using var sourceStream = File.OpenRead(source);
        using var destinationStream = File.OpenRead(destination);
        var sourceHash = SHA256.HashData(sourceStream);
        var destinationHash = SHA256.HashData(destinationStream);
        if (!sourceHash.AsSpan().SequenceEqual(destinationHash))
        {
            throw new IOException("The installed executable did not match the packaged executable.");
        }
    }

    private static void ReplaceExecutable(string source, string destination)
    {
        var temporaryExecutable = destination + ".new";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                File.Copy(source, temporaryExecutable, true);
                File.Move(temporaryExecutable, destination, true);
                VerifyCopy(source, destination);
                return;
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException &&
                DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static string GetInstallDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "CodexUsageTray");

    private static bool PathsEqual(string? left, string right) => left is not null && Path.GetFullPath(left).Equals(
        Path.GetFullPath(right),
        StringComparison.OrdinalIgnoreCase);

    private static void ShowMessageUnlessSilent(bool silent, string message, uint icon)
    {
        if (!silent) MessageBox(0, message, ProductName, icon);
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint windowHandle, string text, string caption, uint type);
}
