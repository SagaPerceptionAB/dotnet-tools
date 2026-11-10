using System.Text.Json;
using System.Text.RegularExpressions;

namespace SystemMap.Tests;

/// <summary>Kartan som JSON är en kontraktsfil: schemaVersion först, inga null, sorterade listor och inga sökvägar från den här datorn.</summary>
public partial class JsonContractTests
{
    private static async Task<string> SampleJson()
    {
        using var loaded = await SolutionLoader.LoadAsync(TestPaths.Solution, restore: true);
        var map = await CodeMapper.AnalyzeAsync(loaded.Solution, TestPaths.Solution, SpecReader.Load(TestPaths.Spec), loaded.Warnings);
        return CodeMapJson.Serialize(map);
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/]|^\\\\")]
    private static partial Regex WindowsPath();

    private static IEnumerable<JsonElement> Walk(JsonElement e)
    {
        yield return e;
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject()) foreach (var c in Walk(p.Value)) yield return c;
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray()) foreach (var c in Walk(i)) yield return c;
    }

    [Test]
    public async Task Json_starts_with_schemaVersion_and_has_no_null_values()
    {
        var json = await SampleJson();
        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.EnumerateObject().First().Name, Is.EqualTo("schemaVersion"));
        Assert.That(doc.RootElement.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(3));
        Assert.That(Walk(doc.RootElement).Count(e => e.ValueKind == JsonValueKind.Null), Is.Zero, "null utelämnas");
    }

    [Test]
    public async Task Json_has_no_absolute_paths_or_times()
    {
        var json = await SampleJson();
        using var doc = JsonDocument.Parse(json);
        var strings = Walk(doc.RootElement).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        Assert.That(strings.Where(s => WindowsPath().IsMatch(s)), Is.Empty);
        Assert.That(json, Does.Not.Contain(Path.GetDirectoryName(TestPaths.Solution)!.Replace("\\", "\\\\")));
        Assert.That(doc.RootElement.GetProperty("solution").GetString(), Is.EqualTo("Shop.slnx"), "bara filnamnet");
    }

    [Test]
    public async Task Json_lists_are_sorted_on_stable_ids()
    {
        using var doc = JsonDocument.Parse(await SampleJson());
        var integrations = doc.RootElement.GetProperty("integrations").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();
        Assert.That(integrations, Is.Ordered.Using<string>(StringComparer.Ordinal));
        var nodes = doc.RootElement.GetProperty("overview").GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()!).ToList();
        Assert.That(nodes, Is.Ordered.Using<string>(StringComparer.Ordinal));
        var entries = doc.RootElement.GetProperty("entryPoints").EnumerateArray().Select(e => e.GetProperty("path").GetString()! + " " + e.GetProperty("method").GetString()).ToList();
        Assert.That(entries, Is.Ordered.Using<string>(StringComparer.Ordinal));
    }
    [Test]
    public async Task Json_with_hidden_parts_keeps_the_contract_and_says_what_is_hidden()
    {
        using var loaded = await SolutionLoader.LoadAsync(TestPaths.Solution, restore: true);
        var map = await CodeMapper.AnalyzeAsync(loaded.Solution, TestPaths.Solution, SpecReader.Load(TestPaths.Spec), loaded.Warnings);
        var overview = CodeMapOverview.Build(map);
        var hidden = CodeMapOverview.ResolveHidden(overview, ["OrderService", "integration:ShopDatabase"]);
        using var doc = JsonDocument.Parse(CodeMapJson.Serialize(CodeMapOverview.Hide(map, hidden), CodeMapOverview.Hide(overview, hidden), hidden));

        Assert.That(doc.RootElement.EnumerateObject().First().Name, Is.EqualTo("schemaVersion"));
        Assert.That(Walk(doc.RootElement).Count(e => e.ValueKind == JsonValueKind.Null), Is.Zero, "null utelämnas");
        Assert.That(doc.RootElement.GetProperty("hidden").EnumerateArray().Select(e => e.GetString()),
            Is.EqualTo(new[] { "class:ShopApplication/OrderService", "int:db:ShopDatabase" }), "listan säger vad som är dolt, sorterad");
        var nodes = doc.RootElement.GetProperty("overview").GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()!).ToList();
        Assert.That(nodes, Is.Ordered.Using<string>(StringComparer.Ordinal).And.Not.Contain("class:ShopApplication/OrderService").And.Not.Contain("int:db:ShopDatabase"));
        var links = doc.RootElement.GetProperty("overview").GetProperty("links").EnumerateArray().ToList();
        Assert.That(links.Select(l => l.GetProperty("to").GetString()!), Has.None.StartWith("op:db:ShopDatabase"), "pilarna till databasens rader är borta");
        Assert.That(links.Select(l => l.GetProperty("from").GetString() + ">" + l.GetProperty("to").GetString()),
            Does.Contain("method:ShopApi/OrdersController.Create>method:ShopInfrastructure/CustomerRepository.GetAsync"), "controllern anropar nu klassen efter den dolda");
        Assert.That(doc.RootElement.GetProperty("integrations").EnumerateArray().Select(i => i.GetProperty("id").GetString()), Has.None.EqualTo("db:ShopDatabase"));
    }
}
