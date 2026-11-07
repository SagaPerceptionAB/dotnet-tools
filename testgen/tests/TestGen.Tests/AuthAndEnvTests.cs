using ClosedXML.Excel;
using TestGen.Cli;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

public class ArgsTests
{
    [Test]
    public void Repeated_headers_are_collected_in_order_and_Get_returns_the_last()
    {
        var a = CommandArgs.Parse("run", new[] { "--header", "A: 1", "--api", "http://x", "--header=B: 2" }, new[] { "header", "api" });
        Assert.That(a.GetAll("header"), Is.EqualTo(new[] { "A: 1", "B: 2" }));
        Assert.That(a.Get("header"), Is.EqualTo("B: 2"));
        Assert.That(a.Get("api"), Is.EqualTo("http://x"));
        Assert.That(a.GetAll("saknas"), Is.Empty);
    }
}

public class EnvVarsTests
{
    private static string? Env(string n) => n == "T" ? "hemlig" : null;

    [Test]
    public void Replaces_environment_variable() =>
        Assert.That(EnvVars.Expand("Bearer ${env:T}!", "--token", Env), Is.EqualTo("Bearer hemlig!"));

    [Test]
    public void Missing_variable_gives_error_with_its_name()
    {
        var e = Assert.Throws<ArgumentException>(() => EnvVars.Expand("${env:SAKNAS}", "--token", Env))!;
        Assert.That(e.Message, Does.Contain("SAKNAS").And.Contain("--token"));
    }

    [Test]
    public void Text_without_placeholders_is_left_untouched() =>
        Assert.That(EnvVars.Expand("${kund.id}", "x", Env), Is.EqualTo("${kund.id}"));
}

public class AuthSchemesTests
{
    private static ApiSpec Spec(params SecuritySchemeInfo[] schemes) => new(Array.Empty<OperationInfo>(), schemes);

    [Test]
    public void Bearer_gives_Authorization_Bearer() =>
        Assert.That(AuthSchemes.TokenHeader(Spec(new SecuritySchemeInfo("b", "bearer", "Authorization")), "abc"),
            Is.EqualTo(KeyValuePair.Create("Authorization", "Bearer abc")));

    [Test]
    public void ApiKey_in_header_gives_that_header() =>
        Assert.That(AuthSchemes.TokenHeader(Spec(new SecuritySchemeInfo("k", "apiKey", "X-Api-Key")), "abc"),
            Is.EqualTo(KeyValuePair.Create("X-Api-Key", "abc")));

    [Test]
    public void Without_schemes_default_is_Authorization_Bearer() =>
        Assert.That(AuthSchemes.TokenHeader(Spec(), "abc"), Is.EqualTo(KeyValuePair.Create("Authorization", "Bearer abc")));

    private static string WriteSpec(string security)
    {
        var path = Path.Combine(Path.GetTempPath(), $"spec-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, $$"""
            openapi: 3.0.3
            info: { title: t, version: '1' }
            {{security}}
            paths:
              /a:
                get:
                  operationId: getA
                  responses: { '200': { description: ok } }
            components:
              securitySchemes:
                bearerAuth: { type: http, scheme: bearer }
                keyAuth: { type: apiKey, in: header, name: X-Api-Key }
            """);
        return path;
    }

    [Test]
    public void Requirement_without_header_gives_note_but_optional_requirement_does_not()
    {
        var required = OpenApiLoader.LoadSpec(WriteSpec("security: [ { bearerAuth: [] } ]"));
        Assert.That(AuthSchemes.MissingLoginNote(required, Array.Empty<string>()), Does.StartWith("Obs:").And.Contain("bearer"));
        Assert.That(AuthSchemes.MissingLoginNote(required, new[] { "authorization" }), Is.Null);

        var optional = OpenApiLoader.LoadSpec(WriteSpec("security: [ {}, { bearerAuth: [] } ]"));
        Assert.That(AuthSchemes.MissingLoginNote(optional, Array.Empty<string>()), Is.Null);
    }

    [Test]
    public void ApiKey_schemes_give_secret_header_names()
    {
        var spec = OpenApiLoader.LoadSpec(WriteSpec("security: [ { keyAuth: [] } ]"));
        Assert.That(AuthSchemes.SecretHeaderNames(spec), Is.EqualTo(new[] { "X-Api-Key" }));
        Assert.That(AuthSchemes.MissingLoginNote(spec, Array.Empty<string>()), Does.Contain("X-Api-Key"));
    }
}

public class HeaderMaskTests
{
    [Test]
    public void Masks_standard_secrets_but_not_ordinary_headers()
    {
        var masked = HeaderMask.Default.Mask(new Dictionary<string, string>
        {
            ["authorization"] = "Bearer hemlig", ["Cookie"] = "s=1", ["X-Env"] = "test",
        });
        Assert.That(masked["Authorization"], Is.EqualTo("Bearer ***"));
        Assert.That(masked["Cookie"], Is.EqualTo("***"));
        Assert.That(masked["X-Env"], Is.EqualTo("test"));
    }

    [TestCase("Basic dXNlcjpoZW1saWc=", "Basic ***")]
    [TestCase("Token hemlig-1", "Token ***")]
    [TestCase("ApiKey hemlig-1", "ApiKey ***")]
    [TestCase("bearer hemlig-1", "bearer ***")]
    [TestCase("hemlig-1", "***")]               // inget känt schema: allt döljs
    [TestCase("Bearer", "***")]                 // schemaord utan värde
    [TestCase("Okänt hemlig-1", "***")]
    public void Authorization_keeps_known_schemes_but_never_the_value(string raw, string expected)
    {
        var mask = HeaderMask.For(new Dictionary<string, string> { ["Authorization"] = raw });
        var shown = mask.Value("authorization", raw);
        Assert.That(shown, Is.EqualTo(expected));
        Assert.That(mask.Format(new Dictionary<string, string> { ["Authorization"] = raw }), Does.Not.Contain("hemlig-1").And.Not.Contain("dXNlcjpoZW1saWc="));
        // Scrub döljer värdet även när det ekas i en text, men inte schemaordet.
        Assert.That(mask.Scrub("fel: " + raw), Does.Not.Contain("hemlig-1").And.Not.Contain("dXNlcjpoZW1saWc="));
    }

    [Test]
    public void Scheme_is_kept_only_for_Authorization_not_for_other_secret_headers()
    {
        var mask = new HeaderMask(new[] { "X-Api-Key" });
        Assert.That(mask.Value("X-Api-Key", "Basic hemlig-1"), Is.EqualTo("***"));
        Assert.That(mask.Value("Cookie", "Token hemlig-1"), Is.EqualTo("***"));
    }

    [Test]
    public void Extra_names_are_masked_and_Format_is_ready_for_output()
    {
        var mask = new HeaderMask(new[] { "X-Api-Key" });
        Assert.That(mask.Format(new Dictionary<string, string> { ["X-Api-Key"] = "k", ["X-Env"] = "test" }),
            Is.EqualTo("X-Api-Key: ***, X-Env: test"));
    }

    [Test]
    public void Scrub_hides_known_values_even_without_Bearer()
    {
        var mask = HeaderMask.For(new Dictionary<string, string> { ["Authorization"] = "Bearer hemlig" });
        Assert.That(mask.Scrub("svar: Bearer hemlig / hemlig"), Is.EqualTo("svar: *** / ***"));
    }
}

public class RunSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("testgen-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private static string? Env(string n) => n == "T" ? "hemlig" : null;

    private string Workbook(bool withSheet = true, params string[][] rows)
    {
        var path = Path.Combine(_dir, "t.xlsx");
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Tester").Cell(1, 1).Value = "Scenario";
        if (withSheet)
        {
            var ws = wb.AddWorksheet("Miljöer");
            string[] heads = { "Miljö", "Inställning", "Värde" };
            for (var c = 0; c < 3; c++) ws.Cell(1, c + 1).Value = heads[c];
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < 3; c++) ws.Cell(r + 2, c + 1).Value = rows[r][c];
        }
        wb.SaveAs(path);
        return path;
    }

    private static readonly string[][] Rows =
    {
        new[] { "lokal", "api", "http://localhost:5080" },
        new[] { "lokal", "header", "X-Env: lokal" },
        new[] { "lokal", "token", "${env:T}" },
        new[] { "lokal", "timeout", "5" },
        new[] { "test", "api", "https://test.example.se" },
    };

    [Test]
    public void Reads_environment_from_the_sheet()
    {
        var s = RunSettings.Resolve(null, Array.Empty<string>(), null, null, Workbook(true, Rows), "LOKAL", Env);
        Assert.That(s.Api, Is.EqualTo("http://localhost:5080"));
        Assert.That(s.Headers["x-env"], Is.EqualTo("lokal"));
        Assert.That(s.Token, Is.EqualTo("hemlig"));
        Assert.That(s.TimeoutSeconds, Is.EqualTo(5));
        Assert.That(s.EnvName, Is.EqualTo("lokal"));
    }

    [Test]
    public void Command_line_wins_over_the_environment()
    {
        var s = RunSettings.Resolve("http://annan:1", new[] { "x-env: cli" }, "cli-token", "9", Workbook(true, Rows), "lokal", Env);
        Assert.That(s.Api, Is.EqualTo("http://annan:1"));
        Assert.That(s.Headers["X-Env"], Is.EqualTo("cli"));
        Assert.That(s.Token, Is.EqualTo("cli-token"));
        Assert.That(s.TimeoutSeconds, Is.EqualTo(9));
    }

    [Test]
    public void Unknown_environment_lists_the_available_ones()
    {
        var e = Assert.Throws<ArgumentException>(() =>
            RunSettings.Resolve(null, Array.Empty<string>(), null, null, Workbook(true, Rows), "prod", Env))!;
        Assert.That(e.Message, Does.Contain("prod").And.Contain("lokal").And.Contain("test"));
    }

    [Test]
    public void Env_without_excel_gives_a_clear_error() =>
        Assert.That(Assert.Throws<ArgumentException>(() =>
            RunSettings.Resolve(null, Array.Empty<string>(), null, null, null, "lokal", Env))!.Message, Does.Contain("--excel"));

    [Test]
    public void Old_file_without_sheet_works_without_env_but_fails_with_env()
    {
        var path = Workbook(false);
        Assert.That(EnvironmentSheet.Read(path), Is.Null);
        Assert.That(RunSettings.Resolve("http://x", Array.Empty<string>(), null, null, path, null, Env).Api, Is.EqualTo("http://x"));
        Assert.That(Assert.Throws<ArgumentException>(() =>
            RunSettings.Resolve(null, Array.Empty<string>(), null, null, path, "lokal", Env))!.Message, Does.Contain("Miljöer"));
    }

    [Test]
    public void Missing_api_and_unknown_setting_give_clear_errors()
    {
        Assert.That(Assert.Throws<ArgumentException>(() =>
            RunSettings.Resolve(null, Array.Empty<string>(), null, null, null, null, Env))!.Message, Does.Contain("--api"));
        var bad = Workbook(true, new[] { "lokal", "url", "x" });
        Assert.That(Assert.Throws<ExcelValidationException>(() => EnvironmentSheet.Read(bad))!.Message, Does.Contain("Miljöer!B2 (Inställning)").And.Contain("url"));
    }

    [Test]
    public void Missing_environment_variable_in_header_names_the_variable()
    {
        var e = Assert.Throws<ArgumentException>(() =>
            RunSettings.Resolve("http://x", new[] { "Authorization: Bearer ${env:SAKNAS}" }, null, null, null, null, Env))!;
        Assert.That(e.Message, Does.Contain("SAKNAS"));
    }

    [Test]
    public void Header_with_env_becomes_secret_and_the_value_is_masked()
    {
        var s = RunSettings.Resolve("http://x", new[] { "X-Custom: ${env:T}", "X-Plain: a" }, null, null, null, null, Env);
        var mask = HeaderMask.For(s.Headers, s.SecretHeaderNames);
        Assert.That(mask.Format(s.Headers), Is.EqualTo("X-Custom: ***, X-Plain: a"));
    }
}
