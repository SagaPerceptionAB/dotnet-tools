using System.Net;
using Microsoft.Extensions.Logging;
using SagaPerception.Testing;
using TestGen.Core.Run;
using TestGen.Core.Settings;

namespace SagaPerception.Testing.NUnit.Tests;

/// <summary>Meddelandet i ett fallerande test: förklaringen från terminalen, anropen, appens utgående anrop och logg, med hemligheter maskade.</summary>
public class FailureTextTests
{
    private sealed class Answers(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("""{"error":"internal error"}""", System.Text.Encoding.UTF8, "application/json") });
    }

    private static async Task<(TestOutcome Outcome, HeaderMask Mask)> Failed(string token)
    {
        var spec = Path.Combine(Samples.Directory, "shop.openapi.yaml");
        var runner = new TestRunner(new RunOptions { Spec = spec, Api = "http://fake.test", Seed = 1, Token = token, Tests = ["T-001"] },
            new Terminal(TextWriter.Null, TextWriter.Null, false), new Answers(HttpStatusCode.InternalServerError));
        await runner.RunAsync();
        return (runner.Outcomes.Single(), runner.Mask);
    }

    [Test]
    public async Task The_message_has_the_explanation_the_technical_lines_and_the_curl()
    {
        var (outcome, mask) = await Failed("hemlig-token-1234");

        var text = FailureText.Build(outcome, mask, [], []);

        Assert.That(text, Does.StartWith("Skapa kund – giltig indata"));
        Assert.That(text, Does.Contain("Tolkning:").And.Contain("anrop: POST http://fake.test/customers").And.Contain("curl för anropet:"));
        Assert.That(text, Does.Not.Contain("Appens utgående anrop").And.Not.Contain("Appens logg"));
    }

    [Test]
    public async Task Secrets_are_masked_everywhere_in_the_message()
    {
        var (outcome, mask) = await Failed("hemlig-token-1234");

        var text = FailureText.Build(outcome, mask, [new HostCall("x.example", "klient", "GET", "https://x.example/?t=hemlig-token-1234", HostAction.Block, 502)],
            [new CapturedLog("Kategori", LogLevel.Error, "tokenet var hemlig-token-1234", null)]);

        Assert.That(text, Does.Not.Contain("hemlig-token-1234"));
        Assert.That(text, Does.Contain("***"));
    }

    [Test]
    public async Task Outgoing_calls_and_important_log_lines_are_attached()
    {
        var (outcome, mask) = await Failed("hemlig-token-1234");
        var calls = new[]
        {
            new HostCall("betaltjanst.example", "PaymentGateway", "POST", "https://betaltjanst.example/payments", HostAction.Mock, 502, "customerId: saknas"),
            new HostCall("lager.example", "", "GET", "https://lager.example/lager", HostAction.Block, 502),
        };
        var logs = new[]
        {
            new CapturedLog("ShopApi.Orders", LogLevel.Information, "Skapar order", null),
            new CapturedLog("ShopApi.Orders", LogLevel.Error, "Betalningen misslyckades", new InvalidOperationException("nej")),
            new CapturedLog("Microsoft.EntityFrameworkCore", LogLevel.Warning, "Långsam fråga", null),
        };

        var text = FailureText.Build(outcome, mask, calls, logs);

        Assert.That(text, Does.Contain("Appens utgående anrop (värdpolicyn i testgen.yaml):"));
        Assert.That(text, Does.Contain("POST https://betaltjanst.example/payments → 502 (mock, klient PaymentGateway): customerId: saknas"));
        Assert.That(text, Does.Contain("GET https://lager.example/lager → 502 (block)"));
        Assert.That(text, Does.Contain("[Error] ShopApi.Orders: Betalningen misslyckades – InvalidOperationException: nej"));
        Assert.That(text, Does.Contain("[Warning] Microsoft.EntityFrameworkCore: Långsam fråga"));
        Assert.That(text, Does.Not.Contain("Skapar order"), "bara varningar och fel i loggen");
    }

    [Test]
    public async Task A_long_log_is_cut_short()
    {
        var (outcome, mask) = await Failed("hemlig-token-1234");
        var logs = Enumerable.Range(0, 30).Select(i => new CapturedLog("K", LogLevel.Warning, $"rad {i}", null)).ToList();

        var text = FailureText.Build(outcome, mask, [], logs);

        Assert.That(text, Does.Contain("rad 19").And.Not.Contain("rad 20").And.Contain("… och 10 till."));
    }
}
