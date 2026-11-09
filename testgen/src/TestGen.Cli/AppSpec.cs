using System.Text.Json;
using System.Text.Json.Nodes;
using TestGen.Core.Projects;

namespace TestGen.Cli;

/// <summary>Hämtar specen en code-first-app serverar, genom att starta appen som <c>testgen views --probe</c> gör.</summary>
internal static class AppSpec
{
    public static readonly string[] Paths = ["/openapi/v1.json", "/swagger/v1/swagger.json"];

    private const long MaxBytes = 20 * 1024 * 1024;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Specen som JSON-text (formaterad), eller ett svenskt felmeddelande om appen inte startar eller inte serverar någon.</summary>
    public static async Task<(string? Json, string? Error)> FetchAsync(ProjectFacts api, CancellationToken ct)
    {
        AppProcess process;
        try { process = await AppProcess.StartAsync(api.Path, new Dictionary<string, string>(), api.RunEnvironment, StartTimeout, ct); }
        catch (IOException e) { return (null, $"{api.Name} startade inte. {e.Message}"); }
        await using (process)
        {
            using var client = new HttpClient { BaseAddress = new Uri(process.Url), Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = MaxBytes };
            foreach (var path in Paths)
            {
                try
                {
                    using var response = await client.GetAsync(path, ct);
                    if (!response.IsSuccessStatusCode) continue;
                    if (Format(await response.Content.ReadAsStringAsync(ct)) is { } json) return (json, null);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { /* nästa adress */ }
            }
        }
        return (null, $"{api.Name} startade men serverar ingen spec på {string.Join(" eller ", Paths)}.");
    }

    /// <summary>JSON med indrag, eller null om texten inte är en OpenAPI-spec.</summary>
    internal static string? Format(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject root && (root.ContainsKey("openapi") || root.ContainsKey("swagger")) ? root.ToJsonString(Indented) + "\n" : null;
        }
        catch (JsonException) { return null; }
    }
}
