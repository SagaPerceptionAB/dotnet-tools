namespace SagaPerception.CommandLine;

/// <summary>Enkel flaggparser: --namn värde, --namn=värde och flaggor utan värde (t.ex. --force).</summary>
public sealed class Args
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _all = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    public bool Help { get; private set; }
    /// <summary>Kommandot som hjälptexterna hänvisar till, t.ex. "testgen map" eller "dotnet systemkarta".</summary>
    private string _see = "";

    /// <param name="see">Kommandot som felen hänvisar till ("Se: <paramref name="see"/> --help").</param>
    /// <param name="name">Namnet i "Giltiga flaggor för '...'".</param>
    public static Args Parse(string see, string name, string[] args, string[] valueNames, params string[] flagNames)
    {
        var r = new Args { _see = see };
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "-h" or "--help") { r.Help = true; continue; }
            if (a == "--verbose") { r._flags.Add("verbose"); continue; }   // gäller alla kommandon, se Command.Run
            if (!a.StartsWith("--"))
                throw new UsageException($"Oväntat argument: {a}", $"Flaggor skrivs med två streck, t.ex. --spec. Se: {see} --help");
            var flag = a[2..];
            string? inline = null;
            var eq = flag.IndexOf('=');
            if (eq > 0) { inline = flag[(eq + 1)..]; flag = flag[..eq]; }
            if (flagNames.Contains(flag)) { r._flags.Add(flag); continue; }
            if (!valueNames.Contains(flag))
            {
                var all = valueNames.Concat(flagNames).Concat(new[] { "verbose", "help" }).Distinct().ToArray();
                var hint = Suggest.Closest(flag, all) is { } m ? $" Menade du --{m}?" : "";
                throw new UsageException($"Okänd flagga: --{flag}.{hint}",
                    $"Giltiga flaggor för '{name}': {string.Join(", ", all.Select(x => "--" + x))}. Se: {see} --help");
            }
            if (inline is null)
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new UsageException($"Flaggan {a} kräver ett värde.", $"Skriv t.ex. {a} <värde>. Se: {see} --help");
                inline = args[++i];
            }
            r._values[flag] = inline;
            if (!r._all.TryGetValue(flag, out var list)) r._all[flag] = list = new List<string>();
            list.Add(inline);
        }
        return r;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);
    /// <summary>Alla värden för en flagga som angetts flera gånger (i den ordning de skrevs).</summary>
    public IReadOnlyList<string> GetAll(string name) => _all.TryGetValue(name, out var l) ? l : Array.Empty<string>();
    public bool Flag(string name) => _flags.Contains(name);
    public string Require(string name) => Get(name) ?? throw new UsageException($"--{name} saknas.", $"Se: {_see} --help");
}
