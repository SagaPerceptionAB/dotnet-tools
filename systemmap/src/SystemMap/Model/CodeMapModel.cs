namespace SystemMap;

/// <summary>Vilket slags system en integration är.</summary>
public enum IntegrationKind { Database, Http, Wcf, Queue, Storage, Email }

/// <summary>
/// Hur en ingång tas emot: HTTP (controller, minimal API, Razor Page, Blazor-sida eller controller som returnerar en vy) eller
/// något annat som sätter igång kod: en bakgrundstjänst, ett konsolprogram, ett meddelande, ett jobb, ett gRPC-anrop eller ett SignalR-anrop.
/// </summary>
public enum EntryKind { Controller, MinimalApi, RazorPage, BlazorPage, MvcView, Background, Console, Consumer, Job, Grpc, SignalR }

public static class EntryKinds
{
    /// <summary>Ingångar som tas emot som HTTP-anrop och har en metod och en sökväg. De andra har ett namn (<see cref="EntryPoint.Name"/>).</summary>
    public static bool IsHttp(this EntryKind kind) => kind is EntryKind.Controller or EntryKind.MinimalApi or EntryKind.RazorPage or EntryKind.BlazorPage or EntryKind.MvcView;
}

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
    IReadOnlyList<string> Warnings,
    CodeInventory? Code = null)
{
    public const int SchemaVersion = 3;   // 2: fälten heter på engelska. 3: ingångar som inte är HTTP saknar method och path, och delar som inte nås har reached: false. Ett HTTP-anrop vars adress inte går att läsa har address: "runtime"
}

/// <summary>
/// Systemets egen kod, för att visa det som ingen ingång når: alla projekt (utom testprojekt) och klasserna i dem som har metoder med kod.
/// Klasser som ingår i uppstart, ramverkets anrop eller bara är data räknas inte (se <c>CodeInventoryFinder</c>).
/// </summary>
public sealed record CodeInventory(IReadOnlyList<CodeProject> Projects, IReadOnlyList<CodeClass> Classes)
{
    public static readonly CodeInventory Empty = new([], []);
}

/// <param name="Reached">Sant om någon ingång når kod i projektet (även hjälpmetoder som kartan inte ritar).</param>
public sealed record CodeProject(string Name, bool Reached);

/// <param name="Reached">Sant om någon ingång når klassen: en metod i den anropas, eller en instans skapas.</param>
/// <param name="Methods">Metoderna med kod som kartan följer anrop till (inte konstruktorer, egenskaper eller metoder som ramverket anropar).</param>
public sealed record CodeClass(string Project, string Name, string File, int Line, bool Reached, IReadOnlyList<CodeMethod> Methods);

public sealed record CodeMethod(string Name, string File, int Line, bool Reached);

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

/// <summary>
/// En ingång i systemet: ett HTTP-anrop som en controller-action, ett minimal API, en Razor Page eller en Blazor-sida tar emot, eller
/// en bakgrundstjänst, ett konsolprogram, en meddelandehanterare, ett jobb, ett gRPC-anrop eller ett SignalR-anrop.
/// </summary>
/// <param name="Method">HTTP-metoden. Tom för ingångar som inte är HTTP (<see cref="EntryKinds.IsHttp"/>).</param>
/// <param name="Path">Sökvägen i specen om ingången matchar en operation där, annars sökvägen enligt koden.</param>
/// <param name="CodePath">Sökvägen enligt koden (route-attributen), t.ex. <c>/Orders/{id}</c>.</param>
/// <param name="Handler">Controller och action, t.ex. <c>OrdersController.Create</c>; för en sida dess hanterare, t.ex. <c>AboutModel.OnPost</c>.</param>
/// <param name="File">Filen relativt solutionens katalog, med snedstreck.</param>
public sealed record EntryPoint(
    EntryKind Kind, string Method, string Path, string CodePath, string? OperationId, string Project, string Handler, string File, int Line,
    DataShape? Input, DataShape? Output, IReadOnlyList<Step> Steps, string? Name = null)
{
    /// <summary>Namnet ingången har i kartan och på kommandoraden: <c>POST /orders</c> för HTTP, annars namnet, t.ex. <c>Bakgrund: OrderCleanupWorker</c>.</summary>
    public string Key => Kind.IsHttp() ? $"{Method} {Path}" : Name ?? Handler;
}

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
/// <param name="AddressAtRuntime">Sant för ett HTTP-anrop vars adress varken är en sökväg i koden eller en nyckel i konfigurationen (den kommer först vid körning,
/// t.ex. ur OIDC-discovery). <see cref="Operation"/> är då bara verbet; texten som säger det är <see cref="CodeMapOverview.RuntimeAddress"/>.</param>
public sealed record IntegrationUse(string Id, string Operation, bool AddressAtRuntime = false)
{
    /// <summary>Det som skiljer ett integrationsanrop från ett annat i samma metod (för dubbletter).</summary>
    internal string Key => AddressAtRuntime ? $"{Id}|{Operation}|runtime" : $"{Id}|{Operation}";
}

/// <summary>En datatyp och dess fält (publika egenskaper), t.ex. <c>PaymentRequest {CustomerId, Amount, Currency}</c>.</summary>
public sealed record DataShape(string Type, IReadOnlyList<string> Fields);

public enum DifferenceKind { OnlyInCode, OnlyInSpec }

/// <summary>En ingång som bara finns i koden, eller en operation som bara finns i specen.</summary>
public sealed record SpecDifference(DifferenceKind Kind, string Method, string Path, string? OperationId, string? Handler);

/// <summary>Vad en app gör: tar emot API-anrop eller visar sidor (HTTP), kör bakgrundsarbete (tjänster, meddelanden, jobb) eller är ett konsolprogram.</summary>
public enum AppKind { Api, Web, Worker, Console }

/// <summary>En databas som appen registrerar: leverantören (<c>sql-server</c>, <c>postgresql</c>, <c>sqlite</c>, <c>in-memory</c>, <c>mysql</c>, <c>cosmos</c>).</summary>
public sealed record DatabaseFacts(string? Provider, string Context, string? ConfigKey);

/// <summary>En utgående HTTP-klient: klientens typ, nyckeln för adressen och projektet i solutionen som den pekar på (om något).</summary>
public sealed record HttpClientFacts(string Client, string? ConfigKey, string IntegrationId, string? TargetProject);

/// <summary>Fakta om en app (ett webbprojekt, en worker eller ett konsolprogram): sort, hälsovägar, databaser och utgående HTTP-klienter.</summary>
public sealed record AppFacts(
    string Project, IReadOnlyList<AppKind> Kinds, IReadOnlyList<string> HealthPaths,
    IReadOnlyList<DatabaseFacts> Databases, IReadOnlyList<HttpClientFacts> HttpClients);
