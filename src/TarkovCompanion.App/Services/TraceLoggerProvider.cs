using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TarkovCompanion.App.Services;

internal sealed class TraceLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TraceLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class TraceLogger(string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The factory's minimum level (TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL) decides what is written; a
        // floor here would make Debug and Trace unreachable without a rebuild.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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

            Trace.WriteLine($"[{logLevel}] {categoryName}: {formatter(state, exception)}");
            if (exception is not null)
            {
                Trace.WriteLine(exception);
            }
        }
    }
}
