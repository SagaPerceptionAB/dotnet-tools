using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SystemMap.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Systemkartan som en sida i appen, som Swagger UI: <c>app.MapSystemMap();</c></summary>
public static class SystemMapEndpointRouteBuilderExtensions
{
    // Samma som <meta> i kartsidan; väntesidan frågar dessutom status på samma adress.
    private const string PageCsp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; base-uri 'none'; form-action 'none'";
    private const string WaitingCsp = PageCsp + "; connect-src 'self'";

    /// <summary>
    /// Kartan på <paramref name="pattern"/> (standard /systemmap), med map.json och, i Development, status. Utanför Development
    /// visas kartan bara om adressen kräver inloggning (<c>.RequireAuthorization()</c> på resultatet) eller om
    /// <see cref="SystemMapOptions.AllowWithoutAuthorization"/> är satt; annars ger alla adresser 404.
    /// </summary>
    public static RouteGroupBuilder MapSystemMap(this IEndpointRouteBuilder app, string pattern = "/systemmap", Action<SystemMapOptions>? configure = null)
    {
        var options = new SystemMapOptions();
        configure?.Invoke(options);
        var runner = new DevMapRunner(options.Directory);
        var warned = 0;
        var group = app.MapGroup(pattern);

        // Sant om adressen får visas. Annars är svaret redan 404.
        bool Allowed(HttpContext ctx)
        {
            if (ctx.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment() || options.AllowWithoutAuthorization) return true;
            var metadata = ctx.GetEndpoint()?.Metadata;
            if (metadata?.GetMetadata<IAuthorizeData>() is not null && metadata.GetMetadata<IAllowAnonymous>() is null) return true;
            if (Interlocked.Exchange(ref warned, 1) == 0)
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SystemMap").LogWarning(
                    "Systemkartan på {Pattern} visas bara i Development. Lägg till .RequireAuthorization(...) på MapSystemMap, " +
                    "eller sätt AllowWithoutAuthorization, för att visa den i andra miljöer.", pattern);
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return false;
        }

        bool Development(HttpContext ctx) => ctx.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment();

        async Task Page(HttpContext ctx)
        {
            if (!Allowed(ctx)) return;
            var built = Path.Combine(options.Directory, "systemmap.html");
            if (File.Exists(built)) { await WriteHtml(ctx, await File.ReadAllTextAsync(built), PageCsp); return; }
            if (!Development(ctx)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            if (!File.Exists(runner.DevJson)) { await WriteHtml(ctx, Pages.Instructions(), PageCsp); return; }

            if (ctx.Request.Query.ContainsKey("retry"))
            {
                runner.Retry();
                ctx.Response.Redirect(ctx.Request.PathBase + ctx.Request.Path);
                return;
            }
            var (state, seconds, message) = runner.Get();
            switch (state)
            {
                case DevMapState.Ready when File.Exists(runner.HtmlFile):
                    await WriteHtml(ctx, await File.ReadAllTextAsync(runner.HtmlFile), PageCsp);
                    break;
                case DevMapState.Ready:
                    await WriteHtml(ctx, Pages.Failed("Kartan finns inte längre."), PageCsp);
                    break;
                case DevMapState.Failed:
                    await WriteHtml(ctx, Pages.Failed(message), PageCsp);
                    break;
                default:
                    await WriteHtml(ctx, Pages.Waiting(seconds), WaitingCsp);
                    break;
            }
        }

        async Task Json(HttpContext ctx)
        {
            if (!Allowed(ctx)) return;
            var file = Path.Combine(options.Directory, "map.json");
            if (!File.Exists(Path.Combine(options.Directory, "systemmap.html")))
            {
                var ready = Development(ctx) && File.Exists(runner.DevJson) && runner.Get(startIfNeeded: false).State == DevMapState.Ready;
                file = ready ? runner.JsonFile : "";
            }
            if (!File.Exists(file)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            await ctx.Response.SendFileAsync(file);
        }

        async Task Status(HttpContext ctx)
        {
            if (!Allowed(ctx)) return;
            if (!Development(ctx) || !File.Exists(runner.DevJson)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            var (state, seconds, message) = runner.Get(startIfNeeded: false);
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.WriteAsJsonAsync(new
            {
                state = state == DevMapState.Ready ? "ready" : state == DevMapState.Failed ? "failed" : "running",
                seconds,
                message,
            });
        }

        group.MapGet("/", Page);
        group.MapGet("/map.json", Json);
        group.MapGet("/status", Status);
        return group;
    }

    private static Task WriteHtml(HttpContext ctx, string html, string csp)
    {
        var headers = ctx.Response.Headers;
        headers.ContentSecurityPolicy = csp;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-cache";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        return ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(html)).AsTask();
    }
}
