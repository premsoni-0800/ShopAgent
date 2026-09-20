using System.Text;
using Microsoft.Extensions.Logging;

namespace PrintlyAgent.Core;

/// <summary>
/// Writes the agent's log to a file, one per day, kept for a fortnight.
///
/// The app ships as a WinExe, which has no console - so without this its logs go
/// nowhere at all. That is only tolerable while someone is watching it from a
/// terminal, which is never true on a shop counter: the one moment the log
/// matters is the one where nobody was looking, and "it stopped printing
/// yesterday afternoon" has to be answerable from something left on disk.
///
/// Deliberately small and dependency-free. A logging framework would do more,
/// but every line here is one that has to keep working on a counter PC with no
/// one to fix it, and the failure mode of a log writer must never be worse than
/// the failure it was going to record - hence a write that cannot throw out.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _keepDays;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _openFor;

    public FileLoggerProvider(string directory, int keepDays = 14)
    {
        _directory = directory;
        _keepDays = keepDays;
        Directory.CreateDirectory(directory);
        SweepOldLogs();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>
    /// Appends one line, flushing immediately.
    ///
    /// Buffered, the last few lines before a crash are exactly the ones lost -
    /// and those are the only ones anybody ever wants. The cost is a flush per
    /// line on a log that writes a handful of lines a minute.
    /// </summary>
    internal void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_writer is null || _openFor != today)
                {
                    _writer?.Dispose();
                    _openFor = today;
                    var path = Path.Combine(_directory, $"agent-{today:yyyy-MM-dd}.log");
                    _writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                        Encoding.UTF8);
                    SweepOldLogs();
                }
                _writer.WriteLine(line);
                _writer.Flush();
            }
        }
        catch (Exception)
        {
            // A log that cannot be written must not take the agent down with it.
            // Losing a line is bad; refusing to print somebody's order because
            // the disk is full and the logger threw is worse.
        }
    }

    private void SweepOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_keepDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "agent-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch (Exception) { /* housekeeping is never worth an exception */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            // The last segment only: "PrintlyAgent.Jobs.PrintQueue" is the same
            // information as "PrintQueue" on every line of a single-app log, and
            // the prefix costs a third of the line width.
            _category = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(' ').Append(Level(logLevel))
                .Append(' ').Append(_category)
                .Append(' ').Append(formatter(state, exception));

            // The stack trace matters more than the message for anything that
            // reaches here as an exception, so it is never truncated away.
            if (exception is not null) line.Append('\n').Append(exception);

            _provider.Write(line.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => "     ",
        };
    }
}
