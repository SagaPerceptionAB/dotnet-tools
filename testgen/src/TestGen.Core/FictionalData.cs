using System.Globalization;
using Bogus;

namespace TestGen.Core;

/// <summary>
/// Fiktiva serier för personnummer, telefon och e-post. Testdata får aldrig kunna vara en verklig person.
/// </summary>
public static partial class FictionalData
{
    public enum Kind { Personnummer, Phone, Email }

    /// <summary>PTS reserverade nummerserier för fiktiva sammanhang: första numret och antal (pts.se, hämtad 2026-11-08).</summary>
    private static readonly (string First, int Count)[] PhoneSeries =
    {
        ("0701740605", 95),
        ("0846500400", 100),
        ("0313900600", 100),
        ("0406280400", 100),
        ("0980319200", 100),
    };

    // RFC 2606: example.com är reserverad för dokumentation.
    public const string EmailDomain = "example.com";

    /// <summary>Vilken sorts persondata fältet är, ur format först och fältnamn sedan. Null om ingen.</summary>
    public static Kind? KindOf(string name, string? format)
    {
        switch (format?.ToLowerInvariant())
        {
            case "personnummer": return Kind.Personnummer;
            case "phone" or "tel": return Kind.Phone;
            case "email": return Kind.Email;
        }
        if (format is not null) return null;
        return FieldRules.KindByName(name);
    }

    public static bool IsFictional(Kind kind, string value) => kind switch
    {
        Kind.Personnummer => IsPersonnummer(value),
        Kind.Phone => IsPhone(value),
        _ => IsEmail(value),
    };

    public static bool IsEmail(string value) =>
        value.LastIndexOf('@') is var at and > 0 && value[(at + 1)..].Equals(EmailDomain, StringComparison.OrdinalIgnoreCase);

    public static bool IsPersonnummer(string value)
    {
        var digits = Digits(value);
        return digits.Length switch
        {
            12 => Personnummer.Contains(digits),
            10 => Personnummer.Any(p => p.EndsWith(digits, StringComparison.Ordinal)),
            _ => false,
        };
    }

    public static bool IsPhone(string value)
    {
        var digits = Digits(value);
        if (digits.StartsWith("0046", StringComparison.Ordinal)) digits = "0" + digits[4..];
        else if (digits.StartsWith("46", StringComparison.Ordinal) && value.TrimStart().StartsWith('+')) digits = "0" + digits[2..];
        return PhoneSeries.Any(s => long.TryParse(s.First, CultureInfo.InvariantCulture, out var from)
            && long.TryParse(digits, CultureInfo.InvariantCulture, out var n) && digits.Length == s.First.Length && n >= from && n < from + s.Count);
    }

    /// <summary>Kandidater i fallande önskemål: första är den form som liknar <paramref name="like"/>.</summary>
    public static IEnumerable<string> PersonnummerCandidates(Randomizer random, string? like)
    {
        var digits = Personnummer[random.Number(0, Personnummer.Length - 1)];
        var forms = new Func<string, string>[]
        {
            d => $"{d[..8]}-{d[8..]}",
            d => $"{d[2..8]}-{d[8..]}",
            d => d,
            d => d[2..],
        };
        var likeIndex = like is null ? 0 : ShapeOfPersonnummer(like);
        yield return forms[likeIndex](digits);
        for (var i = 0; i < forms.Length; i++)
            if (i != likeIndex) yield return forms[i](digits);
    }

    private static int ShapeOfPersonnummer(string like)
    {
        var dash = like.Contains('-') || like.Contains('+');
        return Digits(like).Length == 12 ? (dash ? 0 : 2) : (dash ? 1 : 3);
    }

    /// <summary>Kandidater ur alla serier (en slumpad först), den form som liknar <paramref name="like"/> först för varje nummer.</summary>
    public static IEnumerable<string> PhoneCandidates(Randomizer random, string? like)
    {
        var start = random.Number(0, PhoneSeries.Length - 1);
        var likeIndex = like is null ? 0 : like.TrimStart().StartsWith('+') ? 1 : like.Contains(' ') ? 2 : like.Contains('-') ? 3 : 0;
        for (var k = 0; k < PhoneSeries.Length; k++)
        {
            var (first, count) = PhoneSeries[(start + k) % PhoneSeries.Length];
            var d = (long.Parse(first, CultureInfo.InvariantCulture) + random.Number(0, count - 1)).ToString("D10", CultureInfo.InvariantCulture);
            var mobile = d.StartsWith("070", StringComparison.Ordinal);
            var forms = new[]
            {
                d,
                "+46" + d[1..],
                mobile ? $"{d[..3]}-{d[3..6]} {d[6..8]} {d[8..]}" : d,
                mobile ? $"{d[..3]}-{d[3..]}" : d,
            };
            yield return forms[likeIndex];
            for (var i = 0; i < forms.Length; i++)
                if (i != likeIndex) yield return forms[i];
        }
    }

    public static string Email(Faker faker)
    {
        var local = faker.Internet.UserName().ToLowerInvariant();
        var clean = new string(local.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        return (clean.Length == 0 ? "user" : clean) + "@" + EmailDomain;
    }

    private static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());
}
