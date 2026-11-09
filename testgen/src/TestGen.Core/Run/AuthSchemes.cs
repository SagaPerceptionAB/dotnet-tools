namespace TestGen.Core.Run;

/// <summary>Kopplar specens securityScheman till headers: val av header för --token, hemliga header-namn och varning om inloggning saknas.</summary>
public static class AuthSchemes
{
    /// <summary>
    /// Header för --token: första bearer- eller apiKey-schemat (de som operationerna kräver först, sedan övriga).
    /// Inget passande scheman => Authorization: Bearer.
    /// </summary>
    public static KeyValuePair<string, string> TokenHeader(ApiSpec spec, string token)
    {
        var required = spec.Operations.Where(o => o.AuthAlternatives is not null)
            .SelectMany(o => o.AuthAlternatives!).SelectMany(a => a).Distinct()
            .Select(n => spec.Schemes.FirstOrDefault(s => s.Name == n)).OfType<SecuritySchemeInfo>();
        var scheme = required.Concat(spec.Schemes).FirstOrDefault(s => s.Kind is "bearer" or "apiKey");
        return scheme is { Kind: "apiKey", HeaderName: { } h }
            ? KeyValuePair.Create(h, token)
            : KeyValuePair.Create("Authorization", token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token : "Bearer " + token);
    }

    /// <summary>Header-namn från apiKey-scheman: deras värden är hemliga.</summary>
    public static IEnumerable<string> SecretHeaderNames(ApiSpec spec) =>
        spec.Schemes.Where(s => s.Kind == "apiKey" && s.HeaderName is not null).Select(s => s.HeaderName!);

    /// <summary>
    /// Text att varna med om någon operation kräver inloggning (utan tomt alternativ) och ingen header för
    /// schemat är satt; annars null.
    /// </summary>
    public static string? MissingLoginNote(ApiSpec spec, IEnumerable<string> headerNames)
    {
        var have = new HashSet<string>(headerNames, StringComparer.OrdinalIgnoreCase);
        var byName = spec.Schemes.ToDictionary(s => s.Name);
        foreach (var op in spec.Operations.Where(o => o.AuthAlternatives is not null))
        {
            var satisfiedOrUnknown = op.AuthAlternatives!.Any(alt => alt.All(n =>
                !byName.TryGetValue(n, out var s) || s.Kind == "other" || (s.HeaderName is not null && have.Contains(s.HeaderName))));
            if (satisfiedOrUnknown) continue;
            var first = op.AuthAlternatives!.SelectMany(a => a).Where(byName.ContainsKey).Select(n => byName[n])
                .First(x => x.HeaderName is not null && !have.Contains(x.HeaderName));
            var what = first.Kind == "bearer" ? "bearer" : $"apiKey i headern {first.HeaderName}";
            return $"Obs: API:et kräver inloggning ({what}) men ingen token angavs. Använd --token, --header eller en miljö i testgen.yaml.";
        }
        return null;
    }
}
