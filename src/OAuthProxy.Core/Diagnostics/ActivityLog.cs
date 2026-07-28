using System.Text;

namespace OAuthProxy.Core.Diagnostics;

/// <summary>
/// Append-only activity log with automatic 2-day rotation, plus an in-memory ring buffer so
/// the UI can show recent lines without re-reading the file. Thread-safe: proxied requests
/// are logged from thread-pool threads while the UI reads on the dispatcher thread.
/// </summary>
public sealed class ActivityLog
{
    private const int RotationDays = 2;
    private const int RetainedPeriods = 5;   // ~10 days of history
    private const int BufferSize = 300;

    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly string _logDirectory;

    public ActivityLog(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OAuthProxy",
            "logs");
        Directory.CreateDirectory(_logDirectory);
    }

    public string LogDirectory => _logDirectory;

    /// <summary>Error log lives alongside the activity log so one folder holds everything.</summary>
    public string ErrorLogPath => Path.Combine(_logDirectory, "errors.log");

    /// <summary>
    /// Current activity file. Named by 2-day bucket, so it rolls over on its own with no
    /// rename/lock dance — the name simply changes when the bucket does.
    /// </summary>
    public string CurrentLogPath => Path.Combine(_logDirectory, $"activity-{CurrentPeriodStart():yyyyMMdd}.log");

    private static DateTime CurrentPeriodStart()
    {
        var today = DateTime.UtcNow.Date;
        var daysSinceEpoch = (int)(today - DateTime.UnixEpoch.Date).TotalDays;
        return today.AddDays(-(daysSinceEpoch % RotationDays));
    }

    public void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";

        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > BufferSize) _recent.Dequeue();

            try
            {
                File.AppendAllText(CurrentLogPath, line + Environment.NewLine, Encoding.UTF8);
                PruneExpired();
            }
            catch
            {
                // Logging must never break the thing being logged.
            }
        }
    }

    public void LogError(string message, Exception ex)
    {
        Log($"ERROR {message}: {ex.Message}");
        lock (_gate)
        {
            try
            {
                File.AppendAllText(ErrorLogPath,
                    $"{DateTime.Now:O} {message}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>Most recent lines, oldest first.</summary>
    public IReadOnlyList<string> GetRecent(int count)
    {
        lock (_gate)
        {
            return _recent.Reverse().Take(count).Reverse().ToList();
        }
    }

    /// <summary>Deletes every rotated activity log except the current one, plus the error log.</summary>
    public int PruneAll()
    {
        lock (_gate)
        {
            var deleted = 0;
            var current = CurrentLogPath;

            foreach (var file in Directory.EnumerateFiles(_logDirectory, "activity-*.log"))
            {
                if (string.Equals(file, current, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryDelete(file)) deleted++;
            }

            if (File.Exists(ErrorLogPath) && TryDelete(ErrorLogPath)) deleted++;
            return deleted;
        }
    }

    /// <summary>Drops rotated files older than the retention window. Called on every write.</summary>
    private void PruneExpired()
    {
        var cutoff = CurrentPeriodStart().AddDays(-RotationDays * RetainedPeriods);

        foreach (var file in Directory.EnumerateFiles(_logDirectory, "activity-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file).Replace("activity-", "");
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var periodStart)
                && periodStart < cutoff)
            {
                TryDelete(file);
            }
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
