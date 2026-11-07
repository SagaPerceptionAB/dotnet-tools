using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Playwright;
using Testcontainers.MsSql;
using WireMock.Server;

// Utan namespace så att fixturen gäller alla tester i assemblyn.
[SetUpFixture]
public sealed class ApiEnvironment
{
    public const string DbName = "testgen";

    private static MsSqlContainer? _sql;
    private static WireMockServer? _wireMock;
    private static Process? _api;
    private static IPlaywright? _playwright;
    private static readonly StringBuilder ApiLog = new();

    public static bool Available { get; private set; }
    /// <summary>Förklaring när Available är false.</summary>
    public static string UnavailableReason { get; private set; } = "Miljön har inte initierats.";
    /// <summary>True när orsaken är konfiguration (saknad env), annars infrastruktur (Docker m.m.).</summary>
    public static bool NotConfigured { get; private set; }
    public static string BaseUrl { get; private set; } = "";
    public static string? ConnectionString { get; private set; }
    public static string? WireMockUrl => _wireMock?.Url;
    public static IAPIRequestContext Request { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var project = Environment.GetEnvironmentVariable("TESTGEN_API_PROJECT");
        if (string.IsNullOrWhiteSpace(project))
        {
            NotConfigured = true;
            UnavailableReason = "Miljövariabeln TESTGEN_API_PROJECT är inte satt. Sätt den till sökvägen till API-projektets .csproj " +
                                "(t.ex. export TESTGEN_API_PROJECT=/sökväg/till/Api.csproj) för att köra API-testerna.";
            return;
        }
        if (!File.Exists(project) && !Directory.Exists(project))
        {
            NotConfigured = true;
            UnavailableReason = $"TESTGEN_API_PROJECT pekar på '{project}' som inte finns.";
            return;
        }

        try
        {
            // 1. SQL Server
            try
            {
                _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                await _sql.StartAsync();
            }
            catch (Exception e)
            {
                UnavailableReason = "Kunde inte starta SQL Server via Testcontainers. Är Docker installerat och igång? " + e.Message;
                await StopAsync();
                return;
            }
            await using (var master = new SqlConnection(_sql.GetConnectionString()))
            {
                await master.OpenAsync();
                await using var cmd = master.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE [{DbName}]";
                await cmd.ExecuteNonQueryAsync();
            }
            ConnectionString = new SqlConnectionStringBuilder(_sql.GetConnectionString())
            { InitialCatalog = DbName, TrustServerCertificate = true }.ConnectionString;

            // 2. WireMock (valfri): TESTGEN_WIREMOCK=1 eller mappings i wiremock/mappings.
            var wmDir = FindWireMockDir();
            if (Environment.GetEnvironmentVariable("TESTGEN_WIREMOCK") == "1" ||
                (wmDir is not null && Directory.EnumerateFiles(wmDir, "*.json").Any()))
            {
                _wireMock = WireMockServer.Start();
                if (wmDir is not null) _wireMock.ReadStaticMappings(wmDir);
            }

            // 3. API
            await StartApiAsync(project);

            _playwright = await Playwright.CreateAsync();
            Request = await _playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
            {
                BaseURL = BaseUrl,
                IgnoreHTTPSErrors = true,
            });
            Available = true;
        }
        catch (Exception e)
        {
            UnavailableReason = $"Testmiljön kunde inte startas: {e.Message}\n--- API-logg ---\n{ApiLog}";
            await StopAsync();
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        Available = false;
        try { if (Request is not null) await Request.DisposeAsync(); } catch { }
        _playwright?.Dispose(); _playwright = null;
        if (_api is { HasExited: false }) { try { _api.Kill(entireProcessTree: true); _api.WaitForExit(10000); } catch { } }
        _api?.Dispose(); _api = null;
        _wireMock?.Stop(); _wireMock?.Dispose(); _wireMock = null;
        if (_sql is not null) { try { await _sql.DisposeAsync(); } catch { } _sql = null; }
    }

    private static string? FindWireMockDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            foreach (var rel in new[] { "wiremock", Path.Combine("tests", "Api.Tests", "wiremock") })
            {
                var p = Path.Combine(d.FullName, rel, "mappings");
                if (Directory.Exists(p)) return p;
            }
        }
        return null;
    }

    private static async Task StartApiAsync(string project)
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "run", "--project", project, "--no-launch-profile" }) psi.ArgumentList.Add(a);
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
        // Vanliga namn på connection string; API:et läser det som det använder.
        psi.Environment["ConnectionStrings__Default"] = ConnectionString!;
        psi.Environment["ConnectionStrings__DefaultConnection"] = ConnectionString!;
        psi.Environment["TESTGEN_CONNECTION_STRING"] = ConnectionString!;
        if (WireMockUrl is not null)
        {
            psi.Environment["WireMock__BaseUrl"] = WireMockUrl;
            psi.Environment["WIREMOCK_URL"] = WireMockUrl;
        }

        _api = Process.Start(psi) ?? throw new InvalidOperationException("Kunde inte starta 'dotnet run'.");
        _api.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (ApiLog) ApiLog.AppendLine(e.Data); };
        _api.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (ApiLog) ApiLog.AppendLine(e.Data); };
        _api.BeginOutputReadLine();
        _api.BeginErrorReadLine();

        var timeout = int.TryParse(Environment.GetEnvironmentVariable("TESTGEN_API_TIMEOUT_SECONDS"), out var t) ? t : 180;
        var readyPath = Environment.GetEnvironmentVariable("TESTGEN_API_READY_PATH") ?? "/";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (_api.HasExited)
                throw new InvalidOperationException($"API-processen avslutades med kod {_api.ExitCode} innan den blev redo.\n{ApiLog}");
            try
            {
                using var _ = await http.GetAsync(BaseUrl + readyPath); // valfritt svar (även 404) = servern lyssnar
                return;
            }
            catch (Exception) { await Task.Delay(500); }
        }
        throw new TimeoutException($"API:et blev inte redo inom {timeout} s på {BaseUrl}.");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Anropas först i varje test: hoppar över testet med tydligt meddelande om miljön saknas.</summary>
    public static void RequireOrSkip()
    {
        if (Available) return;
        if (NotConfigured) Assert.Ignore(UnavailableReason);
        Assert.Inconclusive(UnavailableReason);
    }
}
