using System.Collections.Concurrent;
using System.Reflection;
using TestGen.Browser.Forms;
using TestGen.Core;
using TestGen.Core.Projects;
using TestGen.Core.Run;
using TestGen.Core.Views;

namespace SagaPerception.TestGen.NUnit;

internal enum UiKind { Page, Links, Form, Boundary, Invalid, ServerError, Accessibility, Screenshot }

/// <summary>Ett API som appen anropar: konfigurationsnyckeln för adressen och specen som mocken byggs ur.</summary>
internal sealed record ApiBinding(ViewsApi Api, string ConfigKey, ApiSpec Spec);

/// <summary>Ett formulär med sina värden och, om specen har den, operationen det skickas till.</summary>
internal sealed record FormCase(Form Form, FormPlan Plan, OperationInfo? Operation, ApiBinding? Api);

/// <summary>Ett UI-test. Namnet är <c>U-004 /customers/create gränsvärde name (max-length)</c>, nyckeln <c>ui:/customers/create:boundary-max-length:name</c>.</summary>
internal sealed record UiCase(string Id, string Key, UiKind Kind, View View, string Description, string Category,
    FormCase? Form = null, BoundaryCase? Boundary = null, InvalidCase? Invalid = null)
{
    public string Name => $"{Id} {View.Route} {Description}";
}

/// <summary>UI-testfallen för en webbapp, planerade ur views.json och specarna utan att något körs. Delas mellan upptäckten och körningen.</summary>
internal sealed class ViewPlan
{
    public const string ErrorName = "testgen kunde inte förbereda testerna";

    private static readonly ConcurrentDictionary<(string Assembly, string Project), ViewPlan> Cache = new();

    private ViewPlan(ViewTarget? target, IReadOnlyList<UiCase> cases, IReadOnlyList<ApiBinding> apis, string? viewsPath, string? error)
    {
        Target = target;
        Cases = cases;
        Apis = apis;
        ViewsPath = viewsPath;
        Error = error;
        Keys = TestKeys.Unique(cases.Select(c => c.Key));
    }

    /// <summary>Null när det inte gick att förbereda; då finns <see cref="Error"/>.</summary>
    public ViewTarget? Target { get; }

    public IReadOnlyList<UiCase> Cases { get; }

    public IReadOnlyList<ApiBinding> Apis { get; }

    /// <summary>views.json, relativt solutionen.</summary>
    public string? ViewsPath { get; }

    /// <summary>Varför testerna inte kunde planeras (på svenska, med vad man ska göra), annars null.</summary>
    public string? Error { get; }

    /// <summary>Stabil nyckel per test, i samma ordning som <see cref="Cases"/>.</summary>
    public IReadOnlyList<string> Keys { get; }

    public static ViewPlan For(Assembly testAssembly, string project) =>
        Cache.GetOrAdd((testAssembly.Location, project), _ => Create(() => ViewTarget.Resolve(testAssembly, project), testAssembly, project));

    /// <summary>Planerar testerna. Ett fel i konfigurationen blir <see cref="Error"/>, inte ett undantag.</summary>
    internal static ViewPlan Create(Func<ViewTarget> resolve, Assembly testAssembly, string project)
    {
        try
        {
            var target = resolve();
            var path = ViewsFileOf(target, testAssembly);
            var app = ViewsJson.TryRead(path)?.Apps.FirstOrDefault(a => a.Project.Equals(project, StringComparison.OrdinalIgnoreCase))
                ?? throw new UsageException($"views.json saknar vyerna för {project}.", "Kör testgen views, så skrivs views.json i testprojektet. Checka in den.");
            var apis = BindApis(target, app);
            return new ViewPlan(target, Plan(target, app, apis), apis, Terminal.Relative(path, target.Facts.SolutionDirectory), null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new ViewPlan(null, [], [], null, string.Join(Environment.NewLine, FriendlyErrors.Translate(e).Lines(verbose: true, prefix: "")));
        }
    }

    /// <summary>views.json bredvid testassemblyn (kopierad vid bygget), annars den som <c>testgen views</c> skrev i testprojektet.</summary>
    private static string ViewsFileOf(ViewTarget target, Assembly testAssembly)
    {
        var copy = Path.Combine(Path.GetDirectoryName(testAssembly.Location) ?? "", ViewsJson.FileName);
        if (File.Exists(copy)) return copy;
        return target.Facts.ViewsFilePath
            ?? throw new UsageException($"Hittar ingen {ViewsJson.FileName}.", "Kör testgen views, så skrivs filen i testprojektet. Checka in den.");
    }

    private static List<ApiBinding> BindApis(ViewTarget target, AppViews app)
    {
        var context = new RepositoryContext(target.Facts, target.Settings);
        var bindings = new List<ApiBinding>();
        foreach (var api in app.Apis)
        {
            var project = target.Facts.Apis.FirstOrDefault(p => p.Name.Equals(api.Project, StringComparison.OrdinalIgnoreCase))
                ?? throw new UsageException($"{app.Project} anropar {api.Project}, som inte finns i solutionen.", "Lägg API-projektet i solutionen, eller kör testgen views igen.");
            if (api.ConfigKey is null)
                throw new UsageException($"{app.Project} läser adressen till {api.Project} inte ur någon konfigurationsnyckel, så den kan inte pekas om till mocken.",
                    $"Läs adressen ur konfigurationen (t.ex. {api.Project}:BaseUrl) och kör testgen views igen.");
            var spec = context.SpecFor(project, null);
            bindings.Add(new ApiBinding(api, api.ConfigKey, OpenApiLoader.LoadSpec(spec)));
        }
        return bindings;
    }

    private static List<UiCase> Plan(ViewTarget target, AppViews app, IReadOnlyList<ApiBinding> apis)
    {
        var cases = new List<UiCase>();
        var seed = target.Settings.Seed;

        void Add(UiKind kind, View view, string description, string category, string sort, string? field = null,
            FormCase? form = null, BoundaryCase? boundary = null, InvalidCase? invalid = null) =>
            cases.Add(new UiCase($"U-{cases.Count + 1:000}", field is null ? $"{view.Id}:{sort}" : $"{view.Id}:{sort}:{field}", kind, view, description, category, form, boundary, invalid));

        void PlanForm(View view, Form form)
        {
            var found = form.OperationId is { } id
                ? apis.SelectMany(a => a.Spec.Operations.Where(o => o.OperationId == id).Select(o => (Api: a, Operation: o))).FirstOrDefault()
                : default;
            var plan = FormPlan.Build(view, form, seed, found.Operation?.BodySchema);
            var formCase = new FormCase(form, plan, found.Operation, found.Api);
            Add(UiKind.Form, view, "formulär", "form", "form", form: formCase);
            if (plan.Warning is not null) return;
            foreach (var boundary in Boundaries.Find(plan, found.Operation?.BodySchema))
                Add(UiKind.Boundary, view, $"gränsvärde {boundary.Field.Name} ({boundary.Id})", "boundary", $"boundary-{boundary.Id}", boundary.Field.Name, formCase, boundary);
            foreach (var invalid in plan.Cases)
            {
                var rule = Kebab(invalid.Rule.Kind.ToString());
                Add(UiKind.Invalid, view, $"ogiltigt {invalid.Field.Name} ({rule})", "form", $"invalid-{rule}", invalid.Field.Name, formCase, invalid: invalid);
            }
            if (found.Operation is not null) Add(UiKind.ServerError, view, "tjänstefel", "form", "server-error", form: formCase);
        }

        foreach (var view in app.Views)
        {
            Add(UiKind.Page, view, "sida", "ui", "page");
            if (view.RequiresAuth) continue;
            if (view.Links.Count > 0) Add(UiKind.Links, view, "länkar", "ui", "links");
            foreach (var form in view.Forms) PlanForm(view, form);
            if (target.Settings.UiChecks.Contains("accessibility")) Add(UiKind.Accessibility, view, "tillgänglighet", "accessibility", "accessibility");
            if (target.Settings.UiChecks.Contains("screenshots")) Add(UiKind.Screenshot, view, "skärmdump", "screenshot", "screenshot");
        }
        return cases;
    }

    private static string Kebab(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));

    public IEnumerable<TestCaseData> CaseData()
    {
        if (Error is not null)
        {
            yield return new TestCaseData(ViewTests.ErrorKind, "").SetName(ErrorName);
            yield break;
        }
        for (var i = 0; i < Cases.Count; i++)
        {
            var data = new TestCaseData(ViewTests.TestKind, Cases[i].Id).SetName(Cases[i].Name).SetCategory("ui");
            if (Cases[i].Category != "ui") data.SetCategory(Cases[i].Category);
            if (Cases[i].Kind == UiKind.Form) data.SetCategory("mutating");
            data.SetProperty("key", Keys[i]);
            yield return data;
        }
    }
}
