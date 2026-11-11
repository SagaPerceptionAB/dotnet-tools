using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Sidan i en riktig webbläsare: filterraden (sökbara listor med flerval för Anrop, Projekt, Klass, Funktion och Integration) och Rensa.</summary>
public partial class CodeMapTests
{
    // --- Hjälpmetoder för listorna ----------------------------------------------------------------

    /// <summary>Öppnar listan och kryssar (eller väljer nivå) ett eller flera alternativ efter texten, och stänger den.</summary>
    private static async Task PickAsync(IPage page, string field, params string[] texts)
    {
        await page.ClickAsync($"#map-{field}");
        foreach (var text in texts)
            await page.ClickAsync($"#map-{field}-list li[role=option]:has(.map-text:text-is(\"{text}\"))");
        await page.Keyboard.PressAsync("Escape");
    }

    /// <summary>Alternativen i den öppna listan som syns (nivåvalen först), som de står.</summary>
    private static Task<string[]> ListOptionsAsync(IPage page, string field) =>
        page.EvaluateAsync<string[]>($"() => [...document.querySelectorAll('#map-{field}-list [role=option]')].filter(o => !o.hidden).map(o => o.textContent)");

    /// <summary>De valda alternativen (nivåvalet och kryssen) i den öppna listan.</summary>
    private static Task<string[]> SelectedOptionsAsync(IPage page, string field) =>
        page.EvaluateAsync<string[]>($"() => [...document.querySelectorAll('#map-{field}-list [role=option][aria-selected=true]')].filter(o => !o.hidden).map(o => o.textContent)");

    /// <summary>Alternativen i en lista, som de står: öppnar listan, läser den och stänger den.</summary>
    private static async Task<string[]> OptionTextsAsync(IPage page, string field)
    {
        await page.ClickAsync($"#map-{field}");
        var texts = await ListOptionsAsync(page, field);
        await page.Keyboard.PressAsync("Escape");
        return texts;
    }

    /// <summary>Det som skärmläsaren senast fick höra.</summary>
    // Skärmläsarens text. announce() tömmer rutan och skriver texten en stund senare (så att samma text läses upp igen), så vänta tills den finns.
    private static async Task<string> LiveAsync(IPage page)
    {
        await page.WaitForFunctionAsync("() => document.getElementById('map-live').textContent !== ''");
        return await page.EvaluateAsync<string>("() => document.getElementById('map-live').textContent");
    }

    /// <summary>Klickar på anropets nod i kartan.</summary>
    private static Task TapEntryAsync(IPage page, string label) =>
        page.EvaluateAsync("label => { SystemMap.cy().nodes('.t-entry').filter(n => n.data('label') === label)[0].emit('tap'); }", label);

    /// <summary>Nivån som sparats i webbläsaren.</summary>
    private static Task<string> StoredLevelAsync(IPage page) => page.EvaluateAsync<string>("() => sessionStorage.getItem('map-level')");

    /// <summary>Grupperna i en lista som "etikett (antal)", utan nivåvalen.</summary>
    private static async Task<string[]> GroupsAsync(IPage page, string field)
    {
        await page.ClickAsync($"#map-{field}");
        var groups = await page.EvaluateAsync<string[]>(
            $"() => [...document.querySelectorAll('#map-{field}-list [role=group]')].filter(g => g.getAttribute('aria-label') !== 'Nivå').map(g => g.getAttribute('aria-label') + ' (' + g.children.length + ')')");
        await page.Keyboard.PressAsync("Escape");
        return groups;
    }

    /// <summary>Väljer detaljnivån (projects, classes, methods eller all) med ett val i en lista: Klass Visas inte, Funktion Visas inte (classes, när nivån är högre),
    /// Funktion Alla (methods, från classes) och Integration med läsningar och skrivningar (all).</summary>
    private static Task LevelAsync(IPage page, string level) => level switch
    {
        "projects" => PickAsync(page, "cls", "Visas inte"),
        "classes" => PickAsync(page, "fn", "Visas inte"),
        "methods" => PickAsync(page, "fn", "Alla"),
        _ => PickAsync(page, "integ", "Alla, med läsningar och skrivningar"),
    };

    private const string PathEntries = "() => SystemMap.cy().nodes('.t-entry').filter(n => n.hasClass('path')).map(n => n.data('label')).sort()";
    private const string DimmedEntries = "() => SystemMap.cy().nodes('.t-entry').filter(n => n.hasClass('dimmed')).map(n => n.data('label')).sort()";
    private const string EntryNodes = "() => SystemMap.cy().nodes('.t-entry').map(n => n.data('label')).sort()";

    private static Task<bool> HasValueAsync(IPage page, string field) =>
        page.EvaluateAsync<bool>($"() => document.getElementById('map-{field}').parentElement.classList.contains('has-value')");

    // --- Filtret ------------------------------------------------------------------------------------

    [Test]
    public async Task Page_filter_on_a_class_keeps_only_the_calls_through_it_and_the_parts_they_pass_and_shows_the_status_box()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.IsHiddenAsync("#map-status"), Is.True, "inget filter: ingen statusruta");
            Assert.That(await page.GetAttributeAsync("#map-clear", "aria-disabled"), Is.EqualTo("true"));
            Assert.That(await page.EvaluateAsync<string[]>(EntryNodes), Has.Length.EqualTo(5));

            await PickAsync(page, "cls", "AuthService");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(EntryNodes), Is.EqualTo(new[] { "POST /v1/auth/in", "POST /v1/auth/out" }));
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(new[] { "AuthClient", "AuthController", "AuthService" }), "bara delarna anropen går genom");
            Assert.That(await page.IsVisibleAsync("#map-status"), Is.True);
            Assert.That(await page.InnerTextAsync("#map-status"), Is.EqualTo("2 av 5 anrop"), "bara antalet: fälten säger vad som är valt");
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("AuthService"));
            Assert.That(await HasValueAsync(page, "cls"), Is.True, "blå kant för ett valt filter");
            Assert.That(await page.InputValueAsync("#map-project"), Is.EqualTo("Alla"), "projektet fylls inte i");
            Assert.That(await page.GetAttributeAsync("#map-clear", "aria-disabled"), Is.EqualTo("false"));
            Assert.That(await page.IsVisibleAsync("button[aria-label='Ta bort filtret Klass']"), Is.True);
            Assert.That(await page.IsHiddenAsync("button[aria-label='Ta bort filtret Projekt']"), Is.True);
            Assert.That(await OptionTextsAsync(page, "entry"), Is.EqualTo(new[] { "POST /v1/auth/in", "POST /v1/auth/out" }), "Anrop visar bara anropen genom filtret");
            Assert.That(await LiveAsync(page), Is.EqualTo("2 av 5 anrop går genom AuthService."));

            // Kartans inpassning lämnar plats för rutan.
            Assert.That(await page.EvaluateAsync<bool>("() => SystemMap.cy().elements().not('.anchor').renderedBoundingBox().y1 >= document.getElementById('map-status').offsetHeight + 10"), Is.True);

            // Det går att ta bort just det valet med ×.
            await page.ClickAsync("button[aria-label='Ta bort filtret Klass']");
            await ready();
            Assert.That(await page.IsHiddenAsync("#map-status"), Is.True);
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("Alla"));
            Assert.That(await page.EvaluateAsync<string[]>(EntryNodes), Has.Length.EqualTo(5));
        });
    }

    [Test]
    public async Task Page_filter_with_several_ticks_in_one_list_is_the_union_and_the_field_counts_them()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await PickAsync(page, "cls", "AuthService", "MeController");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(EntryNodes), Is.EqualTo(new[] { "GET /v1/me", "POST /v1/auth/in", "POST /v1/auth/out" }), "vägar genom någon av de valda");
            Assert.That(await page.InnerTextAsync("#map-status"), Is.EqualTo("3 av 5 anrop"));
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("2 klasser"));
            Assert.That(await page.GetAttributeAsync("#map-cls", "title"), Is.EqualTo("AuthService, MeController"), "title listar namnen");
            Assert.That(await LiveAsync(page), Is.EqualTo("3 av 5 anrop går genom AuthService och MeController."));

            await PickAsync(page, "cls", "FamilyService");
            await ready();
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("3 klasser"));
            Assert.That(await LiveAsync(page), Is.EqualTo("5 av 5 anrop går genom 3 klasser."));

            // Ett kryss som tas bort ur listan går tillbaka till de andra.
            await PickAsync(page, "cls", "FamilyService", "MeController");
            await ready();
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("AuthService"));
            Assert.That(await page.EvaluateAsync<string[]>(EntryNodes), Is.EqualTo(new[] { "POST /v1/auth/in", "POST /v1/auth/out" }));
        });
    }

    [Test]
    public async Task Page_projects_limit_the_lists_of_classes_and_functions_and_unticking_a_project_removes_what_is_under_it()
    {
        var map = await WithUnreached();
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await OptionTextsAsync(page, "project"), Is.EqualTo(new[] { "Api", "Contracts", "Lib", "Tools" }));
            Assert.That(await GroupsAsync(page, "cls"), Is.EqualTo(new[] { "Api (3)", "Lib (2)", "Tools (1)" }), "klasserna grupperade per projekt");

            await PickAsync(page, "project", "Api");
            await ready();
            Assert.That(await GroupsAsync(page, "cls"), Is.EqualTo(new[] { "Api (3)" }), "ett valt projekt begränsar Klass");
            Assert.That(await GroupsAsync(page, "fn"), Does.Contain("Api / OrderService (2)").And.Not.Contain("Lib / Store (1)"), "och Funktion, grupperad per projekt och klass");
            Assert.That(await page.InputValueAsync("#map-project"), Is.EqualTo("Api"));

            // Projekt plus klass i det projektet: den mest precisa listan gäller. Projektet står kvar.
            await PickAsync(page, "cls", "OrderService");
            await ready();
            Assert.That(await page.InputValueAsync("#map-project"), Is.EqualTo("Api"));
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("OrderService"));
            Assert.That(await page.EvaluateAsync<string[]>(EntryNodes), Is.EqualTo(new[] { "GET /orders" }));
            Assert.That(await GroupsAsync(page, "fn"), Is.EqualTo(new[] { "Api / OrderService (2)" }), "en vald klass begränsar Funktion");

            // Ett annat projekt tar bort klassen som inte hör dit, och att kryssa av projektet tar bort det under det.
            await PickAsync(page, "project", "Lib");
            await ready();
            Assert.That(await page.InputValueAsync("#map-project"), Is.EqualTo("2 projekt"));
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("OrderService"), "klassen hör till Api, som fortfarande är kryssat");
            await PickAsync(page, "project", "Api");
            await ready();
            Assert.That(await page.InputValueAsync("#map-project"), Is.EqualTo("Lib"));
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("Alla"), "kryssen under det avkryssade projektet är borta");
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(new[] { "OrderService", "OrdersController", "Store" }), "vägarna genom Lib: de som anropar Store visas också, men LegacyExporter och andra som ingen väg når är borta");
        });
    }

    [Test]
    public async Task Page_filter_on_a_function_opens_its_class_and_shows_the_function_whatever_the_level()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await GroupsAsync(page, "fn"), Does.Contain("Api / AuthService (1)").And.Contain("Api / AuthController (2)"));
            Assert.That((await OptionTextsAsync(page, "fn")).Take(2), Is.EqualTo(new[] { "Visas inte", "Alla" }));
            Assert.That(await OptionTextsAsync(page, "fn"), Does.Contain("Check()"));

            await PickAsync(page, "fn", "Check()");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(Methods), Is.EqualTo(new[] { "AuthService.Check()" }), "funktionen syns i sin utfällda klass");
            Assert.That(await page.InputValueAsync("#map-fn"), Is.EqualTo("Check()"));
            Assert.That(await page.InnerTextAsync("#map-status"), Is.EqualTo("2 av 5 anrop"));
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("Alla"), "klassen fylls inte i");
            Assert.That(await LiveAsync(page), Is.EqualTo("2 av 5 anrop går genom AuthService.Check."));
        });
    }

    [Test]
    public async Task Page_filter_on_an_integration_works_with_the_parts_and_a_part_that_no_call_reaches_gives_no_calls()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            var options = await OptionTextsAsync(page, "integ");
            Assert.That(options.Take(2), Is.EqualTo(new[] { "Alla", "Alla, med läsningar och skrivningar" }));
            Assert.That(options, Has.Length.EqualTo(3));
            var integration = options[2];
            Assert.That(await GroupsAsync(page, "integ"), Is.EqualTo(new[] { "Annat system (HTTP) (1)" }), "integrationerna grupperade per typ");

            await PickAsync(page, "integ", integration);
            await ready();
            Assert.That(await page.InputValueAsync("#map-integ"), Is.EqualTo(integration));
            Assert.That(await page.InnerTextAsync("#map-status"), Is.EqualTo("5 av 5 anrop"));
            Assert.That(await LiveAsync(page), Does.StartWith("5 av 5 anrop går till "));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-integration').length"), Is.EqualTo(1), "integrationen visas");

            // Och med en kod: OCH.
            await PickAsync(page, "cls", "MeController");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(EntryNodes), Is.EqualTo(new[] { "GET /v1/me" }));
            Assert.That(await page.InnerTextAsync("#map-status"), Is.EqualTo("1 av 5 anrop"));
            Assert.That(await LiveAsync(page), Does.Contain("går genom MeController till "));
        });

        var unreached = await WithUnreached();
        await WithPageAsync(unreached, async (page, ready) =>
        {
            await PickAsync(page, "cls", "LegacyExporter");
            await ready();
            Assert.That(await page.InnerTextAsync("#map-status"), Is.EqualTo("Inga anrop"));
            Assert.That(await LiveAsync(page), Is.EqualTo("Inga anrop går genom LegacyExporter."));
            Assert.That(await page.EvaluateAsync<string[]>(EntryNodes), Is.Empty);
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().getElementById('class:Api/LegacyExporter').length"), Is.EqualTo(1), "den valda delen visas alltid");
            Assert.That(await OptionTextsAsync(page, "entry"), Is.Empty);
        });
    }

    // --- Anrop med flerval --------------------------------------------------------------------------

    [Test]
    public async Task Page_several_ticked_calls_mark_all_their_paths_and_a_call_that_falls_out_of_the_filter_is_unticked()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.InputValueAsync("#map-entry"), Is.EqualTo("Hela systemet"));
            await PickAsync(page, "entry", "POST /v1/auth/in");
            await ready();
            Assert.That(await page.InputValueAsync("#map-entry"), Is.EqualTo("POST /v1/auth/in"));
            Assert.That(await HasValueAsync(page, "entry"), Is.True);
            Assert.That(await page.EvaluateAsync<string[]>(PathEntries), Is.EqualTo(new[] { "POST /v1/auth/in" }));

            await PickAsync(page, "entry", "GET /v1/me");
            await ready();
            Assert.That(await page.InputValueAsync("#map-entry"), Is.EqualTo("2 anrop"));
            Assert.That(await page.GetAttributeAsync("#map-entry", "title"), Is.EqualTo("POST /v1/auth/in, GET /v1/me"));
            Assert.That(await page.EvaluateAsync<string[]>(PathEntries), Is.EqualTo(new[] { "GET /v1/me", "POST /v1/auth/in" }), "båda vägarna markeras");
            Assert.That(await page.EvaluateAsync<string[]>(DimmedEntries), Is.EqualTo(new[] { "GET /v1/family", "POST /v1/auth/out", "POST /v1/family" }), "resten är dämpat");
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.t-class.path').map(n => n.data('label').split('\\n')[0]).sort()"),
                Is.EqualTo(new[] { "AuthClient", "AuthController", "AuthService", "FamilyService", "MeController" }));
            Assert.That(await page.IsHiddenAsync("#map-status"), Is.True, "Anrop är inget filter");
            Assert.That(await page.GetAttributeAsync("#map-clear", "aria-disabled"), Is.EqualTo("false"));

            // Mermaid kan bara visa ett anrop i taget.
            await page.ClickAsync("#map-mermaid");
            await page.WaitForFunctionAsync("() => document.getElementById('map-mermaid-status').textContent !== ''");
            Assert.That(await page.InnerTextAsync("#map-mermaid-status"), Does.Contain("Mermaid-filen kan bara visa ett anrop i taget.").And.Contain("dotnet systemmap --mermaid karta.mmd --entry"));
            await PickAsync(page, "entry", "GET /v1/me");
            await ready();
            var download = await page.RunAndWaitForDownloadAsync(() => page.ClickAsync("#map-mermaid"));
            Assert.That(download.SuggestedFilename, Is.EqualTo("systemmap-post-v1-auth-in.mmd"), "ett kryssat anrop: som förut");

            // Ett kryss som faller bort ur filtret tas bort.
            await PickAsync(page, "entry", "GET /v1/me");
            await PickAsync(page, "cls", "AuthService");
            await ready();
            Assert.That(await page.InputValueAsync("#map-entry"), Is.EqualTo("POST /v1/auth/in"), "anropet går genom klassen och står kvar");
            await PickAsync(page, "cls", "AuthService", "MeController");
            await ready();
            await PickAsync(page, "cls", "MeController");
            await ready();
            Assert.That(await page.InputValueAsync("#map-entry"), Is.EqualTo("Hela systemet"), "anropet går inte genom MeController");
            Assert.That(await page.EvaluateAsync<string[]>(PathEntries), Is.Empty);
        });
    }

    [Test]
    public async Task Page_clicking_a_call_node_sets_exactly_that_call_and_clicking_it_again_removes_it()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await PickAsync(page, "entry", "GET /v1/me", "POST /v1/auth/in");
            await ready();
            Assert.That(await page.InputValueAsync("#map-entry"), Is.EqualTo("2 anrop"));
            await TapEntryAsync(page, "POST /v1/auth/out");
            await ready();
            Assert.That(await page.InputValueAsync("#map-entry"), Is.EqualTo("POST /v1/auth/out"), "exakt det anropet");
            await TapEntryAsync(page, "POST /v1/auth/out");
            await ready();
            Assert.That(await page.InputValueAsync("#map-entry"), Is.EqualTo("Hela systemet"));
        });
    }

    // --- Rensa --------------------------------------------------------------------------------------

    [Test]
    public async Task Page_Rensa_clears_all_ticks_and_the_level_but_not_the_direction_or_the_choice_about_unreached_parts()
    {
        var map = await WithUnreached();
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.GetAttributeAsync("#map-clear", "aria-disabled"), Is.EqualTo("true"), "inget är valt");
            Assert.That(await page.IsDisabledAsync("#map-clear"), Is.True, "grå: går inte att klicka");

            await page.UncheckAsync("#map-unreached");
            await ready();
            await page.ClickAsync("[data-direction='TB']");
            await ready();
            await PickAsync(page, "entry", "GET /orders");
            await PickAsync(page, "project", "Api");
            await PickAsync(page, "cls", "OrderService");
            await PickAsync(page, "fn", "Used()");
            await LevelAsync(page, "all");
            await ready();
            Assert.That(await page.GetAttributeAsync("#map-clear", "aria-disabled"), Is.EqualTo("false"));
            Assert.That(await StoredLevelAsync(page), Is.EqualTo("all"));

            await page.ClickAsync("#map-clear");
            await ready();
            foreach (var (field, text) in new[] { ("entry", "Hela systemet"), ("project", "Alla"), ("cls", "Alla"), ("fn", "Visas inte"), ("integ", "Alla") })
            {
                Assert.That(await page.InputValueAsync($"#map-{field}"), Is.EqualTo(text), field);
                Assert.That(await HasValueAsync(page, field), Is.False, field);
            }
            Assert.That(await page.GetAttributeAsync("#map-clear", "aria-disabled"), Is.EqualTo("true"), "grå när allt är nollställt");
            Assert.That(await page.IsHiddenAsync("#map-status"), Is.True);
            Assert.That(await StoredLevelAsync(page), Is.EqualTo("classes"), "nivån är Klasser");
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-method, .t-operation').length"), Is.Zero);
            Assert.That(await page.GetAttributeAsync("[data-direction='TB']", "aria-pressed"), Is.EqualTo("true"), "riktningen är orörd");
            Assert.That(await page.IsCheckedAsync("#map-unreached"), Is.False, "valet om det som inte nås är orört");
        });
    }

    // --- Mermaid ------------------------------------------------------------------------------------

    [Test]
    public async Task Page_does_not_save_a_Mermaid_file_that_would_ignore_the_filter()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await PickAsync(page, "cls", "AuthService");
            await ready();
            await page.ClickAsync("#map-mermaid");
            await page.WaitForFunctionAsync("() => document.getElementById('map-mermaid-status').textContent !== ''");
            Assert.That(await page.InnerTextAsync("#map-mermaid-status"),
                Does.Contain("Mermaid-filen kan inte följa filtret.").And.Contain("Välj Rensa, eller skapa filen med dotnet systemmap --mermaid karta.mmd --entry \"<anrop>\"."));

            await page.ClickAsync("#map-clear");
            await ready();
            var download = await page.RunAndWaitForDownloadAsync(() => page.ClickAsync("#map-mermaid"));
            Assert.That(download.SuggestedFilename, Is.EqualTo("systemmap.mmd"), "utan filter sparas filen som vanligt");
        });
    }

    // --- Listorna med tangentbord -------------------------------------------------------------------

    [Test]
    public async Task Page_lists_are_comboboxes_that_search_and_toggle_with_the_keyboard_and_close_with_Escape_or_Tab()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            foreach (var field in new[] { "entry", "project", "cls", "fn", "integ" })
            {
                Assert.That(await page.GetAttributeAsync($"#map-{field}", "role"), Is.EqualTo("combobox"), field);
                Assert.That(await page.GetAttributeAsync($"#map-{field}", "aria-expanded"), Is.EqualTo("false"), field);
                Assert.That(await page.GetAttributeAsync($"#map-{field}", "aria-controls"), Is.EqualTo($"map-{field}-list"), field);
                Assert.That(await page.GetAttributeAsync($"#map-{field}", "aria-autocomplete"), Is.EqualTo("list"), field);
                Assert.That(await page.GetAttributeAsync($"#map-{field}-list", "role"), Is.EqualTo("listbox"), field);
                Assert.That(await page.GetAttributeAsync($"#map-{field}-list", "aria-multiselectable"), Is.EqualTo("true"), field);
            }

            // Pil ned öppnar hela listan, och nivåvalen står överst (radioknappar) före kryssrutorna.
            await page.FocusAsync("#map-cls");
            await page.Keyboard.PressAsync("ArrowDown");
            Assert.That(await page.GetAttributeAsync("#map-cls", "aria-expanded"), Is.EqualTo("true"));
            Assert.That(await ListOptionsAsync(page, "cls"), Is.EqualTo(new[]
                { "Alla", "Visas inte", "AuthClient", "AuthController", "AuthService", "FamilyController", "FamilyService", "MeController" }));
            Assert.That(await page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('#map-cls-list [role=option]')].map(o => o.firstElementChild.className + ':' + o.getAttribute('aria-selected'))"),
                Is.EqualTo(new[] { "map-radio:true", "map-radio:false" }.Concat(Enumerable.Repeat("map-check:false", 6))), "den dekorativa rutan före namnet, och Alla är valt");
            Assert.That(await page.EvaluateAsync<bool>("() => [...document.querySelectorAll('#map-cls-list [role=option]')].every(o => o.firstElementChild.getAttribute('aria-hidden') === 'true')"), Is.True);
            Assert.That(await page.GetAttributeAsync("#map-cls", "aria-activedescendant"), Is.EqualTo("map-cls-o0"));

            // Skriv: filtrerar (skiftlägesokänsligt, delsträng) och markerar träffen.
            await page.Keyboard.TypeAsync("SERVICE");
            Assert.That(await ListOptionsAsync(page, "cls"), Is.EqualTo(new[] { "Alla", "Visas inte", "AuthService", "FamilyService" }), "nivåvalen står kvar vid sökning");
            Assert.That(await page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('#map-cls-list mark')].map(m => m.textContent)"), Is.EqualTo(new[] { "Service", "Service" }));
            Assert.That(await page.EvaluateAsync<int>("() => document.querySelectorAll('#map-cls-list .active').length"), Is.EqualTo(1), "bara ett alternativ är markerat, första träffen");

            // Pil ned och Enter kryssar. Listan står kvar öppen.
            await page.Keyboard.PressAsync("ArrowDown");
            await page.Keyboard.PressAsync("Enter");
            await ready();
            Assert.That(await page.GetAttributeAsync("#map-cls", "aria-expanded"), Is.EqualTo("true"));
            Assert.That(await SelectedOptionsAsync(page, "cls"), Is.EqualTo(new[] { "Alla", "FamilyService" }));
            Assert.That(await page.InnerTextAsync("#map-status"), Is.EqualTo("3 av 5 anrop"));
            await page.Keyboard.PressAsync("ArrowUp");
            await page.Keyboard.PressAsync("Enter");
            await ready();
            Assert.That(await SelectedOptionsAsync(page, "cls"), Is.EqualTo(new[] { "Alla", "AuthService", "FamilyService" }));

            // Esc stänger bara listan (inte något annat) och texten är åter det valda.
            await page.EvaluateAsync("() => { window.escapes = 0; document.addEventListener('keydown', e => { if (e.key === 'Escape') window.escapes++; }); }");
            await page.Keyboard.PressAsync("Escape");
            Assert.That(await page.GetAttributeAsync("#map-cls", "aria-expanded"), Is.EqualTo("false"));
            Assert.That(await page.EvaluateAsync<int>("() => window.escapes"), Is.Zero, "Esc stängde listan och inget annat");
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("2 klasser"));

            // Home och End, och Mellanslag växlar när inget är skrivet. Nivåvalet Alla står kvar vald vid sidan av kryssen; Visas inte tar bort dem.
            await page.Keyboard.PressAsync("ArrowDown");
            await page.Keyboard.PressAsync("End");
            Assert.That(await page.GetAttributeAsync("#map-cls", "aria-activedescendant"), Is.EqualTo("map-cls-o7"));
            await page.Keyboard.PressAsync("Home");
            Assert.That(await SelectedOptionsAsync(page, "cls"),
                Is.EqualTo(new[] { "Alla", "AuthService", "FamilyService" }));
            await page.Keyboard.PressAsync("ArrowDown");
            await page.Keyboard.PressAsync("Space");
            await ready();
            Assert.That(await page.IsHiddenAsync("#map-status"), Is.True, "nivåvalet Visas inte tog bort kryssen");
            await page.Keyboard.PressAsync("Escape");
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("Visas inte"));

            // Ingen träff, och Tab stänger och återställer texten.
            await page.Keyboard.PressAsync("ArrowDown");
            await page.Keyboard.TypeAsync("zzz");
            Assert.That(await page.InnerTextAsync("#map-cls-list"), Is.EqualTo("Alla\nVisas inte\nInga träffar"), "nivåvalen står kvar");
            Assert.That(await ListOptionsAsync(page, "cls"), Is.EqualTo(new[] { "Alla", "Visas inte" }), "bara nivåvalen");
            await page.Keyboard.PressAsync("Tab");
            Assert.That(await page.GetAttributeAsync("#map-cls", "aria-expanded"), Is.EqualTo("false"));
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("Visas inte"));

            // Mellanslag är ett tecken i sökningen när något är skrivet, och ett klick utanför stänger.
            await page.ClickAsync("#map-entry");
            Assert.That(await page.InnerTextAsync("#map-entry-list .map-note"), Is.EqualTo("Inget kryss = hela systemet"));
            await page.Keyboard.TypeAsync("POST /v1/auth/i");
            Assert.That(await ListOptionsAsync(page, "entry"), Is.EqualTo(new[] { "POST /v1/auth/in" }));
            await page.Mouse.ClickAsync(700, 600);
            Assert.That(await page.GetAttributeAsync("#map-entry", "aria-expanded"), Is.EqualTo("false"));
            Assert.That(await page.InputValueAsync("#map-entry"), Is.EqualTo("Hela systemet"));

            // Funktion är avstängd när Klass är Visas inte.
            await PickAsync(page, "cls", "Visas inte");
            Assert.That(await page.IsDisabledAsync("#map-fn"), Is.True);
        });
    }
}
