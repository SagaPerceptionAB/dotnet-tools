using System.Text;

namespace TestGen.Core;

/// <summary>FNV-1a (stabil över processer, till skillnad från string.GetHashCode).</summary>
public static class Fnv
{
    public static ulong Hash64(string s)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    /// <summary>Seed per operation: FNV-1a av operationId + global seed.</summary>
    public static int OperationSeed(string operationId, int globalSeed)
    {
        var h = Hash64($"{operationId}\u0000{globalSeed}");
        return unchecked((int)(h ^ (h >> 32)));
    }

    /// <summary>
    /// Seed per exekvering: FNV-1a av operationId + global seed + räknare (salt). Ger olika data
    /// vid varje körning av samma operation, men deterministiskt för samma ordning.
    /// </summary>
    public static int OperationSeed(string operationId, int globalSeed, int salt)
    {
        var h = Hash64($"{operationId}\u0000{globalSeed}\u0000{salt}");
        return unchecked((int)(h ^ (h >> 32)));
    }
}
