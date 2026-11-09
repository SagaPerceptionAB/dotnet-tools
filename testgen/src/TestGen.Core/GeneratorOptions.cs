namespace TestGen.Core;

/// <summary>Det som styr testdatan för ett API: slumpfrö, språk, bindningar, fasta värden och bortvalda operationer.</summary>
public sealed class GeneratorOptions
{
    public int Seed { get; set; } = 12345;
    public string Locale { get; set; } = "sv";
    public Dictionary<string, Dictionary<string, string>> Bindings { get; set; } = new();
    public Dictionary<string, Dictionary<string, object>> Overrides { get; set; } = new();
    public List<string> SkipOperations { get; set; } = new();
}
