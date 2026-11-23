using System.Diagnostics;
using System.Security.Principal;

namespace PrintInterceptor.Core.Diagnostics;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}

public sealed class FileLog : ILog, IDisposable
{
    private const string FilePrefix = "printinterceptor-";
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private const int RetainedFiles = 14;
    private const int RetainedDays = 14;

    private static readonly object AclGate = new();
    private static readonly HashSet<string> SecuredDirectories = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly bool _echoToConsole;
    private string? _currentPath;
    private DateTime _currentDate;

    public FileLog(string directory, bool echoToConsole)
    {
        _directory = directory;
        _echoToConsole = echoToConsole;
        Directory.CreateDirectory(directory);
        SecureDirectory(directory);
        Prune();
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null)
    {
        var text = exception is null
            ? message
            : $"{message} :: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";
        Write("ERROR", text);
    }

    private void Write(string level, string message)
    {
        var now = DateTimeOffset.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";

        lock (_gate)
        {
            try
            {
                File.AppendAllText(ResolvePath(now), line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the service down.
            }

            if (_echoToConsole)
            {
                Console.WriteLine(line);
            }
        }
    }

    /// <summary>
    /// Daily file until it reaches <see cref="MaxFileBytes"/>, then a timestamped
    /// sibling. Old files are pruned on rollover and at startup, not per write.
    /// </summary>
    private string ResolvePath(DateTimeOffset now)
    {
        var dailyPath = Path.Combine(_directory, $"{FilePrefix}{now:yyyyMMdd}.log");
        var path = _currentPath;

        if (path is null || _currentDate != now.Date)
        {
            path = dailyPath;
            _currentPath = path;
            _currentDate = now.Date;
            Prune();
        }

        if (IsFull(path))
        {
            path = RolloverPath(now);
            _currentPath = path;
            Prune();
        }

        return path;
    }

    private string RolloverPath(DateTimeOffset now)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var suffix = attempt == 0
                ? $"{now:yyyyMMdd-HHmmss}"
                : $"{now:yyyyMMdd-HHmmss}-{attempt}";
            var candidate = Path.Combine(_directory, $"{FilePrefix}{suffix}.log");
            if (!IsFull(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(_directory, $"{FilePrefix}{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
    }

    private static bool IsFull(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes;
        }
        catch
        {
            return false;
        }
    }

    private void Prune()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetainedDays);
            var files = Directory.GetFiles(_directory, FilePrefix + "*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            foreach (var file in files.Skip(RetainedFiles))
            {
                TryDelete(file.FullName);
            }

            foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff))
            {
                TryDelete(file.FullName);
            }
        }
        catch
        {
            // A failed prune must never stop logging.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Locked file: the next startup will retry.
        }
    }

    /// <summary>
    /// Best-effort Windows ACL: Administrators, SYSTEM and the account the process
    /// runs under (the tray user or the service account), nothing else.
    /// </summary>
    private static void SecureDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (AclGate)
        {
            string root;
            try
            {
                root = Path.GetFullPath(directory);
            }
            catch
            {
                return;
            }

            if (!SecuredDirectories.Add(root))
            {
                return;
            }

            try
            {
                var start = new ProcessStartInfo("icacls.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add(root);
                start.ArgumentList.Add("/inheritance:r");
                start.ArgumentList.Add("/grant:r");
                start.ArgumentList.Add("*S-1-5-32-544:(OI)(CI)F"); // Administrators
                start.ArgumentList.Add("*S-1-5-18:(OI)(CI)F");      // SYSTEM

                var user = WindowsIdentity.GetCurrent().Name;
                if (!string.IsNullOrWhiteSpace(user))
                {
                    start.ArgumentList.Add($"{user}:(OI)(CI)F");
                }

                start.ArgumentList.Add("/T");

                using var process = Process.Start(start);
                process?.WaitForExit();
            }
            catch
            {
                // ACLs are defence in depth; logging still works without them.
            }
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
