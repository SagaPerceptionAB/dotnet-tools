using System.Globalization;
using System.Text.RegularExpressions;
using Bogus;
using Microsoft.OpenApi.Models;

namespace TestGen.Core;

/// <summary>
/// Fältregler: testgen.yaml fieldRules (matchas på fältnamn, skiftlägesokänsligt, regex tillåtet)
/// följt av inbyggda heuristiker på fältnamn.
/// </summary>
public sealed class FieldRules
{
    private static readonly Dictionary<string, Func<Faker, string>> Generators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["person.fullName"] = f => f.Name.FullName(),
        ["person.firstName"] = f => f.Name.FirstName(),
        ["person.lastName"] = f => f.Name.LastName(),
        ["internet.email"] = f => f.Internet.Email(),
        ["internet.userName"] = f => f.Internet.UserName(),
        ["internet.url"] = f => f.Internet.Url(),
        ["phone.number"] = f => f.Phone.PhoneNumber(),
        ["address.city"] = f => f.Address.City(),
        ["address.streetAddress"] = f => f.Address.StreetAddress(),
        ["address.zipCode"] = f => f.Address.ZipCode(),
        ["address.country"] = f => f.Address.Country(),
        ["company.name"] = f => f.Company.CompanyName(),
        ["commerce.productName"] = f => f.Commerce.ProductName(),
        ["lorem.word"] = f => f.Lorem.Word(),
        ["lorem.sentence"] = f => f.Lorem.Sentence(),
        ["se.personnummer"] = SwedishPersonalNumber,
        ["se.organisationsnummer"] = SwedishOrganizationNumber,
    };

    private static readonly (Regex Name, string Key)[] BuiltIn =
    {
        (R("personnummer|ssn|socialsecurity"), "se.personnummer"),
        (R("orgnr|orgnummer|organisationsnummer|organizationnumber|orgnumber"), "se.organisationsnummer"),
        (R("e-?mail"), "internet.email"),
        (R("phone|mobile|telefon|tel"), "phone.number"),
        (R("firstname|givenname|fornamn"), "person.firstName"),
        (R("lastname|surname|familyname|efternamn"), "person.lastName"),
        (R("fullname"), "person.fullName"),
        (R("city|stad"), "address.city"),
        (R("zip|zipcode|postalcode|postnummer"), "address.zipCode"),
        (R("street|streetaddress|address|adress"), "address.streetAddress"),
        (R("country|land"), "address.country"),
        (R("url|website|homepage"), "internet.url"),
        (R("company|companyname|foretag"), "company.name"),
        (R("username"), "internet.userName"),
    };

    private static Regex R(string p) => new($"^(?:{p})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly List<(Regex Name, string Key)> _config = new();

    public FieldRules(IReadOnlyDictionary<string, string> configRules)
    {
        foreach (var (pattern, key) in configRules)
        {
            if (key != "pattern" && !Generators.ContainsKey(key))
                throw new InvalidOperationException(
                    $"Okänd fältregel '{key}' för '{pattern}'. Tillgängliga: pattern, {string.Join(", ", Generators.Keys)}");
            _config.Add((R(pattern), key));
        }
    }

    /// <summary>Returnerar ett värde om en regel matchar fältnamnet, annars null.</summary>
    public string? TryGenerate(string fieldName, OpenApiSchema schema, Faker faker)
    {
        if (SchemaUtil.TypeOf(schema) is not (null or "string")) return null;

        foreach (var (re, key) in _config)
        {
            if (!re.IsMatch(fieldName)) continue;
            if (key == "pattern")
            {
                if (string.IsNullOrEmpty(schema.Pattern)) continue;
                return PatternGenerator.Generate(schema.Pattern, faker.Random);
            }
            return Satisfying(Generators[key], faker, schema);
        }
        foreach (var (re, key) in BuiltIn)
            if (re.IsMatch(fieldName)) return Satisfying(Generators[key], faker, schema);
        return null;
    }

    /// <summary>
    /// Specens villkor (pattern, format, längdgränser) går före fältnamnet: namnregelns värde används bara om det uppfyller dem
    /// (några försök). Annars returneras null, och värdet genereras ur specen i stället.
    /// </summary>
    private static string? Satisfying(Func<Faker, string> generate, Faker faker, OpenApiSchema schema)
    {
        for (var i = 0; i < 5; i++)
            if (generate(faker) is var v && Meets(v, schema)) return v;
        return null;
    }

    /// <summary>Uppfyller värdet längdgränser, mönster och (kända) format i schemat?</summary>
    public static bool Meets(string v, OpenApiSchema schema)
    {
        if (schema.MaxLength is { } max && v.Length > max) return false;
        if (schema.MinLength is { } min && v.Length < min) return false;
        if (!string.IsNullOrEmpty(schema.Pattern))
        {
            try { if (!Regex.IsMatch(v, schema.Pattern, RegexOptions.None, PatternMismatch.Timeout)) return false; }
            catch (ArgumentException) { }
            catch (RegexMatchTimeoutException) { }
        }
        return schema.Format?.ToLowerInvariant() switch
        {
            "email" => Regex.IsMatch(v, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"),
            "uuid" or "guid" => Guid.TryParse(v, out _),
            "date" => DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "date-time" => DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "uri" or "url" => Uri.TryCreate(v, UriKind.Absolute, out _),
            _ => true,
        };
    }

    /// <summary>Kapar/fyller ut till längdgränserna, men aldrig så att ett värde som följer mönstret slutar göra det.</summary>
    public static string Fit(string s, OpenApiSchema schema)
    {
        var original = s;
        if (schema.MaxLength is { } max && s.Length > max) s = s[..max];
        if (schema.MinLength is { } min && s.Length < min) s = s.PadRight(min, 'x');
        return s != original && !string.IsNullOrEmpty(schema.Pattern) && Meets(original, Loose(schema)) && !Meets(s, Loose(schema)) ? original : s;
    }

    // Bara mönstret prövas (längd är det Fit just justerat).
    private static OpenApiSchema Loose(OpenApiSchema schema) => new() { Pattern = schema.Pattern };

    // --- Luhn / svenska nummer ---

    private static int LuhnCheckDigit(string digits9)
    {
        var sum = 0;
        for (var i = 0; i < digits9.Length; i++)
        {
            var d = digits9[i] - '0';
            if (i % 2 == 0) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return (10 - sum % 10) % 10;
    }

    private static string SwedishPersonalNumber(Faker f)
    {
        var year = f.Random.Number(1940, 2005);
        var month = f.Random.Number(1, 12);
        var day = f.Random.Number(1, 28);
        var serial = f.Random.Number(0, 999);
        var nine = $"{year % 100:D2}{month:D2}{day:D2}{serial:D3}";
        var check = LuhnCheckDigit(nine);
        return string.Create(CultureInfo.InvariantCulture, $"{year:D4}{month:D2}{day:D2}-{serial:D3}{check}");
    }

    private static string SwedishOrganizationNumber(Faker f)
    {
        // Tredje siffran >= 2 (skiljer från personnummer), börjar på 5 (aktiebolag m.fl.).
        var nine = $"5{f.Random.Number(0, 9)}{f.Random.Number(2, 9)}{f.Random.Number(0, 999999):D6}";
        var check = LuhnCheckDigit(nine);
        return $"{nine[..6]}-{nine[6..]}{check}";
    }
}
