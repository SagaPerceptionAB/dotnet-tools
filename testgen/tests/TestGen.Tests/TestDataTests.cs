using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TestGen.Core;

namespace TestGen.Tests;

public class TestDataOrderTests
{
    private static JsonNode? Gen(string name, string schemaYaml, int seed = 1, JsonNode? fixedValue = null) =>
        new SchemaDataGenerator(seed, "sv").GenerateField(name, SpecText.Schema(schemaYaml), fixedValue);

    private static string Str(string name, string schemaYaml, int seed = 1) => Gen(name, schemaYaml, seed)!.GetValue<string>();

    [Test]
    public void Enum_wins_over_override_and_example()
    {
        var value = Gen("status", "{type: string, enum: [a, b], example: a}", fixedValue: JsonValue.Create("zzz"));
        Assert.That(value!.GetValue<string>(), Is.AnyOf("a", "b"));
    }

    [Test]
    public void An_override_that_is_one_of_the_enum_values_is_used()
    {
        for (var seed = 1; seed <= 20; seed++)
            Assert.That(Gen("provider", "{type: string, enum: [apple, google, test]}", seed, JsonValue.Create("test"))!.GetValue<string>(), Is.EqualTo("test"));
    }

    [Test]
    public void Override_wins_over_example() =>
        Assert.That(Gen("quantity", "{type: integer, example: 5}", fixedValue: JsonValue.Create(2))!.GetValue<long>(), Is.EqualTo(2));

    [Test]
    public void Valid_example_is_used_before_format() =>
        Assert.That(Str("when", "{type: string, format: date, example: '2021-02-03'}"), Is.EqualTo("2021-02-03"));

    [Test]
    public void Invalid_example_is_skipped([Range(1, 10)] int seed)
    {
        Assert.That(Str("code", "{type: string, maxLength: 3, example: toolong}", seed), Has.Length.LessThanOrEqualTo(3));
        Assert.That(Gen("n", "{type: integer, minimum: 1, maximum: 5, example: 99}", seed)!.GetValue<long>(), Is.InRange(1, 5));
        Assert.That(Str("sku", "{type: string, pattern: '^[A-Z]{3}$', example: abc}", seed), Does.Match("^[A-Z]{3}$"));
    }

    [Test]
    public void Default_is_used_when_valid_and_there_is_no_example() =>
        Assert.That(Str("mode", "{type: string, default: fast}"), Is.EqualTo("fast"));

    [Test]
    public void Format_comes_before_pattern() =>
        Assert.That(DateTime.TryParseExact(Str("when", "{type: string, format: date, pattern: '^20[0-9]{2}-'}"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), Is.True);

    [Test]
    public void Pattern_comes_before_field_name([Range(1, 10)] int seed) =>
        Assert.That(Str("city", "{type: string, pattern: '^[0-9]{4}$'}", seed), Does.Match("^[0-9]{4}$"));

    [Test]
    public void Bounds_come_before_type([Range(1, 20)] int seed)
    {
        Assert.That(Gen("n", "{type: integer, minimum: 10, maximum: 12}", seed)!.GetValue<long>(), Is.InRange(10, 12));
        Assert.That(Gen("n", "{type: integer, minimum: 1, exclusiveMinimum: true, maximum: 3, exclusiveMaximum: true}", seed)!.GetValue<long>(), Is.EqualTo(2));
        Assert.That(Gen("n", "{type: integer, minimum: 1, maximum: 100, multipleOf: 7}", seed)!.GetValue<long>() % 7, Is.EqualTo(0));
        Assert.That(Gen("n", "{type: number, minimum: 0, maximum: 10, multipleOf: 0.5}", seed)!.GetValue<double>() % 0.5, Is.EqualTo(0));
        Assert.That(Gen("xs", "{type: array, minItems: 3, maxItems: 4, items: {type: string}}", seed)!.AsArray(), Has.Count.InRange(3, 4));
        Assert.That(Str("s", "{type: string, minLength: 6, maxLength: 8}", seed), Has.Length.InRange(6, 8));
    }

    [Test]
    public void Field_name_comes_before_type() =>
        Assert.That(Str("homepage", "{type: string}"), Does.StartWith("https://example.com"));

    [Test]
    public void Type_is_the_last_step([Range(1, 5)] int seed)
    {
        Assert.That(Gen("flag", "{type: boolean}", seed), Is.Not.Null);
        Assert.That(Str("zzz", "{type: string}", seed), Is.Not.Empty);
    }

    [Test]
    public void Same_seed_gives_same_data()
    {
        const string schema = "{type: object, properties: {email: {type: string, format: email}, pn: {type: string, format: personnummer}, tel: {type: string, format: phone}, id: {type: string, format: uuid}}}";
        var first = Gen("x", schema, 7)!.ToJsonString();
        Assert.That(Gen("x", schema, 7)!.ToJsonString(), Is.EqualTo(first));
        Assert.That(Gen("x", schema, 8)!.ToJsonString(), Is.Not.EqualTo(first));
    }
}

public class TestDataFormatTests
{
    private static string Str(string schemaYaml, int seed = 1, string name = "v") =>
        new SchemaDataGenerator(seed, "sv").GenerateField(name, SpecText.Schema(schemaYaml))!.GetValue<string>();

    [Test]
    public void Each_standard_format_gives_a_valid_value([Range(1, 20)] int seed)
    {
        Assert.That(Str("{type: string, format: email}", seed), Does.EndWith("@example.com"));
        Assert.That(Str("{type: string, format: uri}", seed), Does.StartWith("https://example.com/"));
        Assert.That(Str("{type: string, format: url}", seed), Does.StartWith("https://example.com/"));
        Assert.That(Str("{type: string, format: hostname}", seed), Does.EndWith(".example.com"));
        Assert.That(Str("{type: string, format: uuid}", seed), Does.Match("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"));
        Assert.That(Str("{type: string, format: date}", seed), Does.Match(@"^\d{4}-\d{2}-\d{2}$"));
        Assert.That(Str("{type: string, format: date-time}", seed), Does.Match(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"));
        Assert.That(Str("{type: string, format: time}", seed), Does.Match(@"^\d{2}:\d{2}:\d{2}$"));
        Assert.That(Str("{type: string, format: byte}", seed), Has.Length.GreaterThan(0));
        Assert.That(() => Convert.FromBase64String(Str("{type: string, format: byte}", seed)), Throws.Nothing);
        Assert.That(Str("{type: string, format: postnummer}", seed), Does.Match(@"^\d{3} \d{2}$"));
        Assert.That(Str("{type: string, format: organisationsnummer}", seed), Does.Match(@"^\d{6}-\d{4}$"));
    }

    [Test]
    public void Ip_addresses_are_in_the_documentation_ranges([Range(1, 20)] int seed)
    {
        var v4 = IPAddress.Parse(Str("{type: string, format: ipv4}", seed)).GetAddressBytes();
        Assert.That(v4[..3], Is.EqualTo(new byte[] { 192, 0, 2 }));
        var v6 = IPAddress.Parse(Str("{type: string, format: ipv6}", seed)).GetAddressBytes();
        Assert.That(v6[..4], Is.EqualTo(new byte[] { 0x20, 0x01, 0x0d, 0xb8 }));
    }

    [Test]
    public void Numeric_formats_stay_in_range([Range(1, 20)] int seed)
    {
        var gen = new SchemaDataGenerator(seed, "sv");
        Assert.That(gen.GenerateField("n", SpecText.Schema("{type: integer, format: int32}"))!.GetValue<long>(), Is.InRange(int.MinValue, int.MaxValue));
        Assert.That(gen.GenerateField("n", SpecText.Schema("{type: integer, format: int64}"))!.GetValue<long>(), Is.GreaterThan(0));
        Assert.That(gen.GenerateField("n", SpecText.Schema("{type: number, format: float}"))!.GetValue<double>(), Is.GreaterThan(0));
        Assert.That(gen.GenerateField("n", SpecText.Schema("{type: number, format: double, minimum: 5, maximum: 6}"))!.GetValue<double>(), Is.InRange(5, 6));
    }

    [Test]
    public void Personnummer_phone_and_tel_formats_are_recognised([Range(1, 10)] int seed)
    {
        Assert.That(FictionalData.IsPersonnummer(Str("{type: string, format: personnummer}", seed)), Is.True);
        Assert.That(FictionalData.IsPhone(Str("{type: string, format: phone}", seed)), Is.True);
        Assert.That(FictionalData.IsPhone(Str("{type: string, format: tel}", seed)), Is.True);
    }
}

public class FictionalDataTests
{
    private static string Str(string name, string schemaYaml, int seed) =>
        new SchemaDataGenerator(seed, "sv").GenerateField(name, SpecText.Schema(schemaYaml))!.GetValue<string>();

    private static bool Luhn(string tenDigits)
    {
        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var d = tenDigits[i] - '0';
            if (i % 2 == 0) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    [Test]
    public void Generated_personnummer_are_from_the_test_series_and_valid([Range(1, 150)] int seed)
    {
        foreach (var (name, schema) in new[] { ("personnummer", "{type: string}"), ("x", "{type: string, format: personnummer}"), ("ssn", "{type: string, minLength: 5}") })
        {
            var pn = Str(name, schema, seed);
            Assert.That(FictionalData.IsPersonnummer(pn), Is.True, pn);
            Assert.That(Luhn(new string(pn.Where(char.IsAsciiDigit).ToArray())[^10..]), Is.True, pn);
        }
    }

    [Test]
    public void Personnummer_follows_the_form_in_the_schema([Range(1, 30)] int seed)
    {
        Assert.That(Str("pn", "{type: string, format: personnummer}", seed), Does.Match(@"^\d{8}-\d{4}$"));
        Assert.That(Str("pn", @"{type: string, format: personnummer, pattern: '^\d{6}-\d{4}$'}", seed), Does.Match(@"^\d{6}-\d{4}$"));
        Assert.That(Str("pn", @"{type: string, format: personnummer, pattern: '^\d{12}$'}", seed), Does.Match(@"^\d{12}$"));
        Assert.That(Str("pn", @"{type: string, format: personnummer, pattern: '^\d{10}$'}", seed), Does.Match(@"^\d{10}$"));
    }

    [Test]
    public void Generated_phone_numbers_are_from_the_pts_series([Range(1, 150)] int seed)
    {
        foreach (var (name, schema) in new[] { ("phone", "{type: string}"), ("x", "{type: string, format: phone}"), ("mobile", "{type: string, maxLength: 20}") })
        {
            var phone = Str(name, schema, seed);
            Assert.That(FictionalData.IsPhone(phone), Is.True, phone);
        }
    }

    [Test]
    public void Phone_follows_the_form_in_the_schema([Range(1, 30)] int seed)
    {
        var intl = Str("phone", @"{type: string, pattern: '^\+46[0-9]{9}$'}", seed);
        Assert.That(FictionalData.IsPhone(intl), Is.True, intl);
        var spaced = Str("phone", @"{type: string, format: phone, pattern: '^070-\d{3} \d{2} \d{2}$'}", seed);
        Assert.That(spaced, Does.Match(@"^070-\d{3} \d{2} \d{2}$"));
        Assert.That(FictionalData.IsPhone(spaced), Is.True, spaced);
    }

    [Test]
    public void Generated_email_and_urls_only_use_example_com([Range(1, 150)] int seed)
    {
        Assert.That(Str("email", "{type: string}", seed), Does.EndWith("@example.com"));
        Assert.That(Str("contact", "{type: string, format: email}", seed), Does.EndWith("@example.com"));
        Assert.That(Str("mail", "{type: string, format: email, maxLength: 30}", seed), Does.Match(@"^[^@]+@example\.com$").And.Length.LessThanOrEqualTo(30));
        Assert.That(Str("website", "{type: string}", seed), Does.StartWith("https://example.com"));
    }

    [Test]
    public void Examples_in_the_fictional_series_are_kept()
    {
        Assert.That(Str("personnummer", "{type: string, example: '19810706-2385'}", 1), Is.EqualTo("19810706-2385"));
        Assert.That(Str("phone", "{type: string, example: '070-174 06 35'}", 1), Is.EqualTo("070-174 06 35"));
        Assert.That(Str("email", "{type: string, format: email, example: eva@example.com}", 1), Is.EqualTo("eva@example.com"));
    }

    [Test]
    public void Examples_with_real_personal_data_are_replaced([Range(1, 20)] int seed)
    {
        var pn = Str("personnummer", "{type: string, example: '19121212-1212'}", seed);
        Assert.That(pn, Is.Not.EqualTo("19121212-1212"));
        Assert.That(FictionalData.IsPersonnummer(pn), Is.True);

        var phone = Str("phone", "{type: string, format: phone, example: '0701234567'}", seed);
        Assert.That(phone, Is.Not.EqualTo("0701234567"));
        Assert.That(FictionalData.IsPhone(phone), Is.True);

        var email = Str("email", "{type: string, format: email, example: kalle@gmail.com}", seed);
        Assert.That(email, Does.EndWith("@example.com"));
    }

    [Test]
    public void A_replaced_example_keeps_its_form([Range(1, 20)] int seed)
    {
        Assert.That(Str("personnummer", "{type: string, example: '121212-1212'}", seed), Does.Match(@"^\d{6}-\d{4}$"));
        Assert.That(Str("phone", "{type: string, example: '+46701234567'}", seed), Does.StartWith("+46"));
    }

    [Test]
    public void Recognition_rejects_values_outside_the_series()
    {
        Assert.That(FictionalData.IsPhone("0701740604"), Is.False);
        Assert.That(FictionalData.IsPhone("0701740700"), Is.False);
        Assert.That(FictionalData.IsPhone("+46980319250"), Is.True);
        Assert.That(FictionalData.IsEmail("a@example.com.evil.se"), Is.False);
        Assert.That(FictionalData.IsEmail("a@gmail.com"), Is.False);
        Assert.That(FictionalData.IsPersonnummer("19121212-1212"), Is.False);
        Assert.That(FictionalData.IsPersonnummer("abc"), Is.False);
    }

    [Test]
    public void Spec_example_values_in_the_generated_object_are_fictional([Range(1, 30)] int seed)
    {
        var schema = SpecText.Schema("{type: object, properties: {email: {type: string, format: email}, phone: {type: string}, personnummer: {type: string, example: '19121212-1212'}}}");
        var obj = new SchemaDataGenerator(seed, "sv").GenerateField("body", schema)!.AsObject();
        Assert.That(Regex.IsMatch(obj["email"]!.GetValue<string>(), "@example.com$"), Is.True);
        Assert.That(FictionalData.IsPhone(obj["phone"]!.GetValue<string>()), Is.True);
        Assert.That(FictionalData.IsPersonnummer(obj["personnummer"]!.GetValue<string>()), Is.True);
    }
}

public class OwnerNameTests
{
    private static JsonObject Body(string schemaName, string extra = "")
    {
        var yaml = $$"""
            openapi: 3.0.3
            info: {title: t, version: 1.0.0}
            paths:
              /x:
                post:
                  operationId: mk
                  requestBody:
                    content:
                      application/json:
                        schema: {$ref: '#/components/schemas/{{schemaName}}'}
                  responses:
                    '201': {description: ok}
            components:
              schemas:
                {{schemaName}}:
                  type: object
                  properties:
                    name: {type: string{{extra}}}
                    title: {type: string}
            """;
        var op = SpecText.Load(yaml)[0];
        return new SchemaDataGenerator(3, "sv").GenerateField("body", op.BodySchema!)!.AsObject();
    }

    [Test]
    public void Customer_name_is_a_person_name() =>
        Assert.That(Body("CustomerCreate")["name"]!.GetValue<string>(), Does.Match(@"^\S+( \S+)+$"));

    [Test]
    public void Product_name_comes_from_the_product_generator()
    {
        var name = Body("ProductCreate")["name"]!.GetValue<string>();
        Assert.That(name.Split(' '), Has.Length.EqualTo(3), name);   // "<adjektiv> <material> <produkt>"
    }

    [Test]
    public void Company_name_comes_from_the_company_generator()
    {
        var name = Body("SupplierCreate")["name"]!.GetValue<string>();
        Assert.That(name, Is.EqualTo(new Bogus.Faker("sv") { Random = new Bogus.Randomizer(3) }.Company.CompanyName()));
    }

    [Test]
    public void Unknown_owner_gives_the_same_as_before_and_limits_are_respected()
    {
        var unknown = Body("Thing")["name"]!.GetValue<string>();
        var plain = new SchemaDataGenerator(3, "sv").GenerateField("name", SpecText.Schema("{type: string}"))!.GetValue<string>();
        Assert.That(unknown, Is.EqualTo(plain));
        Assert.That(Body("ProductCreate", ", maxLength: 12")["name"]!.GetValue<string>(), Has.Length.LessThanOrEqualTo(12));
    }
}

public class ScenarioOwnerNameTests
{
    [Test]
    public void Valid_product_body_has_a_product_name()
    {
        var yaml = """
            openapi: 3.0.3
            info: {title: t, version: 1.0.0}
            paths:
              /x:
                post:
                  operationId: mk
                  requestBody:
                    content:
                      application/json:
                        schema: {$ref: '#/components/schemas/ProductCreate'}
                  responses:
                    '201': {description: ok}
            components:
              schemas:
                ProductCreate:
                  type: object
                  properties:
                    name: {type: string}
            """;
        var valid = SpecText.Cases(yaml, 1, out _)[0];
        Assert.That(valid.Scenario.Body!["name"]!.GetValue<string>().Split(' '), Has.Length.EqualTo(3));
    }
}
