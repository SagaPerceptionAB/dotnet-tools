namespace SagaPerception.CommandLine.Tests;

public class ExitCodeTests
{
    [Test]
    public void The_codes_are_fixed()
    {
        Assert.That(new[] { ExitCodes.Ok, ExitCodes.Failed, ExitCodes.Usage, ExitCodes.Unreachable, ExitCodes.Inconclusive, ExitCodes.Internal, ExitCodes.Interrupted, ExitCodes.Terminated },
            Is.EqualTo(new[] { 0, 1, 2, 3, 4, 70, 130, 143 }));
    }

    [Test]
    public void An_unexpected_exception_is_an_internal_error()
        => Assert.That(FriendlyError.From(new InvalidCastException("x"), "tool").ExitCode, Is.EqualTo(ExitCodes.Internal));

    [Test]
    public void A_cancellation_is_an_interruption()
        => Assert.That(FriendlyError.From(new OperationCanceledException(), "tool").ExitCode, Is.EqualTo(ExitCodes.Interrupted));

    [Test]
    public void A_usage_error_is_a_usage_error()
        => Assert.That(FriendlyError.From(new UsageException("fel"), "tool").ExitCode, Is.EqualTo(ExitCodes.Usage));
}

public class TerminalTests
{
    private static (Terminal Terminal, StringWriter Out, StringWriter Err) Create(bool interactive, bool noColor = false)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (new Terminal(o, e, interactive, noColor), o, e);
    }

    [Test]
    public void Confirm_writes_a_relative_path_to_stderr_only()
    {
        var (t, o, e) = Create(false);
        t.Confirm(Path.Combine(Directory.GetCurrentDirectory(), "ut", "rapport.html"), "HTML-rapport");
        Assert.That(e.ToString(), Is.EqualTo("Skrev ut/rapport.html (HTML-rapport)" + Environment.NewLine));
        Assert.That(o.ToString(), Is.Empty);
    }

    [Test]
    public void A_path_outside_the_current_directory_is_written_in_full()
    {
        var outside = Path.Combine(Path.GetTempPath(), "x", "rapport.html");
        var shown = Terminal.Relative(outside);
        Assert.That(shown, Does.Not.StartWith(".."));
        Assert.That(shown, Does.EndWith("x/rapport.html"));
        Assert.That(Path.IsPathRooted(shown));
    }

    [Test]
    public void Warning_is_prefixed_and_goes_to_stderr()
    {
        var (t, o, e) = Create(false);
        t.Warning("något är konstigt");
        Assert.That(e.ToString(), Is.EqualTo("Varning: något är konstigt" + Environment.NewLine));
        Assert.That(o.ToString(), Is.Empty);
    }

    [Test]
    public void Progress_is_shown_only_in_a_terminal()
    {
        var (quiet, _, quietErr) = Create(false);
        quiet.Progress(3, 12, "T-003");
        Assert.That(quietErr.ToString(), Is.Empty);

        var (live, liveOut, liveErr) = Create(true);
        live.Progress(3, 12, "T-003");
        Assert.That(liveErr.ToString(), Is.EqualTo("[ 3/12] T-003" + Environment.NewLine));
        Assert.That(liveOut.ToString(), Is.Empty);
    }

    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    public void Color_needs_a_terminal_and_no_NO_COLOR(bool interactive, bool noColor, bool expected)
        => Assert.That(Create(interactive, noColor).Terminal.Color, Is.EqualTo(expected));

    [Test]
    public void Default_reads_the_console_streams_at_the_time_of_the_call()
    {
        var original = Console.Error;
        var swapped = new StringWriter();
        try
        {
            Console.SetError(swapped);
            Terminal.Default.Warning("hej");
        }
        finally { Console.SetError(original); }
        Assert.That(swapped.ToString(), Does.StartWith("Varning: hej"));
    }
}

public class HelpPageTests
{
    private static readonly HelpPage Full = new()
    {
        Usage = ["tool run --spec <fil>"],
        Description = "Gör något nyttigt.",
        Commands = [new("run", "Kör"), new("template", "Skapa en mall")],
        Flags = [new("--spec <fil>", "Specen"), new("--api", "Adressen")],
        Environment = [new("API_TOKEN", "Inloggning")],
        Examples = ["tool run --spec shop.yaml"],
        ExitCodes = [new("0", "Klart"), new("2", "Fel i användning")],
    };

    [Test]
    public void Sections_come_in_the_fixed_order()
    {
        var text = Full.ToString();
        var positions = HelpPage.Headings.Select(h => text.IndexOf(h + ":", StringComparison.Ordinal)).ToList();
        Assert.That(positions, Has.All.GreaterThanOrEqualTo(0));
        Assert.That(positions, Is.Ordered);
        Assert.That(HelpPage.Headings, Is.EqualTo(new[] { "Användning", "Beskrivning", "Kommandon", "Flaggor", "Miljövariabler", "Exempel", "Exitkoder" }));
    }

    [Test]
    public void Empty_sections_are_left_out()
    {
        var text = new HelpPage { Usage = ["tool"], ExitCodes = [new("0", "Klart")] }.ToString();
        Assert.That(text, Does.Contain("Användning:").And.Contain("Exitkoder:"));
        Assert.That(text, Does.Not.Contain("Beskrivning:").And.Not.Contain("Flaggor:").And.Not.Contain("Exempel:"));
    }

    [Test]
    public void Names_are_aligned_in_even_columns()
    {
        var lines = Full.ToString().Split('\n').Where(l => l.StartsWith("  --")).ToList();
        Assert.That(lines, Has.Count.EqualTo(2));
        Assert.That(lines[0].IndexOf("Specen", StringComparison.Ordinal), Is.EqualTo(lines[1].IndexOf("Adressen", StringComparison.Ordinal)));
    }

    [Test]
    public void The_page_ends_with_one_newline()
        => Assert.That(Full.ToString(), Does.EndWith("fel i användning".Length > 0 ? "Fel i användning\n" : ""));
}

public class SecretsTests
{
    [TestCase("X-Api-Key")]
    [TestCase("Authorization")]
    [TestCase("x-auth-token")]
    [TestCase("Session-Id")]
    public void Secret_looking_names_are_recognised(string name) => Assert.That(Secrets.LooksSecret(name), Is.True);

    [TestCase("Accept")]
    [TestCase("Content-Type")]
    public void Ordinary_names_are_not_secret(string name) => Assert.That(Secrets.LooksSecret(name), Is.False);

    [Test]
    public void The_token_value_is_masked()
        => Assert.That(Secrets.RedactCommandLine(["run", "--token", "hemligt123", "--spec", "a.yaml"]), Is.EqualTo("run --token *** --spec a.yaml"));

    [Test]
    public void The_inline_form_is_masked()
        => Assert.That(Secrets.RedactCommandLine(["--token=hemligt123"]), Is.EqualTo("--token=***"));

    [TestCase("--api-key")]
    [TestCase("--client-secret")]
    [TestCase("--password")]
    [TestCase("--my-TOKEN")]
    public void Flags_with_secret_words_are_masked(string flag)
    {
        var shown = Secrets.RedactCommandLine([flag, "hemligt123"]);
        Assert.That(shown, Does.Not.Contain("hemligt123").And.Contain("***"));
    }

    [Test]
    public void A_secret_header_value_is_masked_but_an_ordinary_one_is_not()
    {
        var shown = Secrets.RedactCommandLine(["--header", "X-Api-Key: hemligt123", "--header", "Accept: text/plain"]);
        Assert.That(shown, Does.Not.Contain("hemligt123"));
        Assert.That(shown, Does.Contain("\"X-Api-Key: ***\"").And.Contain("\"Accept: text/plain\""));
    }

    [Test]
    public void Other_arguments_are_kept_and_quoted_when_needed()
        => Assert.That(Secrets.RedactCommandLine(["run", "--only", "a b"]), Is.EqualTo("run --only \"a b\""));
}

public class CommandRunTests
{
    private static (int Code, string Out, string Err) Run(string[] args, Func<int> body)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Command.Run(args, body, ex => FriendlyError.From(ex, "tool"), new Terminal(o, e, false));
        return (code, o.ToString(), e.ToString());
    }

    [Test]
    public void A_normal_run_returns_its_exit_code_and_prints_nothing_itself()
    {
        var (code, o, e) = Run([], () => ExitCodes.Inconclusive);
        Assert.That((code, o, e), Is.EqualTo((ExitCodes.Inconclusive, "", "")));
    }

    [Test]
    public void An_unexpected_exception_gives_70_with_diagnostics_and_masked_command_line_on_stderr()
    {
        var (code, o, e) = Run(["run", "--token", "hemligt123", "--header", "X-Api-Key: hemligt456"], () => throw new InvalidOperationException("boom"));
        Assert.That(code, Is.EqualTo(70));
        Assert.That(o, Is.Empty);
        Assert.That(e, Does.Contain("Kunde inte köra:").And.Contain("Gör så här:").And.Contain("Tekniskt:"));
        Assert.That(e, Does.Contain("Version:").And.Contain("System:").And.Contain("Runtime:").And.Contain("Kommandorad:"));
        Assert.That(e, Does.Contain("--token ***").And.Contain("X-Api-Key: ***"));
        Assert.That(e, Does.Not.Contain("hemligt123").And.Not.Contain("hemligt456"));
    }

    [Test]
    public void A_cancellation_gives_130()
    {
        var (code, o, e) = Run(["run"], () => throw new OperationCanceledException());
        Assert.That(code, Is.EqualTo(130));
        Assert.That(o, Is.Empty);
        Assert.That(e, Does.Contain("avbröts"));
        Assert.That(e, Does.Not.Contain("Kommandorad:"));
    }

    [Test]
    public void A_usage_error_gives_2_without_diagnostics()
    {
        var (code, _, e) = Run(["run"], () => throw new UsageException("Fel flagga."));
        Assert.That(code, Is.EqualTo(2));
        Assert.That(e, Does.Contain("Kunde inte köra: Fel flagga.").And.Not.Contain("Kommandorad:"));
    }
}

public class SolutionFinderTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("solutionfinder-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    [Test]
    public void Several_solutions_in_the_folder_give_an_error()
    {
        File.WriteAllText(Path.Combine(_dir, "Butik.sln"), "");
        File.WriteAllText(Path.Combine(_dir, "Butik.slnx"), "");
        var e = Assert.Throws<UsageException>(() => SolutionFinder.Find(_dir, "dotnet systemmap"))!;
        Assert.That(e.Message, Does.Contain("Butik.sln, Butik.slnx"));
        Assert.That(e.Tip, Does.Contain("dotnet systemmap --solution Butik.sln"));
    }

    [Test]
    public void Solution_is_found_in_a_parent_folder()
    {
        File.WriteAllText(Path.Combine(_dir, "Butik.slnx"), "");
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var src = Directory.CreateDirectory(Path.Combine(_dir, "src", "Api")).FullName;
        File.WriteAllText(Path.Combine(src, "Api.csproj"), "");
        Assert.That(SolutionFinder.Find(src, "dotnet systemmap"), Is.EqualTo(Path.Combine("..", "..", "Butik.slnx")), "solutionen visar hela systemet");
        File.Delete(Path.Combine(_dir, "Butik.slnx"));
        Assert.That(SolutionFinder.Find(src, "dotnet systemmap"), Is.EqualTo("Api.csproj"), "utan solution: närmaste projekt");

        // Repots rot är gränsen: ovanför den letas inte.
        var empty = Directory.CreateDirectory(Path.Combine(_dir, "empty")).FullName;
        Directory.CreateDirectory(Path.Combine(empty, ".git"));
        var e = Assert.Throws<UsageException>(() => SolutionFinder.Find(empty, "dotnet systemmap"))!;
        Assert.That(e.Message, Does.StartWith("Hittade ingen solution"));
    }

    private void Touch(params string[] parts)
    {
        var file = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
    }

    [Test]
    public void A_solution_in_a_subfolder_is_never_picked_but_named_in_the_hint()
    {
        Touch("backend", "Butik.Backend.slnx");
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        Assert.That(SolutionFinder.TryFind(_dir, "systemmap"), Is.Null, "nedåt väljs ingenting automatiskt");
        var e = Assert.Throws<UsageException>(() => SolutionFinder.Find(_dir, "systemmap"))!;
        Assert.That(e.Message, Does.StartWith("Hittade ingen solution"));
        Assert.That(e.Tip, Is.EqualTo("Gör så här: systemmap --solution backend/Butik.Backend.slnx"));
    }

    [Test]
    public void The_search_below_is_shallow_first_bounded_and_skips_build_output_and_hidden_folders()
    {
        Touch("a", "Djup.sln");
        Touch("a", "b", "c", "d", "Alltfördjup.sln");
        Touch("bin", "Bin.sln");
        Touch("obj", "Obj.sln");
        Touch("node_modules", "Nod.sln");
        Touch(".git", "Dold.sln");
        Touch(".vs", "Dold.slnx");
        Touch("z", "Sist.slnx");
        Touch("a", "Anmärkning.txt");
        Assert.That(SolutionFinder.FindBelow(_dir), Is.EqualTo(new[] { Path.Combine("a", "Djup.sln"), Path.Combine("z", "Sist.slnx") }));
        Assert.That(SolutionFinder.FindBelow(_dir, 1), Is.EqualTo(new[] { Path.Combine("a", "Djup.sln"), Path.Combine("z", "Sist.slnx") }));
        Assert.That(SolutionFinder.FindBelow(_dir, 4), Does.Contain(Path.Combine("a", "b", "c", "d", "Alltfördjup.sln")));
        Assert.That(SolutionFinder.FindBelow(_dir, 0), Is.Empty);
    }

    [Test]
    public void The_hint_names_the_first_solution_below_and_mentions_the_others_and_is_null_when_there_are_none()
    {
        Assert.That(SolutionFinder.HintBelow(_dir, "systemmap"), Is.Null);
        Touch("backend", "A.slnx");
        Touch("tools", "B.sln");
        Assert.That(SolutionFinder.HintBelow(_dir, "systemmap"), Is.EqualTo("Gör så här: systemmap --solution backend/A.slnx (andra: " + Path.Combine("tools", "B.sln") + ")"));
        Directory.Delete(Path.Combine(_dir, "backend"), true);
        Directory.Delete(Path.Combine(_dir, "tools"), true);
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var e = Assert.Throws<UsageException>(() => SolutionFinder.Find(_dir, "systemmap"))!;
        Assert.That(e.Tip, Does.Contain("src/Butik.sln"), "utan någon solution nedåt gäller det vanliga tipset");
    }
}
