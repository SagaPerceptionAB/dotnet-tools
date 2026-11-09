using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;

namespace SagaPerception.Testing;

/// <summary>Skapar en <see cref="ApiFactory{TProgram}"/> för en app som pekas ut med projektnamn.</summary>
public static class ApiFactory
{
    /// <summary>Miljön appen körs i om inget annat anges.</summary>
    public const string DefaultEnvironment = "Testing";

    /// <summary>Standardinställningarna i alla fabriker: tom OTLP-adress = ingen export, även om miljövariabeln är satt på maskinen.</summary>
    internal static IReadOnlyDictionary<string, string?> DefaultSettings { get; } = new Dictionary<string, string?>
    {
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
        ["Observability:OtlpEndpoint"] = "",
    };

    /// <summary>
    /// Läser in assemblyn <paramref name="project"/> (testprojektet ska ha en ProjectReference till den) och bygger fabriken kring
    /// appens startpunkt. Det fungerar också när Program ligger i den globala namnrymden och flera appar har varsin.
    /// </summary>
    public static IApiFactory Create(string project)
    {
        var program = EntryAssembly(project).EntryPoint?.DeclaringType
            ?? throw new InvalidOperationException($"'{project}' har ingen startpunkt (Program), så det är ingen app.");
        return (IApiFactory)Activator.CreateInstance(typeof(ApiFactory<>).MakeGenericType(program))!;
    }

    /// <summary>
    /// Bygger appens tjänster utan att köra något efter <c>Build()</c> (migreringar, kontroller av konfigurationen, <c>Run()</c>), som
    /// <c>dotnet ef</c>: startpunkten körs i <paramref name="environment"/> och <paramref name="settings"/> som kommandoradsargument, och stoppas så snart
    /// värden är byggd. Appen ska ge <c>args</c> till <c>WebApplication.CreateBuilder</c>. Värden är inte startad; anroparen avslutar den.
    /// </summary>
    /// <param name="settings">Konfigurationsvärden, utöver standardvärdena.</param>
    /// <param name="environment">Appens miljö.</param>
    /// <param name="contentRoot">Appens katalog (där appsettings.json ligger); utan den används aktuell katalog.</param>
    public static IHost BuildHost(string project, IReadOnlyDictionary<string, string?>? settings = null, string? contentRoot = null, string environment = DefaultEnvironment)
    {
        var entry = EntryAssembly(project).EntryPoint
            ?? throw new InvalidOperationException($"'{project}' har ingen startpunkt (Program), så det är ingen app.");
        var args = new List<string> { "--environment=" + environment };
        if (contentRoot is not null) args.Add("--contentRoot=" + contentRoot);
        var all = DefaultSettings.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) all[key] = value;
        args.AddRange(all.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"--{kv.Key}={kv.Value}"));

        var capture = new HostCapture();
        HostCapture.Current.Value = capture;
        HostCapture.EnsureListening();
        try
        {
            var result = entry.Invoke(null, entry.GetParameters().Length == 0 ? [] : [args.ToArray()]);
            if (result is Task task) task.GetAwaiter().GetResult();
        }
        catch (Exception e) when (capture.Host is not null && Unwrap(e) is HostAbortedException) { /* så ska det gå: värden är byggd och startpunkten stoppad */ }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
        finally { HostCapture.Current.Value = null; }
        return capture.Host ?? throw new InvalidOperationException($"Startpunkten i '{project}' byggde ingen värd (IHost), så tjänsterna kan inte läsas.");
    }

    private static Exception Unwrap(Exception e) => e is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : e;

    private static Assembly EntryAssembly(string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        try { return Assembly.Load(project); }
        catch (FileNotFoundException e)
        {
            throw new InvalidOperationException($"Hittar inte projektet '{project}'. Lägg en ProjectReference till det i testprojektet och kontrollera stavningen.", e);
        }
    }
}
