using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TestGen.Core.Settings;

namespace SagaPerception.Testing.NUnit.Tests;

/// <summary>Värdpolicyn för appens utgående anrop: allow släpper igenom, block (standard) svarar 502 med en förklaring, mock svarar ur en spec.</summary>
public class HostGateTests
{
    private string _dir = "";
    private ServiceProvider? _provider;

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-hosts-").FullName;

    [TearDown]
    public void Down()
    {
        _provider?.Dispose();
        Directory.Delete(_dir, true);
    }

    private sealed class Real : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("riktigt system") });
        }
    }

    private HostPolicy Policy(string hosts) =>
        TestGenSettings.LoadFile(Write("testgen.yaml", "hosts:\n" + hosts)).Hosts;

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>En HttpClient som skapas som i en app: via IHttpClientFactory, med policyn registrerad och <paramref name="real"/> som det riktiga systemet.</summary>
    private (HttpClient Client, HostGate Gate) Client(HostPolicy policy, Real real, string name = "betalning", params string[] specDirectories)
    {
        var gate = new HostGate(policy, specDirectories.Length == 0 ? [_dir] : specDirectories);
        var services = new ServiceCollection();
        gate.Register(services);
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => real);
        _provider = services.BuildServiceProvider();
        return (_provider.GetRequiredService<IHttpClientFactory>().CreateClient(name), gate);
    }

    [Test]
    public async Task Allow_passes_the_call_to_the_real_system()
    {
        var real = new Real();
        var (client, gate) = Client(Policy("  api.example-pay.com: allow\n"), real);

        var response = await client.GetAsync("https://api.example-pay.com/ping");

        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("riktigt system"));
        Assert.That(real.Seen, Has.Count.EqualTo(1));
        Assert.That(gate.Calls, Is.Empty, "tillåtna anrop registreras inte");
    }

    [Test]
    public async Task An_own_address_such_as_a_mock_the_test_started_passes_even_when_everything_else_is_blocked()
    {
        var real = new Real();
        var (client, gate) = Client(TestGenSettings.Defaults.Hosts, real);
        gate.AllowOwn("http://127.0.0.1:5999");

        var own = await client.GetAsync("http://127.0.0.1:5999/customers");
        var other = await client.GetAsync("http://127.0.0.1:5998/customers");

        Assert.That(own.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway), "bara den egna adressen, inte hela värden");
        Assert.That(gate.Calls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Block_is_the_default_and_answers_502_with_problem_json_that_says_what_to_do()
    {
        var real = new Real();
        var (client, gate) = Client(Policy("  default: block\n"), real);

        var response = await client.PostAsync("https://betaltjanst.example/payments?x=1", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/problem+json"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("testgen blockerade anropet till betaltjanst.example (värdpolicy block)")
            .And.Contain("från klienten betalning").And.Contain("Gör så här").And.Contain("betaltjanst.example: mock").And.Contain("betaltjanst.example: allow"));
        Assert.That(real.Seen, Is.Empty, "det riktiga systemet nås aldrig");
        var call = gate.Calls.Single();
        Assert.That((call.Host, call.Method, call.Url, call.Action, call.Status), Is.EqualTo(("betaltjanst.example", "POST", "https://betaltjanst.example/payments", HostAction.Block, 502)));
        Assert.That(call.Text, Does.Contain("POST https://betaltjanst.example/payments → 502 (block, klient betalning)"));
    }

    [Test]
    public async Task Without_any_policy_in_the_file_everything_is_blocked()
    {
        var (client, _) = Client(TestGenSettings.Defaults.Hosts, new Real());
        Assert.That((await client.GetAsync("https://vilken.som.helst/")).StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
    }

    [Test]
    public async Task Mock_answers_from_the_spec_named_after_the_host_and_logs_the_call()
    {
        var real = new Real();
        File.Copy(Samples.Betaltjanst, Path.Combine(_dir, "betaltjanst.openapi.yaml"));
        var (client, gate) = Client(Policy("  default: block\n  betaltjanst.example: mock\n"), real);

        var response = await client.PostAsync("https://betaltjanst.example/payments",
            new StringContent("""{"customerId":1,"amount":99.5,"currency":"SEK"}""", Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("paymentId"));
        Assert.That(real.Seen, Is.Empty);
        var call = gate.Calls.Single();
        Assert.That((call.Action, call.Status, call.Note), Is.EqualTo((HostAction.Mock, 201, (string?)null)));
        await gate.DisposeAsync();
    }

    [Test]
    public async Task Mock_checks_the_call_against_the_spec_and_the_difference_is_noted()
    {
        File.Copy(Samples.Betaltjanst, Path.Combine(_dir, "betaltjanst.openapi.yaml"));
        var (client, gate) = Client(Policy("  betaltjanst.example: mock\n"), new Real());

        var response = await client.PostAsync("https://betaltjanst.example/payments", new StringContent("""{"amount":"mycket"}""", Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(gate.Calls.Single().Note, Does.Contain("customerId"));
        await gate.DisposeAsync();
    }

    [Test]
    public async Task Mock_without_a_spec_says_where_to_put_one()
    {
        var (client, gate) = Client(Policy("  betaltjanst.example: mock\n"), new Real());

        var response = await client.GetAsync("https://betaltjanst.example/payments");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("ingen spec hittades").And.Contain("betaltjanst.openapi.yaml").And.Contain("betaltjanst.example: allow"));
        Assert.That(gate.Calls.Single().Action, Is.EqualTo(HostAction.Mock));
    }

    [TestCase("betaltjanst.example", "betaltjanst")]
    [TestCase("API.Example-Pay.com", "api")]
    [TestCase("localhost", "localhost")]
    public void The_spec_is_named_after_the_first_part_of_the_host(string host, string expected) =>
        Assert.That(HostGate.SpecName(host), Is.EqualTo(expected));
}
