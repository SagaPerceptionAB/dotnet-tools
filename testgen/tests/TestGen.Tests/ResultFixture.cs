using System.Text.Json.Nodes;

namespace TestGen.Tests;

/// <summary>
/// En liten solution med ett testprojekt för testgen och tre resultatfiler som <c>dotnet test</c> skulle ha skrivit:
/// två API-körningar (en tidigare, en senare) och en UI-körning. Delas av webbtesterna och tillgänglighetstesterna.
/// </summary>
internal static class ResultFixture
{
    public const string EarlierApiFile = "Shop.Tests.earlier.json";
    public const string LaterApiFile = "Shop.Tests.json";
    public const string UiFile = "Shop.UiTests.json";

    /// <summary>Skriver solutionen under <paramref name="root"/> och returnerar sökvägen till den.</summary>
    public static string Create(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Tests"));
        File.WriteAllText(Path.Combine(root, "Shop.slnx"), "<Solution><Project Path=\"Tests/Tests.csproj\" /></Solution>");
        File.WriteAllText(Path.Combine(root, "Tests", "Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="NUnit" Version="4.0.0" />
                <PackageReference Include="SagaPerception.Testing.NUnit" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        var results = Path.Combine(root, "Tests", "TestResults", "testgen");
        Directory.CreateDirectory(results);
        Write(results, EarlierApiFile, "api", "Shop.Tests", "2026-11-01T10:00:00.000+01:00",
            ("T-001", "api:createOrder:valid", "passed"), ("T-002", "api:createOrder:missing:quantity", "failed"), ("T-003", "api:getOrder:valid", "failed"));
        Write(results, LaterApiFile, "api", "Shop.Tests", "2026-11-02T10:00:00.000+01:00",
            ("T-001", "api:createOrder:valid", "passed"), ("T-002", "api:createOrder:missing:quantity", "failed"), ("T-003", "api:getOrder:valid", "passed"),
            ("T-004", "api:listOrders:valid", "failed"));
        Write(results, UiFile, "ui", "Shop.UiTests", "2026-11-02T11:00:00.000+01:00",
            ("U-001", "ui:orders:create:valid", "passed"), ("U-002", "ui:orders:create:missing:quantity", "skipped"));
        File.WriteAllText(Path.Combine(results, "not-a-result.json"), "{ \"hello\": 1 }");
        return Path.Combine(root, "Shop.slnx");
    }

    private static void Write(string directory, string file, string layer, string project, string startedAt, params (string Id, string Key, string Outcome)[] tests)
    {
        var failed = tests.Count(t => t.Outcome == "failed");
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["startedAt"] = startedAt,
            ["layer"] = layer,
            ["project"] = project,
            ["api"] = "http://fake.test",
            ["seed"] = 1,
            ["totals"] = new JsonObject
            {
                ["tests"] = tests.Length,
                ["passed"] = tests.Count(t => t.Outcome == "passed"),
                ["failed"] = failed,
                ["skipped"] = tests.Count(t => t.Outcome == "skipped"),
                ["warnings"] = 0,
            },
            ["verdict"] = failed == 0 ? "Allt gick bra." : $"{failed} tester visar ett problem.",
            ["causes"] = failed == 0 ? new JsonArray() : new JsonArray(new JsonObject
            {
                ["code"] = "system",
                ["name"] = "Fel i systemet",
                ["count"] = failed,
                ["testIds"] = new JsonArray(tests.Where(t => t.Outcome == "failed").Select(t => (JsonNode)t.Id).ToArray()),
            }),
            ["tests"] = new JsonArray(tests.Select(t => (JsonNode)new JsonObject
            {
                ["id"] = t.Id,
                ["key"] = t.Key,
                ["group"] = layer == "ui" ? "Beställningar" : "Order",
                ["method"] = "POST",
                ["path"] = "/orders",
                ["title"] = $"Test {t.Id}",
                ["outcome"] = t.Outcome,
                ["cause"] = t.Outcome == "failed" ? "system" : null,
                ["causeName"] = t.Outcome == "failed" ? "Fel i systemet" : null,
                ["explanation"] = t.Outcome == "failed" ? "Systemet svarade 500 i stället för 201." : null,
            }).ToArray()),
        };
        File.WriteAllText(Path.Combine(directory, file), root.ToJsonString(new() { WriteIndented = true }));
    }
}
