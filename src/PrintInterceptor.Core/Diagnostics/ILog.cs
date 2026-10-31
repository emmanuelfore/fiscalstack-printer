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
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly bool _echoToConsole;

    public FileLog(string directory, bool echoToConsole)
    {
        _directory = directory;
        _echoToConsole = echoToConsole;
        Directory.CreateDirectory(directory);
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
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";

        lock (_gate)
        {
            try
            {
                var path = Path.Combine(_directory, $"printinterceptor-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
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

    public void Dispose() { }
}
