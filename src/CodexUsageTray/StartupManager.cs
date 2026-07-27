using Microsoft.Win32;

namespace CodexUsageTray;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexUsageTray";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
        return File.Exists(executablePath)
            && key?.GetValue(ValueName) is string value
            && string.Equals(value, BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(Environment.ProcessPath ?? Application.ExecutablePath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    public static string BuildCommand(string executablePath) => $"\"{executablePath}\" --autostart";
}
