using System.Net;
using TestGen.Core;
using TestGen.Core.Run;
using TestGen.Core.Settings;

namespace TestGen.Tests;

/// <summary>Claims och inloggningssteg i testgen.yaml, 429 med Retry-After, och namnen som ersätter platshållarna i vägledningen.</summary>
public class AuthAndRateLimitTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-auth-core-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private TestGenSettings Load(string yaml)
    {
        var path = Path.Combine(_dir, "testgen.yaml");
        File.WriteAllText(path, yaml);
        return TestGenSettings.LoadFile(path);
    }

    private UsageException Invalid(string yaml)
    {
        var path = Path.Combine(_dir, "bad.yaml");
        File.WriteAllText(path, yaml);
        return Assert.Throws<UsageException>(() => TestGenSettings.LoadFile(path))!;
    }

    // --- testgen.yaml ---

    [Test]
    public void Claims_and_the_login_step_are_read_per_project()
    {
        var settings = Load("""
            apis:
              Orders.Api:
                auth:
                  claims: { role: Admin, tenant: t1 }
                login:
                  operation: SignIn
                  body: { provider: test }
                  token: accessToken
            apps:
              Web:
                auth:
                  claims: { role: Editor }
            """);

        Assert.That(settings.ApiClaims("orders.api"), Is.EqualTo(new Dictionary<string, string> { ["role"] = "Admin", ["tenant"] = "t1" }));
        Assert.That(settings.AppClaims("Web"), Is.EqualTo(new Dictionary<string, string> { ["role"] = "Editor" }));
        var login = settings.LoginOf("Orders.Api")!;
        Assert.That((login.Operation, login.Token), Is.EqualTo(("SignIn", "accessToken")));
        Assert.That(login.Body, Is.EqualTo(new Dictionary<string, object> { ["provider"] = "test" }));
        Assert.That(settings.ApiClaims("Other"), Is.Empty);
        Assert.That(settings.LoginOf("Other"), Is.Null);
    }

    [Test]
    public void A_login_step_needs_an_operation_and_a_token_path_and_unknown_keys_get_a_suggestion()
    {
        Assert.That(Invalid("apis:\n  A:\n    login:\n      operation: SignIn\n").Message, Does.Contain("apis.A.login saknar token."));
        Assert.That(Invalid("apis:\n  A:\n    login:\n      token: t\n").Message, Does.Contain("apis.A.login saknar operation."));
        Assert.That(Invalid("apis:\n  A:\n    login:\n      operation: S\n      token: t\n      bodys: {}\n").Message, Does.Contain("okänd nyckel 'bodys'").And.Contain("Menade du body?"));
        Assert.That(Invalid("apis:\n  A:\n    auth:\n      claim: {}\n").Message, Does.Contain("Menade du claims?"));
        Assert.That(Invalid("apis:\n  A:\n    auth:\n      claims: { role: [a] }\n").Message, Does.Contain("apis.A.auth.claims.role ska vara text."));
        Assert.That(Invalid("apps:\n  W:\n    auth:\n      claims: text\n").Message, Does.Contain("apps.W.auth.claims ska vara en uppsättning"));
    }

    // --- 429 ---

    private sealed class Answers(params (HttpStatusCode Status, string? RetryAfter)[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, retryAfter) = responses[Math.Min(Calls++, responses.Length - 1)];
            var response = new HttpResponseMessage(status);
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        }
    }

    private static async Task<(HttpStatusCode Status, List<TimeSpan> Waits, Answers Inner)> SendAsync(Answers inner, HttpContent? content = null)
    {
        var waits = new List<TimeSpan>();
        using var client = new HttpClient(new RateLimitHandler(inner, disposeInner: false, (wait, _) => { waits.Add(wait); return Task.CompletedTask; }));
        using var response = await client.PostAsync("http://localhost/x", content);
        return (response.StatusCode, waits, inner);
    }

    [Test]
    public async Task A_429_with_retry_after_seconds_waits_that_long_and_sends_the_same_request_again()
    {
        var (status, waits, inner) = await SendAsync(new Answers((HttpStatusCode.TooManyRequests, "2"), (HttpStatusCode.OK, null)), new StringContent("{\"a\":1}"));

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(2) }));
        Assert.That(inner.Bodies, Is.EqualTo(new[] { "{\"a\":1}", "{\"a\":1}" }));
    }

    [Test]
    public async Task A_second_429_is_given_on_after_one_wait_only()
    {
        var (status, waits, inner) = await SendAsync(new Answers((HttpStatusCode.TooManyRequests, "1")));

        Assert.That(status, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(waits, Has.Count.EqualTo(1));
        Assert.That(inner.Calls, Is.EqualTo(2));
    }

    [TestCase(null)]
    [TestCase("11")]
    [TestCase("3600")]
    public async Task A_429_without_retry_after_or_with_a_wait_over_ten_seconds_is_given_on_at_once(string? retryAfter)
    {
        var (status, waits, inner) = await SendAsync(new Answers((HttpStatusCode.TooManyRequests, retryAfter), (HttpStatusCode.OK, null)));

        Assert.That(status, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(waits, Is.Empty);
        Assert.That(inner.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Retry_after_as_a_date_in_the_past_retries_without_waiting_and_ten_seconds_is_still_waited_for()
    {
        var past = await SendAsync(new Answers((HttpStatusCode.TooManyRequests, DateTimeOffset.UtcNow.AddMinutes(-1).ToString("R")), (HttpStatusCode.OK, null)));
        var ten = await SendAsync(new Answers((HttpStatusCode.TooManyRequests, "10"), (HttpStatusCode.OK, null)));

        Assert.That(past.Waits, Is.EqualTo(new[] { TimeSpan.Zero }));
        Assert.That(ten.Waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(10) }));
    }

    [Test]
    public async Task Other_statuses_are_never_retried()
    {
        var (status, _, inner) = await SendAsync(new Answers((HttpStatusCode.ServiceUnavailable, "1")));

        Assert.That(status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(inner.Calls, Is.EqualTo(1));
    }

    // --- Orsaker och namn ---

    [Test]
    public void The_new_causes_have_codes_names_and_guidance_that_names_the_keys()
    {
        Assert.That(CauseCodes.Code(FailureCause.TestUserRejected), Is.EqualTo("test-user-rejected"));
        Assert.That(CauseCodes.Code(FailureCause.RateLimited), Is.EqualTo("rate-limited"));
        Assert.That(CauseCodes.TryParse("Rate-Limited", out var cause) && cause == FailureCause.RateLimited, Is.True);
        Assert.That(PlainLanguage.CauseExplanation(FailureCause.TestUserRejected), Does.Contain("auth.claims").And.Contain("login"));
        Assert.That(PlainLanguage.CauseExplanation(FailureCause.TestUserRejected, names: new PlainNames("Orders.Api", "local")),
            Does.Contain("apis.Orders.Api.auth.claims").And.Contain("apis.Orders.Api.login").And.Not.Contain("<api>"));
    }

    [Test]
    public void Placeholders_are_replaced_when_the_names_are_known_and_kept_when_they_are_not()
    {
        const string text = "environments.<miljö>.apis.<api>.token och apis.<api>.bindings";

        Assert.That(PlainNames.Fill(text, new PlainNames("ShopApi", "test")), Is.EqualTo("environments.test.apis.ShopApi.token och apis.ShopApi.bindings"));
        Assert.That(PlainNames.Fill(text, new PlainNames("ShopApi", null)), Is.EqualTo("environments.<miljö>.apis.ShopApi.token och apis.ShopApi.bindings"));
        Assert.That(PlainNames.Fill(text, null), Is.EqualTo(text));
    }

    [Test]
    public void The_auth_note_names_the_project_and_the_environment_in_the_token_key()
    {
        var statuses = new int?[] { 401, 401, 401 };

        Assert.That(PlainLanguage.AuthNote(statuses, names: new PlainNames("ShopApi", "test")), Does.Contain("environments.test.apis.ShopApi.token"));
        Assert.That(PlainLanguage.AuthNote(statuses), Does.Contain("environments.<miljö>.apis.<api>.token"));
    }
}
