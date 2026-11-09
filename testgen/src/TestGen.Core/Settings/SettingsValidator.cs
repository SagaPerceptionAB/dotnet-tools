using TestGen.Core.Run;
using YamlDotNet.RepresentationModel;

namespace TestGen.Core.Settings;

/// <summary>Ett fel i testgen.yaml med raden det gäller.</summary>
internal sealed record SettingsProblem(long Line, string Message, string? Tip = null);

/// <summary>Kontrollerar testgen.yaml mot modellen innan den läses in: okända nycklar (med förslag), typer och hemligheter i klartext.</summary>
internal static class SettingsValidator
{
    private static readonly string[] RootKeys = ["seed", "locale", "apis", "apps", "hosts", "ui", "environments"];
    internal static readonly string[] UiChecks = ["accessibility", "screenshots"];
    private static readonly string[] UiKeys = ["checks", "trace"];
    private static readonly string[] ApiKeys = ["spec", "excel", "environment", "skipOperations", "overrides", "bindings", "settings", "auth", "login"];
    private static readonly string[] AppKeys = ["environment", "settings", "auth"];
    private static readonly string[] AuthKeys = ["claims"];
    private static readonly string[] LoginKeys = ["operation", "body", "token"];
    private static readonly string[] EnvironmentKeys = ["readOnly", "apis", "apps"];
    private static readonly string[] EndpointKeys = ["url", "token", "headers", "timeout"];

    private const string SecretTip = "Skriv hemligheten som ${env:NAMN}, t.ex. token: ${env:SHOP_TEST_TOKEN}, och sätt variabeln i miljön eller som en hemlighet i CI.";

    /// <summary>Alla fel i filen, i radordning. Kastar <see cref="YamlDotNet.Core.YamlException"/> om filen inte är YAML.</summary>
    public static List<SettingsProblem> Validate(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        var problems = new List<SettingsProblem>();
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" }) return problems;
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            return [new(stream.Documents[0].RootNode.Start.Line, "filen ska börja med nycklar som seed, apis och environments.")];

        Entries(root, RootKeys, "i filen", problems, (key, value) =>
        {
            switch (key)
            {
                case "seed": Integer(value, "seed", problems, min: int.MinValue); break;
                case "locale": Text(value, "locale", problems); break;
                case "apis": Map(value, "apis", problems, (name, api) => Api(api, $"apis.{name}", problems)); break;
                case "apps": Map(value, "apps", problems, (name, app) => App(app, $"apps.{name}", problems)); break;
                case "hosts": Hosts(value, problems); break;
                case "ui": Ui(value, problems); break;
                case "environments": Map(value, "environments", problems, (name, env) => Environment(env, $"environments.{name}", problems)); break;
            }
        });
        return problems.OrderBy(p => p.Line).ToList();
    }

    private static void Api(YamlNode node, string path, List<SettingsProblem> problems)
    {
        if (node is YamlScalarNode { Value: null or "" }) return;
        if (node is not YamlMappingNode map) { problems.Add(new(node.Start.Line, $"{path} ska vara en uppsättning nycklar ({string.Join(", ", ApiKeys)}).")); return; }
        Entries(map, ApiKeys, $"under {path}", problems, (key, value) =>
        {
            switch (key)
            {
                case "spec": Text(value, $"{path}.spec", problems); break;
                case "excel": Text(value, $"{path}.excel", problems); break;
                case "environment": Text(value, $"{path}.environment", problems); break;
                case "skipOperations":
                    if (value is not YamlSequenceNode items || items.Any(i => i is not YamlScalarNode))
                        problems.Add(new(value.Start.Line, $"{path}.skipOperations ska vara en lista med operationId, t.ex. [getOrder]."));
                    break;
                case "overrides": Map(value, $"{path}.overrides", problems, (op, fields) => Map(fields, $"{path}.overrides.{op}", problems, (_, _) => { })); break;
                case "bindings": Map(value, $"{path}.bindings", problems, (op, fields) => Map(fields, $"{path}.bindings.{op}", problems, (_, v) => Text(v, "värdet", problems))); break;
                case "settings": ProcessSettings(value, $"{path}.settings", problems); break;
                case "auth": Auth(value, $"{path}.auth", problems); break;
                case "login": Login(value, $"{path}.login", problems); break;
            }
        });
    }

    private static void Auth(YamlNode node, string path, List<SettingsProblem> problems)
    {
        if (node is YamlScalarNode { Value: null or "" }) return;
        if (node is not YamlMappingNode map) { problems.Add(new(node.Start.Line, $"{path} ska vara en uppsättning nycklar ({string.Join(", ", AuthKeys)}).")); return; }
        Entries(map, AuthKeys, $"under {path}", problems, (_, value) => Map(value, $"{path}.claims", problems, (type, v) => Text(v, $"{path}.claims.{type}", problems)));
    }

    private static void Login(YamlNode node, string path, List<SettingsProblem> problems)
    {
        if (node is YamlScalarNode { Value: null or "" }) return;
        if (node is not YamlMappingNode map) { problems.Add(new(node.Start.Line, $"{path} ska vara en uppsättning nycklar ({string.Join(", ", LoginKeys)}).")); return; }
        Entries(map, LoginKeys, $"under {path}", problems, (key, value) =>
        {
            if (key == "body") Map(value, $"{path}.body", problems, (_, _) => { });
            else Text(value, $"{path}.{key}", problems);
        });
        foreach (var required in new[] { "operation", "token" })
            if (!map.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == required))
                problems.Add(new(map.Start.Line, $"{path} saknar {required}."));
    }

    private static void App(YamlNode node, string path, List<SettingsProblem> problems)
    {
        if (node is YamlScalarNode { Value: null or "" }) return;
        if (node is not YamlMappingNode map) { problems.Add(new(node.Start.Line, $"{path} ska vara en uppsättning nycklar ({string.Join(", ", AppKeys)}).")); return; }
        Entries(map, AppKeys, $"under {path}", problems, (key, value) =>
        {
            switch (key)
            {
                case "environment": Text(value, $"{path}.environment", problems); break;
                case "auth": Auth(value, $"{path}.auth", problems); break;
                default: ProcessSettings(value, $"{path}.settings", problems); break;
            }
        });
    }

    /// <summary>Konfiguration för appen i testprocessen: nycklar som <c>Auth:SigningKey</c> med text som värde.</summary>
    private static void ProcessSettings(YamlNode node, string path, List<SettingsProblem> problems) =>
        Map(node, path, problems, (key, value) => Text(value, $"{path}.{key}", problems));

    private static void Environment(YamlNode node, string path, List<SettingsProblem> problems)
    {
        if (node is YamlScalarNode { Value: null or "" }) return;
        if (node is not YamlMappingNode map) { problems.Add(new(node.Start.Line, $"{path} ska vara en uppsättning nycklar ({string.Join(", ", EnvironmentKeys)}).")); return; }
        Entries(map, EnvironmentKeys, $"under {path}", problems, (key, value) =>
        {
            switch (key)
            {
                case "readOnly":
                    if (value is not YamlScalarNode { Value: { } b } || !bool.TryParse(b, out _))
                        problems.Add(new(value.Start.Line, $"{path}.readOnly ska vara true eller false."));
                    break;
                case "apis":
                case "apps":
                    Map(value, $"{path}.{key}", problems, (project, endpoint) => Endpoint(endpoint, $"{path}.{key}.{project}", problems));
                    break;
            }
        });
    }

    private static void Endpoint(YamlNode node, string path, List<SettingsProblem> problems)
    {
        if (node is YamlScalarNode { Value: null or "" }) return;
        if (node is not YamlMappingNode map) { problems.Add(new(node.Start.Line, $"{path} ska vara en uppsättning nycklar ({string.Join(", ", EndpointKeys)}).")); return; }
        Entries(map, EndpointKeys, $"under {path}", problems, (key, value) =>
        {
            switch (key)
            {
                case "url":
                    if (Text(value, $"{path}.url", problems) is { } url && !EnvVars.HasPlaceholder(url)
                        && !(Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
                        problems.Add(new(value.Start.Line, $"{path}.url '{url}' är inte en giltig http/https-adress, t.ex. https://shop-test.example.com."));
                    break;
                case "token":
                    if (Text(value, $"{path}.token", problems) is { Length: > 0 } token && !EnvVars.HasPlaceholder(token))
                        problems.Add(new(value.Start.Line, $"{path}.token står i klartext. Hemligheter får bara komma från miljövariabler.", SecretTip));
                    break;
                case "headers":
                    Map(value, $"{path}.headers", problems, (name, header) =>
                    {
                        if (Text(header, $"{path}.headers.{name}", problems) is { Length: > 0 } v && Secrets.LooksSecret(name) && !EnvVars.HasPlaceholder(v))
                            problems.Add(new(header.Start.Line, $"{path}.headers.{name} ser ut som en hemlighet och står i klartext. Hemligheter får bara komma från miljövariabler.", SecretTip));
                    });
                    break;
                case "timeout": Integer(value, $"{path}.timeout", problems, min: 1); break;
            }
        });
    }

    private static void Ui(YamlNode node, List<SettingsProblem> problems)
    {
        if (node is YamlScalarNode { Value: null or "" }) return;
        if (node is not YamlMappingNode map) { problems.Add(new(node.Start.Line, $"ui ska vara en uppsättning nycklar ({string.Join(", ", UiKeys)}).")); return; }
        Entries(map, UiKeys, "under ui", problems, (key, value) =>
        {
            if (key == "trace")
            {
                if (value is not YamlScalarNode { Value: { } b } || !bool.TryParse(b, out _)) problems.Add(new(value.Start.Line, "ui.trace ska vara true eller false."));
            }
            else if (value is not YamlSequenceNode items || items.Any(i => i is not YamlScalarNode { Value: { } v } || !UiChecks.Contains(v)))
                problems.Add(new(value.Start.Line, $"ui.checks ska vara en lista med {string.Join(" och ", UiChecks)}, t.ex. [accessibility]."));
        });
    }

    private static void Hosts(YamlNode node, List<SettingsProblem> problems) =>
        Map(node, "hosts", problems, (host, value) =>
        {
            if (host.Contains("://") || host.Contains('/'))
                problems.Add(new(value.Start.Line, $"hosts.{host}: skriv bara värdnamnet, t.ex. api.example-payments.com."));
            if (value is not YamlScalarNode { Value: { } v } || HostPolicy.Parse(v) is null)
                problems.Add(new(value.Start.Line, $"hosts.{host} ska vara block, allow eller mock."));
        });

    /// <summary>Går igenom nycklarna i en uppsättning och anmäler okända, med förslag. Kända skickas vidare med sitt rätta skrivsätt.</summary>
    private static void Entries(YamlMappingNode map, string[] allowed, string where, List<SettingsProblem> problems, Action<string, YamlNode> known)
    {
        foreach (var (keyNode, value) in map.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? "";
            var match = allowed.FirstOrDefault(a => a == key);
            if (match is null)
            {
                var suggestion = Suggest.Closest(key, allowed);
                problems.Add(new(keyNode.Start.Line, $"okänd nyckel '{key}' {where}." + (suggestion is null ? $" Giltiga nycklar: {string.Join(", ", allowed)}." : $" Menade du {suggestion}?")));
                continue;
            }
            known(match, value);
        }
    }

    private static void Map(YamlNode node, string path, List<SettingsProblem> problems, Action<string, YamlNode> each)
    {
        if (node is YamlScalarNode { Value: null or "" }) return;
        if (node is not YamlMappingNode map) { problems.Add(new(node.Start.Line, $"{path} ska vara en uppsättning med nycklar.")); return; }
        foreach (var (key, value) in map.Children) each((key as YamlScalarNode)?.Value ?? "", value);
    }

    private static string? Text(YamlNode node, string path, List<SettingsProblem> problems)
    {
        if (node is YamlScalarNode scalar) return scalar.Value ?? "";
        problems.Add(new(node.Start.Line, $"{path} ska vara text."));
        return null;
    }

    private static void Integer(YamlNode node, string path, List<SettingsProblem> problems, int min)
    {
        if (node is not YamlScalarNode { Value: { } v } || !int.TryParse(v, out var n) || n < min)
            problems.Add(new(node.Start.Line, min > 0 ? $"{path} ska vara ett positivt heltal (sekunder)." : $"{path} ska vara ett heltal."));
    }
}
