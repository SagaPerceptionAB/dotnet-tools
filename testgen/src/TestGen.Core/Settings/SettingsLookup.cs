namespace TestGen.Core.Settings;

/// <summary>Uppslag på projekt- och miljönamn i testgen.yaml: skiftlägesokänsligt, som namnen i solutionen.</summary>
internal static class SettingsLookup
{
    public static T? Named<T>(this Dictionary<string, T?>? map, string name) where T : class =>
        map?.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}
