using Microsoft.Extensions.Logging;

namespace SagaPerception.TestGen.NUnit;

/// <summary>En fångad loggpost.</summary>
internal sealed record CapturedLog(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>Samlar loggposter från appen i minnet, som logger-provider. Fångar bara loggar från den app providern är registrerad i.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<CapturedLog> _logs = [];

    /// <summary>Loggposter i den ordning de skrevs. Returnerar en kopia.</summary>
    public IReadOnlyList<CapturedLog> Logs
    {
        get { lock (_gate) return _logs.ToArray(); }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose() { }

    private void Add(CapturedLog log)
    {
        lock (_gate)
            _logs.Add(log);
    }

    private sealed class CaptureLogger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Add(new CapturedLog(category, logLevel, formatter(state, exception), exception));
    }
}
