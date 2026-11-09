namespace SagaPerception.TestGen.NUnit.Tests;

/// <summary>
/// ServicesApp läser PostgreSQL och Azure Storage ur sin Development-konfiguration (localhost, Azurite) och använder båda vid start.
/// testgen byter dem mot containrar, men aldrig ett värde som pekar på en riktig värd.
/// </summary>
public class ServicesAppTests
{
    private string _dir = "";
    private string _out = "";

    [SetUp]
    public void Up()
    {
        _dir = Directory.CreateTempSubdirectory("testgen-services-").FullName;
        _out = Directory.CreateTempSubdirectory("testgen-services-out-").FullName;
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ServicesApp"));
        File.WriteAllText(Path.Combine(_dir, "S.slnx"), $"<Solution><Project Path=\"{Path.Combine(app, "ServicesApp.csproj")}\" /></Solution>");
        File.Copy(Path.Combine(app, "servicesapp.openapi.yaml"), Path.Combine(_dir, "servicesapp.openapi.yaml"));
    }

    [TearDown]
    public void Down()
    {
        Directory.Delete(_dir, true);
        Directory.Delete(_out, true);
    }

    private ApiPlan Plan(string yaml = "")
    {
        File.WriteAllText(Path.Combine(_dir, "testgen.yaml"), "apis:\n  ServicesApp:\n    spec: servicesapp.openapi.yaml\n" + yaml);
        var plan = ApiPlan.Create(() => TestGenTarget.Resolve(_dir, null, "ServicesApp", null, testProjectDirectory: _out), "ServicesApp");
        Assert.That(plan.Error, Is.Null);
        return plan;
    }

    private static string IdOf(ApiPlan plan, string call) => plan.Tests.First(t => t.Call == call).Id;

    [Test]
    public void The_app_runs_in_its_development_environment_when_nothing_else_is_said() =>
        Assert.That(Plan().Target!.HostEnvironment, Is.EqualTo("Development"));

    [Test]
    public async Task A_storage_account_on_a_real_host_stops_the_app_and_says_what_to_do_without_starting_anything()
    {
        var plan = Plan("    settings:\n      Storage:ConnectionString: DefaultEndpointsProtocol=https;AccountName=prodlager;AccountKey=hemlig-nyckel;EndpointSuffix=core.windows.net\n");

        await using var session = await ApiSession.StartAsync(plan);

        Assert.That(session.NotReady, Is.Null, "inga containrar startas, så Docker behövs inte");
        Assert.That(session.StartFailure, Is.EqualTo(
            "Appen ServicesApp pekar på en riktig tjänst: Storage:ConnectionString (Azure Storage, prodlager). testgen ansluter inte till riktiga tjänster." + Environment.NewLine +
            "Gör så här: ange en lokal under apis.ServicesApp.settings i testgen.yaml (Storage:ConnectionString: <lokal värd>); testgen byter en lokal mot en container."));
        var failure = await Caught.Failure(() => session.RunTestAsync(IdOf(plan, "GET /ping"), CancellationToken.None));
        Assert.That(failure.Message, Is.EqualTo(session.StartFailure));
        Assert.That(failure.Message, Does.Not.Contain("hemlig-nyckel"));
    }

    [Test]
    public async Task A_database_on_a_real_host_stops_the_app_too()
    {
        var plan = Plan("    settings:\n      ConnectionStrings:Database: Host=db.example.com;Database=app;Username=a;Password=hemligt\n");

        await using var session = await ApiSession.StartAsync(plan);

        Assert.That(session.StartFailure, Does.StartWith("Appen ServicesApp pekar på en riktig tjänst: ConnectionStrings:Database (PostgreSQL, db.example.com).").And.Not.Contain("hemligt"));
    }

    [Test, Category("docker")]
    public async Task Both_services_are_replaced_by_containers_with_the_images_of_the_compose_file_and_the_app_starts()
    {
        File.WriteAllText(Path.Combine(_dir, "docker-compose.yml"),
            "services:\n  db:\n    image: postgres:17-alpine\n  storage:\n    image: mcr.microsoft.com/azure-storage/azurite:3.37.0\n  other:\n    image: ${OTHER_IMAGE}\n");
        var plan = Plan();

        await using var session = await ApiSession.StartAsync(plan);

        if (session.NotReady is { } notReady) Assert.Inconclusive(notReady);
        Assert.That(session.StartFailure, Is.Null);
        Assert.That(session.Containers!.ImageOf(ServiceKind.PostgreSql), Is.EqualTo("postgres:17-alpine"));
        Assert.That(session.Containers.ImageOf(ServiceKind.AzureStorage), Is.EqualTo("mcr.microsoft.com/azure-storage/azurite:3.37.0"));
        await session.RunTestAsync(IdOf(plan, "GET /ping"), CancellationToken.None);
        await session.RunTestAsync(IdOf(plan, "GET /where"), CancellationToken.None);
    }
}
