using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace SagaPerception.TestGen.NUnit;

/// <summary>
/// Fångar värden när appen byggt den (händelsen <c>HostBuilt</c> från <c>Microsoft.Extensions.Hosting</c>) och stoppar startpunkten med
/// <see cref="HostAbortedException"/>, som <c>dotnet ef</c>. Bara den pågående anropskedjan berörs, så att värdar som byggs samtidigt
/// i andra tester lämnas ifred.
/// </summary>
internal sealed class HostCapture
{
    public static readonly AsyncLocal<HostCapture?> Current = new();

    private static readonly object Gate = new();
    private static bool _listening;

    public IHost? Host { get; private set; }

    public static void EnsureListening()
    {
        lock (Gate)
        {
            if (_listening) return;
            DiagnosticListener.AllListeners.Subscribe(new Listeners());
            _listening = true;
        }
    }

    private sealed class Listeners : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.Extensions.Hosting") listener.Subscribe(new Events());
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }
    }

    private sealed class Events : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key != "HostBuilt" || Current.Value is not { } capture || value.Value is not IHost host) return;
            capture.Host = host;
            throw new HostAbortedException();
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }
    }
}
