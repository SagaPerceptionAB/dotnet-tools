using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>Metoden som anropet finns i, och det som behövs för att namnge integrationen. <paramref name="Bindings"/> binder metodens parametrar till argumenten hos anroparen.</summary>
internal sealed record SinkContext(
    IMethodSymbol Caller, SyntaxNode Body, SemanticModel Model, ServiceResolver Resolver, Origins Origins, string Project, Bindings Bindings);

/// <param name="Member">Anropet som lämnar systemet, t.ex. <c>PostAsJsonAsync</c>.</param>
/// <param name="Sent">Det som skickas (en typ i solutionen), om det går att se.</param>
/// <param name="Received">Det som kommer tillbaka, om det går att se.</param>
internal sealed record SinkHit(Integration Integration, string Operation, string Member, DataShape? Sent, DataShape? Received, bool AddressAtRuntime = false);

/// <summary>
/// Känner igen anrop som lämnar systemet ("dörrar ut"): HTTP, databas, WCF, köer, lagring och e-post. Reglerna jämför
/// typernas fullständiga namn (och det korta namnet när ett paket saknas), så en ny regel är några rader i en av metoderna nedan.
/// </summary>
internal static class SinkCatalog
{
    private static readonly Integration FileIntegration = new("file", IntegrationKind.Storage, "Filsystem", ["filer"]);
    private static readonly Integration EmailIntegration = new("email:smtp", IntegrationKind.Email, "E-post", ["SMTP"]);
    private const string SaveChanges = "spara ändringar";

    public static SinkHit? Match(InvocationExpressionSyntax invocation, SinkContext ctx)
    {
        var method = Symbols.Invoked(invocation, ctx.Model);
        var name = method?.Name ?? Symbols.InvokedName(invocation);
        if (name is null || name == "nameof") return null;
        var receiver = Symbols.Receiver(invocation);
        // Extension-metod anropad som statisk metod: HttpClientJsonExtensions.PostAsJsonAsync(http, ...). Mottagaren är första argumentet.
        var offset = 0;
        if (method is { IsExtensionMethod: true, ReducedFrom: null } && invocation.ArgumentList.Arguments.Count > 0 &&
            (receiver is null || ctx.Model.GetSymbolInfo(receiver).Symbol is INamedTypeSymbol))
        {
            receiver = invocation.ArgumentList.Arguments[0].Expression;
            offset = 1;
        }
        var call = new Call(invocation, method, name, receiver, receiver is null ? null : ctx.Model.GetTypeInfo(receiver).Type, ctx) { ArgumentOffset = offset };
        return Wcf(call) ?? Refit(call) ?? Oidc(call) ?? Http(call) ?? EntityFramework(call) ?? Dapper(call) ?? AdoNet(call)
               ?? ServiceBus(call) ?? MassTransit(call) ?? RabbitMq(call) ?? Kafka(call) ?? StorageQueue(call)
               ?? Blob(call) ?? FileSystem(call) ?? Email(call);
    }

    /// <summary><c>new FileStream("fil", ...)</c> är också en väg ut.</summary>
    public static SinkHit? MatchCreation(BaseObjectCreationExpressionSyntax creation, SinkContext ctx)
    {
        if (!Symbols.Is(ctx.Model.GetTypeInfo(creation).Type, "System.IO.FileStream")) return null;
        var path = Origins.FirstString(creation.ArgumentList, ctx.Model);
        return new SinkHit(FileIntegration, path is null ? "öppna en fil" : $"öppna {path}", "new FileStream", null, null);
    }

    /// <summary>Om <paramref name="operation"/> är "spara ändringar" (EF Core) utan tabell.</summary>
    public static bool IsSaveChanges(string operation) => operation == SaveChanges;

    private sealed record Call(
        InvocationExpressionSyntax Invocation, IMethodSymbol? Method, string Name, ExpressionSyntax? Receiver, ITypeSymbol? ReceiverType, SinkContext Ctx)
    {
        public SemanticModel Model => Ctx.Model;

        /// <summary>1 när en extension-metod anropas i statisk form (mottagaren är första argumentet), annars 0.</summary>
        public int ArgumentOffset { get; init; }

        /// <summary>Argumenten utan mottagaren.</summary>
        public IEnumerable<ArgumentSyntax> Arguments => Invocation.ArgumentList.Arguments.Skip(ArgumentOffset);

        /// <summary>Anropas metoden på (eller är den deklarerad i) någon av typerna, eller deras bastyper och gränssnitt?</summary>
        public bool On(params string[] types) => Symbols.Is(ReceiverType, types) || (Method is not null && Symbols.Is(Method.ContainingType, types));

        public string? FirstString => SystemMap.Origins.FirstString(Invocation.ArgumentList, Model);
        public DataShape? Sent => DataShapes.FromArguments(Invocation.ArgumentList, Model);
        public IReadOnlyList<OriginFact> ReceiverOrigins => Ctx.Origins.Of(Receiver, Model);
        public string? OriginText(string kind, string name) => ReceiverOrigins.FirstOrDefault(f => f.Kind == kind && f.Name == name && f.Text is not null)?.Text;
    }

    // --- HTTP ------------------------------------------------------------------------------

    private static SinkHit? Http(Call c)
    {
        if (!c.On("System.Net.Http.HttpClient", "System.Net.Http.HttpMessageInvoker") || HttpVerb(c.Name) is not { } verb) return null;
        var target = Destination.None;
        if (verb == "SEND")
        {
            var (v, d) = RequestMessage(c);
            verb = v ?? "SEND";
            target = d;
        }
        else if (c.Arguments.FirstOrDefault() is { } first) target = DestinationOf(first.Expression, c);

        var owner = ClientOwner(c);
        var info = c.Ctx.Resolver.HttpClientFor(owner)
                   ?? (c.OriginText("call", "CreateClient") is { } named ? c.Ctx.Resolver.NamedClient(named) ?? new HttpClientInfo(named, null) : null)
                   ?? (c.ReceiverOrigins.FirstOrDefault(f => f.Kind == "config") is { } key ? new HttpClientInfo(Origins.NameFromConfigKey(key.Name), key.Name) : null)
                   ?? new HttpClientInfo(owner.Name, null);
        var integration = new Integration("http:" + info.Name, IntegrationKind.Http, info.Name, ["HTTP"], info.ConfigKey ?? target.ConfigKey);
        var received = c.Name.Contains("FromJson") ? c.Method?.TypeArguments.Select(DataShapes.Of).FirstOrDefault(s => s is not null) : ReadInBody(c.Ctx);
        // En adress som varken är en sökväg i koden eller en nyckel i konfigurationen kommer först vid körning (t.ex. ur OIDC-discovery).
        var addressAtRuntime = target.Text is null && target.ConfigKey is null && integration.ConfigKey is null;
        var operation = target.Text is not null ? $"{verb} {target.Text}" : verb;
        return new SinkHit(integration, operation, c.Name, c.Name.Contains("FromJson") ? null : c.Sent, received, addressAtRuntime);
    }

    /// <summary>Klientens ägare: klassen med den typade klienten. En <c>HttpClient</c> som är parameter hör till den som skickade den.</summary>
    private static ITypeSymbol ClientOwner(Call c) =>
        c.Receiver is IdentifierNameSyntax id && c.Ctx.Bindings.Of(c.Model.GetSymbolInfo(id).Symbol)?.Owner is { } bound ? bound : c.Ctx.Caller.ContainingType;

    /// <summary>Vart ett anrop går: nyckeln för adressen (om den kommer ur konfigurationen) och texten som visas efter metoden.</summary>
    private sealed record Destination(string? ConfigKey, string? Text)
    {
        public static readonly Destination None = new(null, null);
    }

    /// <summary>
    /// Adressen i ett uttryck: en sökväg, <c>new Uri(...)</c> eller <c>new Uri(new Uri(bas), sökväg)</c>. En bas ur konfigurationen visas som
    /// värdet i appsettings eller egenskapens startvärde när det är en adress, annars som <c>{Nyckel}</c>.
    /// </summary>
    private static Destination DestinationOf(ExpressionSyntax expression, Call c)
    {
        var (model, bindings) = (c.Model, c.Ctx.Bindings);
        ExpressionSyntax? baseExpression = null;
        if (expression is BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [var first, var second] } pair && Symbols.Is(model.GetTypeInfo(pair).Type, "System.Uri"))
        {
            baseExpression = first.Expression;
            expression = second.Expression;
        }
        var key = c.Ctx.Origins.ConfigKeysIn(baseExpression ?? expression, model, bindings).FirstOrDefault();
        var path = PathArgument(expression, model, bindings);
        if (key is null) return new Destination(null, path);
        if (baseExpression is null && path is not null) return new Destination(key, path);   // t.ex. $"{settings.BaseUrl}/orders"
        var address = c.Ctx.Resolver.AddressOf(key, c.Ctx.Project) ?? "{" + key + "}";
        if (path is null) return new Destination(key, address);
        return new Destination(key, path.StartsWith("http://", StringComparison.Ordinal) || path.StartsWith("https://", StringComparison.Ordinal)
            ? path
            : address.TrimEnd('/') + "/" + path.TrimStart('/'));
    }

    private static string? HttpVerb(string name) => (name.EndsWith("Async") ? name[..^5] : name) switch
    {
        "Get" or "GetString" or "GetByteArray" or "GetStream" or "GetFromJson" or "GetFromJsonAsAsyncEnumerable" => "GET",
        "Post" or "PostAsJson" => "POST",
        "Put" or "PutAsJson" => "PUT",
        "Patch" or "PatchAsJson" => "PATCH",
        "Delete" or "DeleteFromJson" => "DELETE",
        "Send" => "SEND",
        _ => null,
    };

    /// <summary><c>new HttpRequestMessage(HttpMethod.Post, "orders")</c> i samma metod, för <c>SendAsync</c>.</summary>
    private static (string? Verb, Destination Target) RequestMessage(Call c)
    {
        foreach (var creation in c.Ctx.Body.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            if (!Symbols.Is(c.Model.GetTypeInfo(creation).Type, "System.Net.Http.HttpRequestMessage") || creation.ArgumentList is not { Arguments.Count: > 0 } args) continue;
            var verb = args.Arguments[0].Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText.ToUpperInvariant(),
                BaseObjectCreationExpressionSyntax m => Origins.FirstString(m.ArgumentList, c.Model)?.ToUpperInvariant(),
                _ => null,
            };
            return (verb, args.Arguments.Count > 1 ? DestinationOf(args.Arguments[1].Expression, c) : Destination.None);
        }
        return (null, Destination.None);
    }

    /// <summary><c>ReadFromJsonAsync&lt;T&gt;</c> eller <c>Deserialize&lt;T&gt;</c> i samma metod: det som kommer tillbaka.</summary>
    private static DataShape? ReadInBody(SinkContext ctx) =>
        ctx.Body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => Symbols.InvokedName(i) is "ReadFromJsonAsync" or "ReadFromJsonAsAsyncEnumerable" or "ReadAsAsync" or "DeserializeAsync" or "Deserialize" or "DeserializeObject")
            .Select(i => Symbols.Invoked(i, ctx.Model)?.TypeArguments.Select(DataShapes.Of).FirstOrDefault(s => s is not null))
            .FirstOrDefault(s => s is not null);

    /// <summary>
    /// OIDC-discovery: <c>ConfigurationManager&lt;OpenIdConnectConfiguration&gt;.GetConfigurationAsync</c> hämtar discovery-dokumentet och sedan nycklarna (JWKS) över HTTP,
    /// likaså <c>OpenIdConnectConfigurationRetriever.GetAsync(adress)</c>. Integrationen heter som värden i adressen (eller som konfigurationsnyckeln),
    /// och adressen är dokumentets (se <see cref="DiscoveryAddress"/>). <c>HttpDocumentRetriever.GetDocumentAsync</c> hämtar ett dokument på samma sätt.
    /// </summary>
    private static SinkHit? Oidc(Call c)
    {
        var technique = "HTTP (OIDC-konfiguration och nycklar)";
        var discovery = c.Ctx.Resolver.Discovery;
        MetadataAddress address;
        if (c.Name is "GetConfigurationAsync" or "RequestRefresh" && c.On(DiscoveryAddress.ManagerType, DiscoveryAddress.ManagerInterface))
            address = discovery.OfManager(c.Receiver, c.Model, c.Ctx.Bindings, c.Ctx.Project);
        else if (c.Name is "GetAsync" or "GetConfigurationAsync" && c.On("Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfigurationRetriever") &&
                 c.Arguments.FirstOrDefault() is { } retrieved)
            address = discovery.OfAddress(retrieved.Expression, c.Model, c.Ctx.Bindings, c.Ctx.Project);
        else if (c.Name is "GetDocumentAsync" && c.On("Microsoft.IdentityModel.Protocols.HttpDocumentRetriever") && c.Arguments.FirstOrDefault() is { } document)
        {
            technique = "HTTP";
            address = discovery.OfAddress(document.Expression, c.Model, c.Ctx.Bindings, c.Ctx.Project);
        }
        else return null;

        var host = address.Text is not null && Uri.TryCreate(address.Text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.Host : null;
        var name = address.ConfigKey is { } key ? Origins.NameFromConfigKey(key) : host;
        var readable = name is not null;
        name ??= c.Ctx.Caller.ContainingType.Name;
        var integration = new Integration("http:" + name, IntegrationKind.Http, name, [technique], readable ? address.ConfigKey : null);
        var operation = readable ? $"GET {address.Text ?? "{" + address.ConfigKey + "}"}" : "GET";
        return new SinkHit(integration, operation, c.Name, null, null, !readable);
    }

    private static SinkHit? Refit(Call c)
    {
        if (c.Method is not { ContainingType.TypeKind: TypeKind.Interface } m) return null;
        var attribute = m.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass is { } k && k.ContainingNamespace?.ToDisplayString() == "Refit" &&
            k.Name is "GetAttribute" or "PostAttribute" or "PutAttribute" or "DeleteAttribute" or "PatchAttribute" or "HeadAttribute");
        if (attribute is null) return null;
        var verb = attribute.AttributeClass!.Name[..^"Attribute".Length].ToUpperInvariant();
        var path = attribute.ConstructorArguments.FirstOrDefault().Value as string;
        var info = c.Ctx.Resolver.HttpClientFor(m.ContainingType) ?? new HttpClientInfo(Plain(m.ContainingType.Name), null);
        return new SinkHit(new Integration("http:" + info.Name, IntegrationKind.Http, info.Name, ["HTTP (Refit)"], info.ConfigKey),
            path is null ? verb : $"{verb} {path.TrimStart('/')}", m.Name, c.Sent, DataShapes.Of(m.ReturnType));
    }

    // --- Databas ---------------------------------------------------------------------------

    private static readonly HashSet<string> EfWrites =
        ["Add", "AddAsync", "AddRange", "AddRangeAsync", "Attach", "AttachRange", "Update", "UpdateRange", "Remove", "RemoveRange",
         "ExecuteDelete", "ExecuteDeleteAsync", "ExecuteUpdate", "ExecuteUpdateAsync"];

    private static readonly HashSet<string> EfReads =
        ["Find", "FindAsync", "ToList", "ToListAsync", "ToArray", "ToArrayAsync", "ToDictionary", "ToDictionaryAsync", "ToHashSet", "ToHashSetAsync",
         "First", "FirstAsync", "FirstOrDefault", "FirstOrDefaultAsync", "Single", "SingleAsync", "SingleOrDefault", "SingleOrDefaultAsync",
         "Last", "LastAsync", "LastOrDefault", "LastOrDefaultAsync", "Any", "AnyAsync", "All", "AllAsync", "Count", "CountAsync", "LongCount", "LongCountAsync",
         "Sum", "SumAsync", "Min", "MinAsync", "Max", "MaxAsync", "Average", "AverageAsync", "Contains", "ContainsAsync",
         "ElementAt", "ElementAtAsync", "ElementAtOrDefault", "ElementAtOrDefaultAsync", "Load", "LoadAsync", "ForEachAsync", "AsAsyncEnumerable"];

    private static SinkHit? EntityFramework(Call c)
    {
        const string context = "Microsoft.EntityFrameworkCore.DbContext";
        if (c.On("Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade") && (c.Name.StartsWith("ExecuteSql") || c.Name.StartsWith("SqlQuery")))
        {
            var owner = c.Receiver is MemberAccessExpressionSyntax ma ? c.Model.GetTypeInfo(ma.Expression).Type : null;
            var sql = c.Invocation.ArgumentList.Arguments.Select(a => Text(a.Expression, c.Model)).FirstOrDefault(s => s is not null);
            return Ef(owner, sql is null ? "SQL-fråga" : SqlText.Describe(sql), c, null, null);
        }
        if (c.On(context))
        {
            if (c.Name is "SaveChanges" or "SaveChangesAsync") return Ef(c.ReceiverType, SaveChanges, c, null, null);
            var entity = c.Method?.TypeArguments.FirstOrDefault() ?? (c.Invocation.ArgumentList.Arguments.FirstOrDefault() is { } a ? c.Model.GetTypeInfo(a.Expression).Type : null);
            if (EfWrites.Contains(c.Name)) return Ef(c.ReceiverType, $"{WriteVerb(c.Name)} {entity?.Name ?? "databasen"}", c, c.Sent, null);
            if (c.Name is "Find" or "FindAsync") return Ef(c.ReceiverType, $"läsa {entity?.Name ?? "databasen"}", c, null, DataShapes.Of(entity));
        }
        if (DbSetIn(c.Receiver, c.Model) is not { } set) return null;
        if (EfWrites.Contains(c.Name)) return Ef(set.Context, $"{WriteVerb(c.Name)} {set.Table}", c, c.Sent, null);
        if (EfReads.Contains(c.Name)) return Ef(set.Context, $"läsa {set.Table}", c, null, DataShapes.Of(set.Entity));
        return null;
    }

    private static string WriteVerb(string name) =>
        name.StartsWith("Add") || name.StartsWith("Attach") ? "lägga till i" : name.StartsWith("Remove") || name.StartsWith("ExecuteDelete") ? "ta bort ur" : "ändra i";

    /// <summary>Databasen heter som anslutningens källa om den syns i registreringen (se <see cref="ServiceResolver.DatabaseOf"/>), annars som kontexten.</summary>
    private static SinkHit Ef(ITypeSymbol? context, string operation, Call c, DataShape? sent, DataShape? received)
    {
        var contextName = context is not null && Symbols.Is(context, "Microsoft.EntityFrameworkCore.DbContext") && context.Name != "DbContext" ? context.Name : null;
        var name = (context is null ? null : c.Ctx.Resolver.DatabaseOf(context)) ?? contextName ?? "Databas";
        var technique = contextName is not null && contextName != name ? $"EF Core ({contextName})" : "EF Core";
        return new SinkHit(new Integration("db:" + name, IntegrationKind.Database, name, [technique]), operation, c.Name, sent, received);
    }

    private sealed record DbSetUse(ITypeSymbol? Context, string Table, ITypeSymbol Entity);

    /// <summary>Hittar <c>DbSet&lt;T&gt;</c> i mottagarkedjan: <c>db.Customers.Where(...).AnyAsync()</c> ger tabellen Customers.</summary>
    private static DbSetUse? DbSetIn(ExpressionSyntax? expression, SemanticModel model)
    {
        var seen = 0;
        for (var e = expression; e is not null && seen++ < 30;)
        {
            var type = model.GetTypeInfo(e).Type;
            if (Symbols.Find(type, "Microsoft.EntityFrameworkCore.DbSet`1") is { } set)
            {
                var entity = set.TypeArguments.FirstOrDefault() ?? type!;
                return e switch
                {
                    MemberAccessExpressionSyntax ma => new DbSetUse(model.GetTypeInfo(ma.Expression).Type, ma.Name.Identifier.ValueText, entity),
                    InvocationExpressionSyntax inv when Symbols.Receiver(inv) is { } r => new DbSetUse(model.GetTypeInfo(r).Type, entity.Name, entity),
                    _ => new DbSetUse(null, entity.Name, entity),
                };
            }
            e = e switch
            {
                MemberAccessExpressionSyntax ma => ma.Expression,
                InvocationExpressionSyntax inv => Symbols.Receiver(inv),
                ParenthesizedExpressionSyntax p => p.Expression,
                AwaitExpressionSyntax a => a.Expression,
                QueryExpressionSyntax q => q.FromClause.Expression,
                IdentifierNameSyntax id when model.GetSymbolInfo(id).Symbol is ILocalSymbol local =>
                    local.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Initializer?.Value,
                _ => null,
            };
        }
        return null;
    }

    private static SinkHit? Dapper(Call c)
    {
        // DbConnection har inga Query- eller Execute-metoder själv: det är Dapper (eller liknande) som lägger till dem.
        if (!c.On("System.Data.IDbConnection") || !(c.Name.StartsWith("Query") || c.Name.StartsWith("Execute"))) return null;
        var sql = DeepText(c.Invocation.ArgumentList, c.Model);
        var name = ConnectionName(c);
        var received = c.Method?.TypeArguments.Select(DataShapes.Of).FirstOrDefault(s => s is not null);
        return new SinkHit(new Integration("db:" + name, IntegrationKind.Database, name, ["SQL"]), sql is null ? "SQL-fråga" : SqlText.Describe(sql),
            c.Name, received is null ? c.Sent : null, received);
    }

    private static SinkHit? AdoNet(Call c)
    {
        if (!c.On("System.Data.Common.DbCommand", "System.Data.IDbCommand") || !c.Name.StartsWith("Execute")) return null;
        var sql = c.ReceiverOrigins.FirstOrDefault(f => f.Kind == "new" && f.Text is not null)?.Text
                  ?? c.Ctx.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                      .Where(a => a.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CommandText" } or IdentifierNameSyntax { Identifier.ValueText: "CommandText" })
                      .Select(a => Text(a.Right, c.Model)).FirstOrDefault(s => s is not null);
        var name = ConnectionName(c);
        return new SinkHit(new Integration("db:" + name, IntegrationKind.Database, name, ["SQL"]), sql is null ? "SQL-fråga" : SqlText.Describe(sql), c.Name, null, null);
    }

    /// <summary>
    /// Databasen en anslutning går till: konfigurationsnyckeln, eller klassen i solutionen som skapar anslutningen. Samma namn
    /// som en DbContext med samma källa får, så att EF Core och SQL mot samma databas blir en integration.
    /// </summary>
    private static string ConnectionName(Call c)
    {
        var origins = c.ReceiverOrigins;
        if (origins.FirstOrDefault(f => f.Kind == "config") is { } key) return Origins.NameFromConfigKey(key.Name);
        if (origins.FirstOrDefault(f => f.Kind == "call" && f.Type is not null && Symbols.InSource(f.Type)) is { Type: { } factory }) return factory.Name;
        return c.ReceiverType is { } t && Symbols.InSource(t) ? t.Name : "SQL";
    }

    // --- Köer ------------------------------------------------------------------------------

    private static SinkHit? ServiceBus(Call c)
    {
        if (!c.On("Azure.Messaging.ServiceBus.ServiceBusSender") || !(c.Name.StartsWith("SendMessage") || c.Name.StartsWith("ScheduleMessage"))) return null;
        var queue = c.OriginText("call", "CreateSender") ?? "Service Bus";
        return new SinkHit(new Integration("queue:" + queue, IntegrationKind.Queue, queue, ["Service Bus"]), "skicka", c.Name, c.Sent, null);
    }

    private static SinkHit? MassTransit(Call c)
    {
        var publish = c.Name == "Publish" && c.On("MassTransit.IPublishEndpoint");
        var send = c.Name == "Send" && c.On("MassTransit.ISendEndpoint");
        if (!publish && !send) return null;
        var message = c.Method?.TypeArguments.FirstOrDefault()
                      ?? (c.Invocation.ArgumentList.Arguments.FirstOrDefault() is { } a ? c.Model.GetTypeInfo(a.Expression).Type : null);
        var name = message?.Name ?? "MassTransit";
        return new SinkHit(new Integration("queue:" + name, IntegrationKind.Queue, name, ["MassTransit"]), publish ? "publicera" : "skicka", c.Name, c.Sent, null);
    }

    private static SinkHit? RabbitMq(Call c)
    {
        if (c.Name is not ("BasicPublish" or "BasicPublishAsync") || !c.On("RabbitMQ.Client.IModel", "RabbitMQ.Client.IChannel")) return null;
        var parts = c.Invocation.ArgumentList.Arguments.Take(2).Select(a => Symbols.ConstantString(a.Expression, c.Model)).Where(s => !string.IsNullOrEmpty(s));
        var name = string.Join('/', parts) is { Length: > 0 } n ? n : "RabbitMQ";
        return new SinkHit(new Integration("queue:" + name, IntegrationKind.Queue, name, ["RabbitMQ"]), "skicka", c.Name, c.Sent, null);
    }

    private static SinkHit? Kafka(Call c)
    {
        if (c.Name is not ("Produce" or "ProduceAsync") || !c.On("Confluent.Kafka.IProducer`2")) return null;
        var topic = c.FirstString ?? "Kafka";
        return new SinkHit(new Integration("queue:" + topic, IntegrationKind.Queue, topic, ["Kafka"]), "skicka", c.Name, c.Sent, null);
    }

    private static SinkHit? StorageQueue(Call c)
    {
        if (!c.Name.StartsWith("SendMessage") || !c.On("Azure.Storage.Queues.QueueClient")) return null;
        var queue = c.OriginText("call", "GetQueueClient") ?? c.OriginText("new", "QueueClient") ?? "Storage Queue";
        return new SinkHit(new Integration("queue:" + queue, IntegrationKind.Queue, queue, ["Azure Storage Queue"]), "skicka", c.Name, c.Sent, null);
    }

    // --- Lagring och e-post ----------------------------------------------------------------

    /// <summary>
    /// Vad ett anrop gör med lagringen, efter metodens namn. Ett namn som slutar på <c>*</c> matchar alla som börjar så, annars måste namnet vara exakt;
    /// den första träffen gäller. En ny operation är en rad här.
    /// </summary>
    private static string? OperationOf(string name, (string Name, string Operation)[] table) =>
        table.FirstOrDefault(t => t.Name.EndsWith('*') ? name.StartsWith(t.Name[..^1], StringComparison.Ordinal) : name == t.Name).Operation;

    /// <summary>Azure Blob Storage: <c>BlobClient</c>/<c>BlobBaseClient</c> (och <c>BlockBlobClient</c> m.fl.), <c>BlobContainerClient</c> och <c>BlobServiceClient</c>, med och utan <c>Async</c>.</summary>
    private static readonly (string Name, string Operation)[] BlobOperations =
    [
        ("Exists", "finns"),
        ("Upload*", "ladda upp"),
        ("Download*", "hämta"), ("OpenRead*", "hämta"), ("Query*", "hämta"),
        ("OpenWrite*", "skriva"),
        ("Delete*", "ta bort"),
        ("Undelete*", "återställa"),
        ("GetBlobs*", "lista"), ("GetBlobContainers*", "lista"), ("FindBlobsByTags*", "lista"),
        ("GetProperties*", "läsa egenskaper"), ("GetTags*", "läsa egenskaper"), ("GetAccountInfo*", "läsa egenskaper"),
        ("SetMetadata*", "ändra egenskaper"), ("SetHttpHeaders*", "ändra egenskaper"), ("SetTags*", "ändra egenskaper"), ("SetAccessTier*", "ändra egenskaper"),
        ("Create*", "skapa"),
        ("CopyFromUri*", "kopiera"), ("StartCopyFromUri*", "kopiera"), ("SyncCopyFromUri*", "kopiera"),
    ];

    private static SinkHit? Blob(Call c)
    {
        if (!c.On("Azure.Storage.Blobs.BlobContainerClient", "Azure.Storage.Blobs.Specialized.BlobBaseClient", "Azure.Storage.Blobs.BlobServiceClient")) return null;
        // "Exists" slutar på Async eller inte: båda är samma operation.
        var name = c.Name.EndsWith("Async", StringComparison.Ordinal) ? c.Name[..^"Async".Length] : c.Name;
        if (OperationOf(name, BlobOperations) is not { } operation) return null;
        var container = (c.Name.Contains("BlobContainer") && c.On("Azure.Storage.Blobs.BlobServiceClient") ? c.FirstString : null)
                        ?? c.OriginText("call", "GetBlobContainerClient") ?? c.OriginText("new", "BlobContainerClient") ?? "Blob";
        return new SinkHit(new Integration("storage:" + container, IntegrationKind.Storage, container, ["Blob"]), operation, c.Name, c.Sent, null);
    }

    // Filsystemet. Namnen är metoderna i System.IO.File, Directory, FileInfo och DirectoryInfo; en egenskap som läser disken (Exists) är en egen tabell.
    private static readonly (string Name, string Operation)[] FileOperations =
    [
        ("Exists", "finns"),
        ("Read*", "läsa"), ("OpenRead*", "läsa"), ("OpenText", "läsa"),
        ("Write*", "skriva"), ("Append*", "skriva"), ("Create*", "skriva"), ("OpenWrite*", "skriva"),
        ("Delete*", "ta bort"),
        ("Copy*", "flytta eller kopiera"), ("Move*", "flytta eller kopiera"), ("Replace", "flytta eller kopiera"),
        ("Open", "öppna"),
        ("Get*", "läsa egenskaper"), ("Set*", "ändra egenskaper"),
    ];

    private static readonly (string Name, string Operation)[] DirectoryOperations =
    [
        ("Exists", "finns"),
        ("Create*", "skapa"),
        ("Delete", "ta bort"),
        ("Enumerate*", "lista"), ("GetFiles", "lista"), ("GetDirectories", "lista"), ("GetFileSystemEntries", "lista"), ("GetFileSystemInfos", "lista"),
        ("Move*", "flytta eller kopiera"),
        ("GetCreationTime*", "läsa egenskaper"), ("GetLastAccessTime*", "läsa egenskaper"), ("GetLastWriteTime*", "läsa egenskaper"),
        ("SetCreationTime*", "ändra egenskaper"), ("SetLastAccessTime*", "ändra egenskaper"), ("SetLastWriteTime*", "ändra egenskaper"),
    ];

    /// <summary>FileInfo och DirectoryInfo: samma operationer som File och Directory, med namnen som metoderna på instansen har (<c>CopyTo</c>, <c>MoveTo</c>, <c>Delete</c>).</summary>
    private static readonly (string Name, string Operation)[] FileInfoOperations =
    [
        ("Delete", "ta bort"), ("CopyTo", "flytta eller kopiera"), ("MoveTo", "flytta eller kopiera"), ("Replace", "flytta eller kopiera"),
        ("OpenRead", "läsa"), ("OpenText", "läsa"), ("OpenWrite", "skriva"), ("AppendText", "skriva"), ("Create*", "skriva"), ("Open", "öppna"),
    ];

    private static readonly (string Name, string Operation)[] DirectoryInfoOperations =
    [
        ("Create*", "skapa"), ("Delete", "ta bort"), ("MoveTo", "flytta eller kopiera"),
        ("Enumerate*", "lista"), ("GetFiles", "lista"), ("GetDirectories", "lista"), ("GetFileSystemInfos", "lista"),
    ];

    /// <summary>Egenskaper på <c>FileInfo</c> och <c>DirectoryInfo</c> som frågar disken: <c>Exists</c> och filens <c>Length</c>.</summary>
    private static readonly (string Name, string Operation)[] FileInfoProperties = [("Exists", "finns"), ("Length", "läsa egenskaper")];

    private static SinkHit? FileSystem(Call c)
    {
        if (c.Method?.ContainingType is not { } type) return null;
        var (table, noun, instance) = Symbols.FullName(type) switch
        {
            "System.IO.File" => (FileOperations, "fil", false),
            "System.IO.Directory" => (DirectoryOperations, "mapp", false),
            "System.IO.FileInfo" or "System.IO.FileSystemInfo" => (FileInfoOperations, "fil", true),
            "System.IO.DirectoryInfo" => (DirectoryInfoOperations, "mapp", true),
            _ => (null, "", false),
        };
        if (table is null || OperationOf(c.Name, table) is not { } operation) return null;
        // Statiskt: sökvägen är första argumentet. På en instans (new FileInfo("ut.txt").Delete()): den som instansen skapades med.
        var path = instance ? c.OriginText("new", type.Name == "DirectoryInfo" ? "DirectoryInfo" : "FileInfo")
                            : c.Arguments.FirstOrDefault() is { } first ? PathArgument(first.Expression, c.Model) : null;
        return new SinkHit(FileIntegration, path is null ? $"{operation} en {noun}" : $"{operation} {path}", c.Name, null, null);
    }

    /// <summary><c>new FileInfo("ut.txt").Exists</c> och <c>info.Length</c>: en fråga till disken utan anrop.</summary>
    public static SinkHit? MatchProperty(MemberAccessExpressionSyntax access, SinkContext ctx)
    {
        if (OperationOf(access.Name.Identifier.ValueText, FileInfoProperties) is null) return null;   // samma namn som tabellen, innan symbolen slås upp för varje medlemsåtkomst
        if (ctx.Model.GetSymbolInfo(access).Symbol is not IPropertySymbol { IsStatic: false } property || property.ContainingType is not { } type) return null;
        var fullName = Symbols.FullName(type);
        if (fullName is not ("System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.FileSystemInfo") ||
            OperationOf(property.Name, FileInfoProperties) is not { } operation) return null;
        var directory = fullName == "System.IO.DirectoryInfo" || Symbols.Is(ctx.Model.GetTypeInfo(access.Expression).Type, "System.IO.DirectoryInfo");
        var origins = ctx.Origins.Of(access.Expression, ctx.Model);
        var path = origins.FirstOrDefault(f => f.Kind == "new" && f.Name is "FileInfo" or "DirectoryInfo" && f.Text is not null)?.Text;
        return new SinkHit(FileIntegration, path is null ? $"{operation} en {(directory ? "mapp" : "fil")}" : $"{operation} {path}", property.Name, null, null);
    }

    private static SinkHit? Email(Call c)
    {
        var smtp = c.Name is "Send" or "SendAsync" or "SendMailAsync" && c.On("System.Net.Mail.SmtpClient");
        var mailKit = c.Name is "Send" or "SendAsync" && c.On("MailKit.Net.Smtp.SmtpClient", "MailKit.MailTransport", "MailKit.IMailTransport");
        return smtp || mailKit ? new SinkHit(EmailIntegration, "skicka e-post", c.Name, c.Sent, null) : null;
    }

    // --- WCF och SOAP ----------------------------------------------------------------------

    private static SinkHit? Wcf(Call c)
    {
        if (c.Method is not { } m) return null;
        var owner = c.ReceiverType ?? m.ContainingType;
        INamedTypeSymbol? contract = null;
        var technique = "WCF/SOAP";
        if (Symbols.Find(owner, "System.ServiceModel.ClientBase`1") is { } clientBase && !Symbols.FullName(m.ContainingType).StartsWith("System.ServiceModel."))
            contract = clientBase.TypeArguments.FirstOrDefault() as INamedTypeSymbol ?? clientBase;
        else if (m.ContainingType.TypeKind == TypeKind.Interface && Symbols.HasAttribute(m.ContainingType, "ServiceContract"))
            contract = m.ContainingType;
        else if (Symbols.Find(owner, "System.Web.Services.Protocols.SoapHttpClientProtocol") is not null && !Symbols.FullName(m.ContainingType).StartsWith("System."))
        {
            contract = owner as INamedTypeSymbol;
            technique = "SOAP, asmx";
        }
        if (contract is null) return null;
        var name = Plain(contract.Name);
        var operation = m.Name.EndsWith("Async") ? m.Name[..^5] : m.Name;
        return new SinkHit(new Integration("wcf:" + contract.Name, IntegrationKind.Wcf, name, [technique]), operation, m.Name, c.Sent, DataShapes.Of(m.ReturnType));
    }

    // --- Hjälpare --------------------------------------------------------------------------

    /// <summary>"ILagerService" blir "LagerService".</summary>
    private static string Plain(string name) => name.Length > 2 && name[0] == 'I' && char.IsUpper(name[1]) ? name[1..] : name;

    /// <summary>En sökväg: en konstant, en interpolerad sträng (<c>$"orders/{id}"</c>) eller <c>new Uri("...")</c>.</summary>
    private static string? PathArgument(ExpressionSyntax expression, SemanticModel model, Bindings? bindings = null) =>
        Text(expression, model, bindings) ??
        (expression is BaseObjectCreationExpressionSyntax uri ? uri.ArgumentList?.Arguments.Select(a => Text(a.Expression, model, bindings)).FirstOrDefault(s => s is not null) : null);

    /// <summary>
    /// En strängkonstant eller en interpolerad sträng där hålen skrivs som <c>{namn}</c>. En parameter ger texten hos anroparen
    /// när <paramref name="bindings"/> binder den.
    /// </summary>
    internal static string? Text(ExpressionSyntax expression, SemanticModel model, Bindings? bindings = null)
    {
        if (Symbols.ConstantString(expression, model) is { } s) return s;
        if (expression is IdentifierNameSyntax && bindings?.Of(model.GetSymbolInfo(expression).Symbol) is { } bound) return Text(bound.Expression, bound.Model, bound.Outer);
        if (expression is not InterpolatedStringExpressionSyntax interpolated) return null;
        return string.Concat(interpolated.Contents.Select(part => part switch
        {
            InterpolatedStringTextSyntax text => text.TextToken.ValueText,
            InterpolationSyntax hole => "{" + hole.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                IdentifierNameSyntax id => id.Identifier.ValueText,
                _ => "…",
            } + "}",
            _ => "",
        }));
    }

    /// <summary>Första strängen någonstans bland argumenten, även inne i <c>new CommandDefinition(Sql, ...)</c>.</summary>
    private static string? DeepText(BaseArgumentListSyntax arguments, SemanticModel model) =>
        arguments.Arguments.SelectMany(a => a.Expression.DescendantNodesAndSelf().OfType<ExpressionSyntax>())
            .Select(e => Text(e, model)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
}
