namespace SagaPerception.Testing.NUnit.Tests;

/// <summary>Avbildningen för varje slag: repots compose-fil om den har en tjänst av samma slag, annars standardavbildningen. Docker-testerna startar riktiga containrar.</summary>
public class ServiceCatalogTests
{
    private static readonly string[] Compose =
    [
        "postgres:17-alpine",
        "mcr.microsoft.com/azure-storage/azurite:3.37.0",
        "mcr.microsoft.com/mssql/server:2019-latest",
        "redis:6",
        "nginx:1",
    ];

    [TestCase("PostgreSql", "postgres:17-alpine")]
    [TestCase("AzureStorage", "mcr.microsoft.com/azure-storage/azurite:3.37.0")]
    [TestCase("SqlServer", "mcr.microsoft.com/mssql/server:2019-latest")]
    [TestCase("Redis", "redis:6")]
    public void The_image_of_the_same_kind_in_the_compose_file_is_used(string kind, string expected) =>
        Assert.That(ServiceCatalog.ImageFor(Enum.Parse<ServiceKind>(kind), Compose), Is.EqualTo(expected));

    [Test]
    public void Without_a_service_of_the_same_kind_the_default_image_is_used()
    {
        foreach (var kind in Enum.GetValues<ServiceKind>())
            Assert.That(ServiceCatalog.ImageFor(kind, ["nginx:1", "rabbitmq:3"]), Is.EqualTo(ServiceCatalog.DefaultImage(kind)));
    }

    [Test]
    public void A_postgres_like_image_with_another_name_is_not_postgres() =>
        Assert.That(ServiceCatalog.ImageFor(ServiceKind.PostgreSql, ["postgrest/postgrest:v12", "bitnami/postgresql:16"]), Is.EqualTo(ServiceCatalog.DefaultImage(ServiceKind.PostgreSql)));

    [Test, Category("docker")]
    public async Task Containers_for_postgres_and_azurite_start_together_from_the_compose_images_and_give_values_in_the_same_form()
    {
        ServiceContainers containers;
        try { containers = await ServiceContainers.StartAsync([ServiceKind.PostgreSql, ServiceKind.AzureStorage], Compose); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Assert.Inconclusive("Starta Docker för att köra det här testet. " + e.Message);
            return;
        }

        await using (containers)
        {
            Assert.That(containers.ImageOf(ServiceKind.PostgreSql), Is.EqualTo("postgres:17-alpine"));
            Assert.That(containers.ImageOf(ServiceKind.AzureStorage), Is.EqualTo("mcr.microsoft.com/azure-storage/azurite:3.37.0"));
            var postgres = ServiceDiscovery.Classify("ConnectionStrings:Database", containers.ValueOf(ServiceKind.PostgreSql));
            Assert.That((postgres?.Kind, postgres?.Local), Is.EqualTo((ServiceKind.PostgreSql, true)), "en anslutningssträng mot en anslutningssträng");
            var storage = ServiceDiscovery.Classify("Photos:ConnectionString", containers.ValueOf(ServiceKind.AzureStorage));
            Assert.That((storage?.Kind, storage?.Local), Is.EqualTo((ServiceKind.AzureStorage, true)));
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(postgres!.Host, int.Parse(System.Text.RegularExpressions.Regex.Match(containers.ValueOf(ServiceKind.PostgreSql), @"Port=(\d+)").Groups[1].Value));
            Assert.That(tcp.Connected, Is.True);
        }
    }

    [Test, Category("docker")]
    public async Task Redis_gives_host_and_port()
    {
        ServiceContainers containers;
        try { containers = await ServiceContainers.StartAsync([ServiceKind.Redis], []); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Assert.Inconclusive("Starta Docker (och nå registret) för att köra det här testet. " + e.Message);
            return;
        }

        await using (containers)
        {
            var found = ServiceDiscovery.Classify("ConnectionStrings:Cache", containers.ValueOf(ServiceKind.Redis));
            Assert.That((found?.Kind, found?.Local), Is.EqualTo((ServiceKind.Redis, true)), "värd:port mot värd:port");
        }
    }
}
