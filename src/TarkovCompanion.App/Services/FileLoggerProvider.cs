using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.App.Services;

/// <summary>
/// Writes application logs to the same file the crash handler uses.
/// </summary>
/// <remarks>
/// Until now the only sink was <see cref="System.Diagnostics.Trace"/>, which needs a debugger
/// attached, so the log file recorded lifecycle and crashes and nothing else. That left no way
/// to see which folder observation chose, which files it opened, or whether anything was being
/// read. Diagnosing a silent watcher then cost whole raids of guessing.
/// </remarks>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class FileLogger(string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var shortCategory = categoryName[(categoryName.LastIndexOf('.') + 1)..];
            CrashLog.Write(
                $"{logLevel.ToString().ToLowerInvariant()}/{shortCategory}",
                exception is null ? message : $"{message} :: {exception}");
        }
    }
}
