using System.Net;
using System.Net.Http.Headers;

namespace SagaPerception.Testing.NUnit.Tests;

/// <summary>
/// AuthApp (JwtBearer som standard) körs i testprocessen: testanvändaren godtas utan konfiguration, claims ger roller, ett inloggningssteg
/// ersätter testanvändaren, och en token i testgen.yaml vinner över båda.
/// </summary>
public class AuthTests
{
    private string _dir = "";
    private string _out = "";

    [SetUp]
    public void Up()
    {
        _dir = Directory.CreateTempSubdirectory("testgen-auth-").FullName;
        _out = Directory.CreateTempSubdirectory("testgen-auth-out-").FullName;
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AuthApp"));
        File.WriteAllText(Path.Combine(_dir, "S.slnx"), $"<Solution><Project Path=\"{Path.Combine(app, "AuthApp.csproj")}\" /></Solution>");
        File.Copy(Path.Combine(app, "authapp.openapi.yaml"), Path.Combine(_dir, "authapp.openapi.yaml"));
    }

    [TearDown]
    public void Down()
    {
        Directory.Delete(_dir, true);
        Directory.Delete(_out, true);
    }

    private ApiPlan Plan(string apiYaml = "", string rest = "", Func<string, string?>? getEnv = null)
    {
        File.WriteAllText(Path.Combine(_dir, "testgen.yaml"), "apis:\n  AuthApp:\n    spec: authapp.openapi.yaml\n" + apiYaml + rest);
        return ApiPlan.Create(() => TestGenTarget.Resolve(_dir, null, "AuthApp", getEnv, testProjectDirectory: _out), "AuthApp");
    }

    private static string IdOf(ApiPlan plan, string call) => plan.Tests.First(t => t.Call == call).Id;

    private static Task Run(ApiSession session, ApiPlan plan, string call) => session.RunTestAsync(IdOf(plan, call), CancellationToken.None);

    [Test]
    public async Task The_test_user_is_accepted_without_configuration_by_the_default_scheme_and_by_a_named_scheme()
    {
        var plan = Plan();
        await using var session = await ApiSession.StartAsync(plan);

        Assert.That(session.StartFailure, Is.Null);
        await Run(session, plan, "GET /me");
        await Run(session, plan, "GET /named");
        await Run(session, plan, "GET /open");
    }

    [Test]
    public async Task A_request_without_the_token_is_still_rejected_and_the_token_is_never_the_apps_own()
    {
        var user = TestUser.Create(new Dictionary<string, string>());
        using var factory = ApiFactory.Create("AuthApp").WithServices(user.Register);
        using var client = factory.CreateClient();

        Assert.That((await client.GetAsync("/me")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "inte-testgens-token-1234");
        Assert.That((await client.GetAsync("/me")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var me = await client.GetAsync("/me");
        Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await me.Content.ReadAsStringAsync(), Does.Contain("Test Testsson").And.Contain("test.testsson@example.com"));
    }

    [Test]
    public async Task The_test_user_has_no_role_unless_claims_give_one()
    {
        var plan = Plan();
        await using var session = await ApiSession.StartAsync(plan);

        var skipped = (await Assert.ThrowsAsync<InconclusiveException>(() => Run(session, plan, "GET /admin")))!;

        Assert.That(skipped.Message, Does.Contain("testanvändaren").And.Contain("apis.AuthApp.auth.claims").And.Contain("apis.AuthApp.login"));
        Assert.That(skipped.Message, Does.Not.Contain("<api>"));
    }

    [Test]
    public async Task Claims_in_testgen_yaml_give_the_role_and_the_known_user()
    {
        var plan = Plan("    auth:\n      claims:\n        role: Admin\n        sub: known-user\n");
        await using var session = await ApiSession.StartAsync(plan);

        await Run(session, plan, "GET /admin");
        await Run(session, plan, "GET /strict");
    }

    [Test]
    public async Task A_valid_call_rejected_for_the_test_user_is_skipped_with_its_own_cause_and_nothing_fails()
    {
        var plan = Plan();
        await using var session = await ApiSession.StartAsync(plan);
        await Assert.ThrowsAsync<InconclusiveException>(() => Run(session, plan, "GET /strict"));

        var json = File.ReadAllText(session.WriteResult()!);

        Assert.That(json, Does.Contain("\"cause\": \"test-user-rejected\"").Or.Contain("\"cause\":\"test-user-rejected\""));
        Assert.That(File.ReadAllText(session.WriteResult()!), Does.Not.Contain("\"outcome\": \"failed\"").And.Not.Contain("\"outcome\":\"failed\""));
    }

    [Test]
    public async Task A_login_step_gives_the_token_and_the_test_user_is_not_registered()
    {
        var plan = Plan("    login:\n      operation: signIn\n      body: { userName: Inloggad }\n      token: accessToken\n");
        await using var session = await ApiSession.StartAsync(plan);

        Assert.That(session.StartFailure, Is.Null);
        await Run(session, plan, "GET /admin");   // rollen och sub kommer ur appens egen token
        await Run(session, plan, "GET /strict");
        await Run(session, plan, "GET /me");
    }

    [Test]
    public async Task The_token_from_the_login_step_is_masked_in_the_result_file()
    {
        var plan = Plan("    login:\n      operation: signIn\n      body: { userName: Inloggad }\n      token: accessToken\n");
        await using var session = await ApiSession.StartAsync(plan);
        await Run(session, plan, "GET /me");

        var json = File.ReadAllText(session.WriteResult()!);

        Assert.That(json, Does.Not.Contain("eyJ"));
    }

    [Test]
    public async Task A_login_with_an_unknown_operation_fails_the_fixture_in_three_parts()
    {
        var plan = Plan("    login:\n      operation: signin\n      token: accessToken\n");
        await using var session = await ApiSession.StartAsync(plan);

        Assert.That(session.StartFailure, Is.Not.Null);
        var lines = session.StartFailure!.Split(Environment.NewLine);
        Assert.That(lines, Has.Length.EqualTo(3));
        Assert.That(lines[0], Does.Contain("'signin'").And.Contain("ingen sådan"));
        Assert.That(lines[1], Does.StartWith("Gör så här: ").And.Contain("apis.AuthApp.login.operation").And.Contain("Menade du signIn?"));
        Assert.That(lines[2], Does.StartWith("Tekniskt: ").And.Contain("getMe"));
    }

    [Test]
    public async Task A_login_whose_response_lacks_the_token_says_which_fields_it_has()
    {
        var plan = Plan("    login:\n      operation: signIn\n      body: { userName: Inloggad }\n      token: data.token\n");
        await using var session = await ApiSession.StartAsync(plan);

        var lines = session.StartFailure!.Split(Environment.NewLine);
        Assert.That(lines[0], Does.Contain("'data.token'"));
        Assert.That(lines[1], Does.Contain("apis.AuthApp.login.token"));
        Assert.That(lines[2], Does.Contain("accessToken"));
        var failure = await Caught.Failure(() => Run(session, plan, "GET /me"));
        Assert.That(failure.Message, Is.EqualTo(session.StartFailure));
    }

    [Test]
    public async Task A_login_the_app_refuses_names_the_status_and_where_to_change_it()
    {
        var plan = Plan("    login:\n      operation: getMe\n      token: name\n");   // kräver inloggning, så den nekas
        await using var session = await ApiSession.StartAsync(plan);

        var lines = session.StartFailure!.Split(Environment.NewLine);
        Assert.That(lines[0], Is.EqualTo("Inloggningen (getMe) gav 401."));
        Assert.That(lines[1], Does.Contain("apis.AuthApp.login.body").And.Contain("apis.AuthApp.settings"));
        Assert.That(lines[2], Is.EqualTo("Tekniskt: GET /me -> 401"));
    }

    [Test]
    public async Task A_token_in_testgen_yaml_wins_over_the_test_user()
    {
        var plan = Plan("", "environments:\n  local:\n    apis:\n      AuthApp: { token: '${env:AUTH_TOKEN}' }\n", name => name == "AUTH_TOKEN" ? "en-annan-token-1234" : null);
        await using var session = await ApiSession.StartAsync(plan);

        var unsure = (await Assert.ThrowsAsync<InconclusiveException>(() => Run(session, plan, "GET /me")))!;

        Assert.That(unsure.Message, Does.Contain("401").And.Not.Contain("testanvändaren godtogs inte"), "testanvändaren används inte när en token anges");
    }

    private const string LoginYaml = "    login:\n      operation: signIn\n      body: { userName: Inloggad }\n      token: accessToken\n";

    private static async Task<int> LoginsAsync(ApiSession session)
    {
        using var client = new HttpClient(session.AppHandler!, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
        return int.Parse(await client.GetStringAsync("/logins"));
    }

    [Test]
    public async Task A_401_after_the_session_ended_logs_in_again_and_the_call_is_sent_once_more()
    {
        var plan = Plan(LoginYaml);
        await using var session = await ApiSession.StartAsync(plan);
        await Run(session, plan, "POST /logout");   // avslutar sessionen som inloggningen gav

        await Run(session, plan, "GET /me");

        Assert.That(await LoginsAsync(session), Is.EqualTo(2), "en inloggning först och en efter utloggningen");
    }

    [Test]
    public async Task Parallel_calls_after_the_session_ended_share_one_new_login()
    {
        var plan = Plan(LoginYaml);
        await using var session = await ApiSession.StartAsync(plan);
        await Run(session, plan, "POST /logout");

        await Task.WhenAll(Run(session, plan, "GET /me"), Run(session, plan, "GET /named"), Run(session, plan, "GET /admin"), Run(session, plan, "GET /strict"));

        Assert.That(await LoginsAsync(session), Is.EqualTo(2));
    }

    [Test]
    public async Task A_call_that_is_denied_even_after_a_new_login_is_skipped_and_the_text_points_at_the_login_step()
    {
        var plan = Plan(LoginYaml);
        await using var session = await ApiSession.StartAsync(plan);

        var skipped = (await Assert.ThrowsAsync<InconclusiveException>(() => Run(session, plan, "GET /nope")))!;

        Assert.That(skipped.Message, Does.Contain("apis.AuthApp.login").And.Contain("ny inloggning").And.Not.Contain("environments."));
        Assert.That(await LoginsAsync(session), Is.EqualTo(2), "bara ett omförsök");
        Assert.That(File.ReadAllText(session.WriteResult()!), Does.Contain("test-user-rejected"));
    }

    [Test]
    public async Task The_login_operation_itself_is_not_retried_or_skipped_as_a_rejected_user()
    {
        var plan = Plan(LoginYaml);
        await using var session = await ApiSession.StartAsync(plan);

        foreach (var test in plan.Tests.Where(t => t.Call == "POST /login"))
            await session.RunTestAsync(test.Id, CancellationToken.None);   // negativa fall får 400 som väntat

        Assert.That(await LoginsAsync(session), Is.EqualTo(2), "fixturens inloggning och det giltiga signIn-fallet, inga omlogin");
    }

    [Test]
    public async Task Negative_cases_sent_as_the_test_user_are_skipped_like_valid_ones_and_never_fail()
    {
        var plan = Plan();
        await using var session = await ApiSession.StartAsync(plan);
        var echo = plan.Tests.Where(t => t.Call == "POST /echo").ToList();
        Assert.That(echo, Has.Count.GreaterThan(1), "ett giltigt och minst ett negativt fall");

        foreach (var test in echo)
        {
            var skipped = (await Assert.ThrowsAsync<InconclusiveException>(() => session.RunTestAsync(test.Id, CancellationToken.None)))!;
            Assert.That(skipped.Message, Does.Contain("testanvändaren"));
        }
    }

    [Test]
    public async Task A_503_from_an_app_in_the_test_process_says_the_app_lacks_a_service_and_names_the_blocked_host()
    {
        var plan = Plan();
        await using var session = await ApiSession.StartAsync(plan);

        var unsure = (await Assert.ThrowsAsync<InconclusiveException>(() => Run(session, plan, "GET /dependent")))!;

        Assert.That(unsure.Message, Does.Contain("saknar troligen en tjänst").And.Contain("apis.AuthApp.settings").And.Contain("payments.example.com").And.Contain("hosts"));
        Assert.That(unsure.Message, Does.Not.Contain("Kör igen när miljön är uppe"));
    }

    [Test]
    public void Claims_from_settings_become_both_short_and_long_claim_types()
    {
        var user = TestUser.Create(new Dictionary<string, string> { ["role"] = "Admin, Editor", ["tenant"] = "t1" });

        var principal = user.Principal();

        Assert.That(principal.IsInRole("Admin") && principal.IsInRole("Editor"), Is.True);
        Assert.That(principal.FindFirst("tenant")?.Value, Is.EqualTo("t1"));
        Assert.That(principal.FindFirst("sub")?.Value, Is.EqualTo("5c0b8a3e-0d3f-4f6e-9a57-3f1c2b7e8d10"));
        Assert.That(principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, Is.EqualTo("5c0b8a3e-0d3f-4f6e-9a57-3f1c2b7e8d10"));
        Assert.That(principal.Identity!.Name, Is.EqualTo("Test Testsson"));
    }

    [Test]
    public void Each_test_user_gets_its_own_random_token()
    {
        var a = TestUser.Create(new Dictionary<string, string>());
        var b = TestUser.Create(new Dictionary<string, string>());

        Assert.That(a.Token, Is.Not.EqualTo(b.Token).And.Length.GreaterThanOrEqualTo(32));
    }
}
