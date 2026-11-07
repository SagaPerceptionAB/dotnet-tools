using System.Net;

namespace SystemMap.AspNetCore;

/// <summary>Väntesidan, instruktionssidan och felsidan: små och utan animationer, i samma stil som kartsidan.</summary>
internal static class Pages
{
    private const string Style = """
        <style>
          * { box-sizing: border-box; }
          body { margin: 0; background: #f5f7fa; color: #1b2430; font: 16px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; }
          main { max-width: 720px; margin: 0 auto; padding: 48px 24px; }
          h1 { font-size: 24px; margin: 0 0 12px; }
          p { margin: 0 0 12px; }
          pre { background: #fff; border: 1px solid #d5dbe3; border-radius: 6px; padding: 12px; overflow: auto; white-space: pre-wrap; }
          code { background: #e8ecf1; padding: 1px 4px; border-radius: 3px; }
          a { color: #1f5fbf; }
        </style>
        """;

    public static string Waiting(int seconds) => Page($$"""
        <h1>Kartan ritas …</h1>
        <p>Läser koden. Första gången kan det ta upp till en minut.</p>
        <p>Det har gått <span id="seconds">{{seconds}}</span> sekunder.</p>
        <script>
        (function () {
          var base = location.pathname.replace(/\/$/, "");
          var seconds = document.getElementById("seconds");
          function poll() {
            fetch(base + "/status", { cache: "no-store" })
              .then(function (r) { return r.json(); })
              .then(function (s) {
                if (s.state !== "running") { location.reload(); return; }
                seconds.textContent = s.seconds;
                setTimeout(poll, 1000);
              })
              .catch(function () { setTimeout(poll, 1000); });
          }
          setTimeout(poll, 1000);
        })();
        </script>
        """);

    public static string Instructions() => Page("""
        <h1>Kartan har inte ritats</h1>
        <p>Kartan har inte ritats i det här bygget.</p>
        <p>Bygg i Release, eller bygg med <code>-p:GenerateSystemMap=true</code>.</p>
        <p>Kontrollera också att paketet <code>SagaPerception.SystemMap.AspNetCore</code> är installerat i projektet.</p>
        """);

    public static string Failed(string message) => Page($"""
        <h1>Kartan gick inte att rita</h1>
        <pre>{WebUtility.HtmlEncode(message)}</pre>
        <p><a href="?retry=1">Försök igen</a></p>
        """);

    private static string Page(string body) => $"""
        <!DOCTYPE html>
        <html lang="sv">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Systemkarta</title>
        {Style}
        </head>
        <body>
        <main>
        {body}
        </main>
        </body>
        </html>
        """;
}
