using System.Globalization;
using System.Text.RegularExpressions;
using Bogus;
using Microsoft.OpenApi;

namespace TestGen.Core;

/// <summary>Inbyggd heuristik på fältnamn (näst sista steget), samt hjälpare som kontrollerar och kapar värden mot schemat.</summary>
public static class FieldRules
{
    private static readonly (Regex Name, Func<Faker, IOpenApiSchema, string?, string?> Generate, FictionalData.Kind? Kind)[] BuiltIn =
    {
        (R("personnummer|ssn|socialsecurity"), (f, s, like) => First(FictionalData.PersonnummerCandidates(f.Random, like), s), FictionalData.Kind.Personnummer),
        (R("orgnr|orgnummer|organisationsnummer|organizationnumber|orgnumber"), (f, _, _) => OrganizationNumber(f), null),
        (R("e-?mail"), (f, _, _) => FictionalData.Email(f), FictionalData.Kind.Email),
        (R("phone|mobile|telefon|tel"), (f, s, like) => First(FictionalData.PhoneCandidates(f.Random, like), s), FictionalData.Kind.Phone),
        (R("firstname|givenname|fornamn"), (f, _, _) => f.Name.FirstName(), null),
        (R("lastname|surname|familyname|efternamn"), (f, _, _) => f.Name.LastName(), null),
        (R("fullname"), (f, _, _) => f.Name.FullName(), null),
        (R("city|stad"), (f, _, _) => f.Address.City(), null),
        (R("zip|zipcode|postalcode|postnummer"), (f, _, _) => f.Address.ZipCode(), null),
        (R("street|streetaddress|address|adress"), (f, _, _) => f.Address.StreetAddress(), null),
        (R("country|land"), (f, _, _) => f.Address.Country(), null),
        (R("url|website|homepage"), (_, _, _) => "https://example.com/", null),
        (R("company|companyname|foretag"), (f, _, _) => f.Company.CompanyName(), null),
        (R("username"), (f, _, _) => f.Internet.UserName(), null),
    };

    private static readonly Regex TitleName = R("name|title|displayname");

    private static readonly (Regex Owner, Func<Faker, string> Name)[] OwnerNames =
    {
        (new("customer|person|user|employee|contact|member", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), f => f.Name.FullName()),
        (new("product|item|article|artikel|produkt", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), f => f.Commerce.ProductName()),
        (new("company|organization|organisation|foretag|supplier", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), f => f.Company.CompanyName()),
    };

    /// <summary>Namngenerator efter det omgivande schemats namn (t.ex. ProductCreate), eller null om namnet inte är känt.</summary>
    private static Func<Faker, string>? NameFor(string? owner) =>
        owner is null ? null : OwnerNames.FirstOrDefault(o => o.Owner.IsMatch(owner)).Name;

    private static Regex R(string p) => new($"^(?:{p})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Vilken fiktiv serie fältnamnet hör till, om någon.</summary>
    public static FictionalData.Kind? KindByName(string fieldName) =>
        BuiltIn.FirstOrDefault(b => b.Kind is not null && b.Name.IsMatch(fieldName)).Kind;

    /// <summary>Värde ur fältnamnet om ett sådant finns och uppfyller schemat, annars null.</summary>
    public static string? TryGenerate(string fieldName, IOpenApiSchema schema, Faker faker, string? owner = null)
    {
        if (SchemaUtil.TypeOf(schema) is not (null or "string")) return null;
        if (TitleName.IsMatch(fieldName) && NameFor(owner) is { } ownerName)
        {
            for (var i = 0; i < 5; i++)
                if (ownerName(faker) is var v && Meets(v, schema)) return v;
            return null;
        }
        foreach (var (re, generate, _) in BuiltIn)
        {
            if (!re.IsMatch(fieldName)) continue;
            for (var i = 0; i < 5; i++)
                if (generate(faker, schema, null) is { } v && Meets(v, schema)) return v;
            return null;
        }
        return null;
    }

    /// <summary>Första kandidaten som uppfyller schemat (null om ingen).</summary>
    public static string? First(IEnumerable<string> candidates, IOpenApiSchema schema) =>
        candidates.FirstOrDefault(c => Meets(c, schema));

    /// <summary>Uppfyller värdet längdgränser, mönster och (kända) format i schemat?</summary>
    public static bool Meets(string v, IOpenApiSchema schema)
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
            "personnummer" => FictionalData.IsPersonnummer(v),
            "phone" or "tel" => FictionalData.IsPhone(v),
            _ => true,
        };
    }

    /// <summary>Kapar/fyller ut till längdgränserna, men aldrig så att ett värde som följer mönstret slutar göra det.</summary>
    public static string Fit(string s, IOpenApiSchema schema)
    {
        var original = s;
        if (schema.MaxLength is { } max && s.Length > max) s = s[..max];
        if (schema.MinLength is { } min && s.Length < min) s = s.PadRight(min, 'x');
        return s != original && !string.IsNullOrEmpty(schema.Pattern) && Meets(original, Loose(schema)) && !Meets(s, Loose(schema)) ? original : s;
    }

    // Bara mönstret prövas (längd är det Fit just justerat).
    private static IOpenApiSchema Loose(IOpenApiSchema schema) => new OpenApiSchema { Pattern = schema.Pattern };

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

    public static string OrganizationNumber(Faker f)
    {
        // Tredje siffran >= 2 (skiljer från personnummer), börjar på 5 (aktiebolag m.fl.).
        var nine = $"5{f.Random.Number(0, 9)}{f.Random.Number(2, 9)}{f.Random.Number(0, 999999):D6}";
        var check = LuhnCheckDigit(nine);
        return $"{nine[..6]}-{nine[6..]}{check}";
    }
}
