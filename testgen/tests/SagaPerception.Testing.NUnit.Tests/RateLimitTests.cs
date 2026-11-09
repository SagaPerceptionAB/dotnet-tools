using System.Diagnostics;

namespace SagaPerception.Testing.NUnit.Tests;

/// <summary>429 från AuthApp: Retry-After följs en gång (högst 10 s), annars hoppas testet över med orsaken rate-limited.</summary>
public class RateLimitTests
{
    private string _dir = "";
    private string _out = "";

    [SetUp]
    public void Up()
    {
        _dir = Directory.CreateTempSubdirectory("testgen-rate-").FullName;
        _out = Directory.CreateTempSubdirectory("testgen-rate-out-").FullName;
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AuthApp"));
        File.WriteAllText(Path.Combine(_dir, "S.slnx"), $"<Solution><Project Path=\"{Path.Combine(app, "AuthApp.csproj")}\" /></Solution>");
        File.Copy(Path.Combine(app, "authapp.openapi.yaml"), Path.Combine(_dir, "authapp.openapi.yaml"));
        File.WriteAllText(Path.Combine(_dir, "testgen.yaml"), "apis:\n  AuthApp:\n    spec: authapp.openapi.yaml\n");
    }

    [TearDown]
    public void Down()
    {
        Directory.Delete(_dir, true);
        Directory.Delete(_out, true);
    }

    private async Task<(ApiSession Session, ApiPlan Plan)> StartAsync()
    {
        var plan = ApiPlan.Create(() => TestGenTarget.Resolve(_dir, null, "AuthApp", testProjectDirectory: _out), "AuthApp");
        return (await ApiSession.StartAsync(plan), plan);
    }

    private static Task Run(ApiSession session, ApiPlan plan, string call) =>
        session.RunTestAsync(plan.Tests.First(t => t.Call == call).Id, CancellationToken.None);

    [Test]
    public async Task A_429_with_retry_after_is_retried_once_and_then_passes()
    {
        var (session, plan) = await StartAsync();
        await using var _ = session;
        var clock = Stopwatch.StartNew();

        await Run(session, plan, "GET /once");

        Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900)), "väntade Retry-After: 1");
    }

    [Test]
    public async Task A_429_that_stays_after_the_wait_is_skipped_as_rate_limited()
    {
        var (session, plan) = await StartAsync();
        await using var _ = session;

        var skipped = Assert.ThrowsAsync<InconclusiveException>(() => Run(session, plan, "GET /always"))!;

        Assert.That(skipped.Message, Does.Contain("429").And.Contain("för många anrop"));
        Assert.That(File.ReadAllText(session.WriteResult()!), Does.Contain("rate-limited"));
    }

    [Test]
    public async Task A_429_without_retry_after_is_skipped_at_once()
    {
        var (session, plan) = await StartAsync();
        await using var _ = session;
        var clock = Stopwatch.StartNew();

        var skipped = Assert.ThrowsAsync<InconclusiveException>(() => Run(session, plan, "GET /never"))!;

        Assert.That(skipped.Message, Does.Contain("429"));
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task A_retry_after_longer_than_ten_seconds_is_not_waited_for()
    {
        var (session, plan) = await StartAsync();
        await using var _ = session;
        var clock = Stopwatch.StartNew();

        Assert.ThrowsAsync<InconclusiveException>(() => Run(session, plan, "GET /slow"));

        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
    }
}
