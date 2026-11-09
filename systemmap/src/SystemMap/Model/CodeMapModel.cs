namespace SystemMap;

/// <summary>Vilket slags system en integration är.</summary>
public enum IntegrationKind { Database, Http, Wcf, Queue, Storage, Email }

/// <summary>Hur en ingång tas emot: controller, minimal API, Razor Page, Blazor-sida eller controller som returnerar en vy.</summary>
public enum EntryKind { Controller, MinimalApi, RazorPage, BlazorPage, MvcView }

/// <summary>
/// Systemkartan: vägen från varje ingång (controller-action, minimal API eller sida) genom solutionens projekt ut till integrationerna.
/// Kartan visar möjliga vägar enligt koden, inte vilken väg ett visst anrop tog.
/// </summary>
/// <param name="Solution">Sökvägen som angavs (relativ eller absolut, som användaren skrev den).</param>
/// <param name="Projects">Projekten i den ordning anropen når dem (ingångens projekt först).</param>
public sealed record CodeMapModel(
    string Solution,
    IReadOnlyList<string> Projects,
    IReadOnlyList<Integration> Integrations,
    IReadOnlyList<EntryPoint> EntryPoints,
    IReadOnlyList<AppFacts> Apps,
    IReadOnlyList<SpecDifference> Differences,
    IReadOnlyList<string> Warnings)
{
    public const int SchemaVersion = 2;   // 2: fälten heter på engelska
}

/// <param name="Id">Stabilt id, t.ex. <c>http:Betaltjanst</c> eller <c>db:ShopDatabase</c>.</param>
/// <param name="Techniques">Hur systemet når integrationen, t.ex. "EF Core (ShopDbContext)" och "SQL", "Service Bus" eller "WCF/SOAP".</param>
/// <param name="ConfigKey">Nyckeln som adressen läses från, t.ex. <c>Betaltjanst:BaseUrl</c>.</param>
/// <param name="TargetProject">För HTTP: projektet i solutionen som adressen pekar på, om något.</param>
public sealed record Integration(string Id, IntegrationKind Kind, string Name, IReadOnlyList<string> Techniques, string? ConfigKey = null, string? TargetProject = null)
{
    /// <summary>Vad det är och tekniken, t.ex. "databas, EF Core och SQL", "kö, Service Bus" eller "HTTP, adressen i Betaltjanst:BaseUrl".</summary>
    public string Detail
    {
        get
        {
            var kind = Kind switch { IntegrationKind.Database => "databas, ", IntegrationKind.Queue => "kö, ", IntegrationKind.Storage => "lagring, ", _ => "" };
            return kind + string.Join(" och ", Techniques) + (ConfigKey is null ? "" : $", adressen i {ConfigKey}") + (TargetProject is null ? "" : $", anropar {TargetProject}");
        }
    }

    /// <summary>
    /// Samma integration hittad på ett annat sätt, t.ex. samma databas via EF Core och via SQL: teknikerna slås ihop (sorterade,
    /// så att ordningen anropen hittas i inte spelar roll) och den första adressnyckeln gäller.
    /// </summary>
    public Integration Merge(Integration other) =>
        other.Techniques.All(Techniques.Contains) && (ConfigKey is not null || other.ConfigKey is null) ? this
            : this with { Techniques = Techniques.Union(other.Techniques).Order(StringComparer.Ordinal).ToList(), ConfigKey = ConfigKey ?? other.ConfigKey };
}

/// <summary>En ingång i systemet: ett HTTP-anrop som en controller-action, ett minimal API, en Razor Page eller en Blazor-sida tar emot.</summary>
/// <param name="Path">Sökvägen i specen om ingången matchar en operation där, annars sökvägen enligt koden.</param>
/// <param name="CodePath">Sökvägen enligt koden (route-attributen), t.ex. <c>/Orders/{id}</c>.</param>
/// <param name="Handler">Controller och action, t.ex. <c>OrdersController.Create</c>; för en sida dess hanterare, t.ex. <c>AboutModel.OnPost</c>.</param>
/// <param name="File">Filen relativt solutionens katalog, med snedstreck.</param>
public sealed record EntryPoint(
    EntryKind Kind, string Method, string Path, string CodePath, string? OperationId, string Project, string Handler, string File, int Line,
    DataShape? Input, DataShape? Output, IReadOnlyList<Step> Steps);

/// <summary>
/// Ett steg på vägen: antingen en metod i solutionen (<see cref="Integration"/> är null) eller ett anrop till en integration.
/// För ett integrationsanrop är <see cref="In"/> det som skickas och <see cref="Out"/> det som kommer tillbaka.
/// </summary>
/// <param name="Project">Projektet där metoden (eller anropet till integrationen) finns.</param>
/// <param name="Member">Metoden, t.ex. <c>OrderService.PlaceAsync</c>; för en integration anropet, t.ex. <c>PostAsJsonAsync</c>.</param>
public sealed record Step(
    string Project, string Member, string File, int Line, DataShape? In, DataShape? Out, IntegrationUse? Integration, IReadOnlyList<Step> Steps)
{
    public bool ReachesIntegration => Integration is not null || Steps.Any(s => s.ReachesIntegration);
}

/// <param name="Operation">Vad som görs, t.ex. "POST payments", "läsa Customers" eller "skicka".</param>
public sealed record IntegrationUse(string Id, string Operation);

/// <summary>En datatyp och dess fält (publika egenskaper), t.ex. <c>PaymentRequest {CustomerId, Amount, Currency}</c>.</summary>
public sealed record DataShape(string Type, IReadOnlyList<string> Fields);

public enum DifferenceKind { OnlyInCode, OnlyInSpec }

/// <summary>En ingång som bara finns i koden, eller en operation som bara finns i specen.</summary>
public sealed record SpecDifference(DifferenceKind Kind, string Method, string Path, string? OperationId, string? Handler);

public enum AppKind { Api, Web }

/// <summary>En databas som appen registrerar: leverantören (<c>sql-server</c>, <c>postgresql</c>, <c>sqlite</c>, <c>in-memory</c>, <c>mysql</c>, <c>cosmos</c>).</summary>
public sealed record DatabaseFacts(string? Provider, string Context, string? ConfigKey);

/// <summary>En utgående HTTP-klient: klientens typ, nyckeln för adressen och projektet i solutionen som den pekar på (om något).</summary>
public sealed record HttpClientFacts(string Client, string? ConfigKey, string IntegrationId, string? TargetProject);

/// <summary>Fakta om en app (ett webbprojekt): sort, hälsovägar, databaser och utgående HTTP-klienter.</summary>
public sealed record AppFacts(
    string Project, IReadOnlyList<AppKind> Kinds, IReadOnlyList<string> HealthPaths,
    IReadOnlyList<DatabaseFacts> Databases, IReadOnlyList<HttpClientFacts> HttpClients);
