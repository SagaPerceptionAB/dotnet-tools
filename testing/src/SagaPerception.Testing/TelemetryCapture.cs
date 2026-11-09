using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SagaPerception.Testing;

/// <summary>En fångad loggpost.</summary>
public sealed record CapturedLog(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Samlar färdiga Activity-objekt (spår) och loggposter i minnet.
/// <para>
/// OBS: en ActivityListener är processglobal. Alla Activity-objekt som avslutas i processen medan fångsten lever
/// samlas, även från andra appar och tester som körs parallellt. Filtrera på källnamn (konstruktorn) eller på
/// operationsnamn/taggar i testet, och undvik parallella tester som delar förväntningar på antal spår.
/// Loggar fångas däremot bara från den app som fångsten är kopplad till.
/// </para>
/// </summary>
public sealed class TelemetryCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Activity> _activities = [];
    private readonly List<CapturedLog> _logs = [];
    private readonly ActivityListener _listener;

    /// <param name="sourceFilter">Vilka ActivitySource-namn som ska fångas. Standard: alla.</param>
    public TelemetryCapture(Func<string, bool>? sourceFilter = null)
    {
        LoggerProvider = new CaptureLoggerProvider(this);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => sourceFilter?.Invoke(source.Name) ?? true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_gate)
                    _activities.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Färdiga aktiviteter, i den ordning de avslutades. Returnerar en kopia.</summary>
    public IReadOnlyList<Activity> Activities
    {
        get { lock (_gate) return _activities.ToArray(); }
    }

    /// <summary>Loggposter i den ordning de skrevs. Returnerar en kopia.</summary>
    public IReadOnlyList<CapturedLog> Logs
    {
        get { lock (_gate) return _logs.ToArray(); }
    }

    /// <summary>Logger-provider som ApiFactory registrerar i appen.</summary>
    public ILoggerProvider LoggerProvider { get; }

    /// <summary>Tömmer insamlade aktiviteter och loggar.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _activities.Clear();
            _logs.Clear();
        }
    }

    public void Dispose() => _listener.Dispose();

    private void AddLog(CapturedLog log)
    {
        lock (_gate)
            _logs.Add(log);
    }

    private sealed class CaptureLoggerProvider(TelemetryCapture owner) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(owner, categoryName);

        public void Dispose() { }
    }

    private sealed class CaptureLogger(TelemetryCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.AddLog(new CapturedLog(category, logLevel, formatter(state, exception), exception));
    }
}
