namespace UsageBar.Core;

/// <summary>
/// Statuses and counts only: never tokens, cookies, emails or organization names. The app points <see cref="Sink"/>
/// at its log file; tests leave it unset.
/// </summary>
public static class Log
{
    public static Action<string>? Sink { get; set; }

    public static void Info(string message) => Sink?.Invoke($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}");
}
