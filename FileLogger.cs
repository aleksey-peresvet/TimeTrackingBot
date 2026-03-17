using System.Collections.Concurrent;

namespace TimeTrackerBot;

public class FileLogger : ILogger
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly string _categoryName;

    public FileLogger(string filePath, string categoryName)
    {
        _filePath = filePath;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public async void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {

        if (!IsEnabled(logLevel)) return;

        var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel,8}] [{_categoryName}] {formatter(state, exception)}";

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(_filePath, message + Environment.NewLine);
        }
        catch { }
    }
}

public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(_filePath, name));

    public void Dispose()
    {
        _loggers.Clear();
    }
}

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string filePath)
    {
        builder.AddProvider(new FileLoggerProvider(filePath));
        return builder;
    }
}