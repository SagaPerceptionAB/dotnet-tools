using System.Globalization;

namespace TestGen.Core.Run;

/// <summary>Resultatet av att tolka --seed: ett heltal, "slumpa" eller ett frö hämtat från CI-systemet.</summary>
/// <param name="Seed">Slumpfröet som används (32 bitar). Null när verktyget ska välja själv.</param>
/// <param name="Random">Sant om verktyget ska välja ett slumpfrö (--seed slumpa, eller --seed ci utan CI-variabler).</param>
/// <param name="Note">Vad som hände, att skriva ut och spara i rapporten (t.ex. att ett stort tal gjordes om). Null om inget särskilt.</param>
public sealed record SeedChoice(int? Seed, bool Random, string? Note);

/// <summary>
/// Tolkar --seed. Heltal som ryms i 32 bitar används som de är. Större tal (t.ex. $GITHUB_RUN_ID) görs om till ett 32-bitarstal
/// med en stabil hash, så samma tal ger alltid samma frö på alla datorer. <c>ci</c> tar numret på bygget/körningen från CI-systemet.
/// </summary>
public static class SeedSpec
{
    /// <summary>CI-variabler i den ordning de prövas: (variabel som är körningens id, variabel som skiljer omkörningar åt).</summary>
    private static readonly (string Id, string? Attempt)[] CiVariables =
    {
        ("GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT"),
        ("BUILD_BUILDID", "SYSTEM_JOBATTEMPT"),
        ("CI_PIPELINE_ID", "CI_JOB_ID"),
        ("BUILD_NUMBER", null),
    };

    public static SeedChoice Parse(string text, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var t = text.Trim();
        switch (t.ToLowerInvariant())
        {
            case "slumpa" or "random": return new SeedChoice(null, true, null);
            case "ci": return FromCi(getEnv);
        }
        var digits = t.StartsWith('-') || t.StartsWith('+') ? t[1..] : t;
        if (digits.Length > 0 && digits.All(char.IsAsciiDigit))
        {
            if (int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return new SeedChoice(n, false, null);
            var hashed = Hash(t.TrimStart('+'));
            return new SeedChoice(hashed, false,
                $"Talet {t} är större än ett 32-bitars heltal och gjordes om till slumpfröet {hashed} (samma tal ger alltid samma frö). Upprepa körningen med --seed {t} eller --seed {hashed}.");
        }
        throw new UsageException($"--seed måste vara ett heltal, ordet slumpa eller ordet ci, inte '{text}'.",
            "Skriv t.ex. --seed 2, --seed $GITHUB_RUN_ID (stora tal går bra), --seed ci (tar bygget/körningens nummer från CI) eller --seed slumpa (verktyget väljer ett frö och skriver ut det så att körningen kan upprepas).");
    }

    private static SeedChoice FromCi(Func<string, string?> getEnv)
    {
        foreach (var (idVar, attemptVar) in CiVariables)
        {
            var id = getEnv(idVar)?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            var attempt = attemptVar is null ? null : getEnv(attemptVar)?.Trim();
            var key = string.IsNullOrEmpty(attempt) ? $"{idVar}={id}" : $"{idVar}={id};{attemptVar}={attempt}";
            var seed = Hash(key);
            var shown = string.IsNullOrEmpty(attempt) ? $"{idVar}={id}" : $"{idVar}={id}, {attemptVar}={attempt}";
            return new SeedChoice(seed, false, $"--seed ci: slumpfröet {seed} kommer från {shown}. Samma bygge ger samma frö; en omkörning (nytt försök) ger ett nytt.");
        }
        return new SeedChoice(null, true, "--seed ci: hittade ingen CI-variabel (GITHUB_RUN_ID, BUILD_BUILDID, CI_PIPELINE_ID eller BUILD_NUMBER), så verktyget väljer ett slumpfrö själv.");
    }

    /// <summary>Stabil hash till ett positivt 32-bitarstal (1 .. 2 147 483 646).</summary>
    public static int Hash(string text) => (int)(Fnv.Hash64(text) % 2147483646UL) + 1;
}
