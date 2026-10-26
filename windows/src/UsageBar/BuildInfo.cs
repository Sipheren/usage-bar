using System.Globalization;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace UsageBar;

/// <summary>Version, build and commit for About, from the assembly attributes the build stamps.</summary>
internal static class BuildInfo
{
    static readonly Assembly Asm = Assembly.GetExecutingAssembly();

    static string? Meta(string key) =>
        Asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;

    public static string Version => Asm.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>"Version 0.1.0 (build 7 · 9196348)".</summary>
    public static string VersionLine
    {
        get
        {
            var parts = new[] { Meta("BuildNumber") is { } b ? "build " + b : null, Meta("Commit") }.Where(p => p is not null).ToList();
            return $"Version {Version}" + (parts.Count > 0 ? $" ({string.Join(" · ", parts)})" : "");
        }
    }

    /// <summary>"26 Oct 2026 08:12", from the executable's timestamp.</summary>
    public static string Built
    {
        get
        {
            var path = Environment.ProcessPath ?? Asm.Location;
            return File.Exists(path) ? File.GetLastWriteTime(path).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) : "";
        }
    }

    /// <summary>The repository this build came from, when it runs from inside one (a development build).</summary>
    public static string? ProjectFolder
    {
        get
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath ?? Asm.Location);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir, "windows", "VERSION")) && Directory.Exists(Path.Combine(dir, "macos"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}

/// <summary>Launch at login through the per-user Run key. Off unless the user turns it on.</summary>
internal static class StartupRegistry
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "UsageBar";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            try { return Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(Name) is string v && v == Command; }
            catch (System.Security.SecurityException) { return false; }
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(Name, Command);
            else key.DeleteValue(Name, false);
            Core.Log.Info("launch at login: " + (on ? "on" : "off"));
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Core.Log.Info("launch at login: " + e.GetType().Name);
        }
    }
}
