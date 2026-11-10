using System.Globalization;
using System.Text;
using System.Xml;

namespace TestGen.Core.Run;

/// <summary>
/// JUnit-XML (--report resultat.xml eller resultat.junit.xml) för CI-system som visar testresultat (GitHub-rapportörer, Azure DevOps
/// PublishTestResults, Jenkins, GitLab). En <c>testsuite</c> per grupp (Genererade tester, Excel-tester), <c>classname</c> = operationId,
/// <c>name</c> = "T-020 quantity för lågt (under minimum)", <c>time</c> i sekunder med punkt.
/// <list type="bullet">
/// <item><c>failure</c> (type system eller expectation): FEL med orsak Fel i systemet / Förväntan i testet stämmer inte.</item>
/// <item><c>error</c> (type auth, test-data eller environment): FEL som inte säger något säkert om systemet.</item>
/// <item><c>skipped</c> ("Följd av T-001"): HOPPAD och följdfel. Fel i testgen (orsak verktyg) är också skipped, med meddelandet
/// "Fel i testgen: ...", eftersom det inte säger något om systemet.</item>
/// </list>
/// Alla texter går genom <see cref="HeaderMask.Scrub"/>. Typkoderna är samma som cause i JSON-resultatet.
/// </summary>
public static class JUnitResult
{
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n";

    private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Tar bort tecken som inte är tillåtna i XML 1.0 (styrtecken, lösa surrogat).</summary>
    public static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); continue; }
            if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static XmlWriter Create(StringBuilder sb) =>
        XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.Replace });

    public static string Text(ReportInfo info, IReadOnlyList<ReportEntry> entries, HeaderMask mask)
    {
        string S(string? s) => Clean(mask.Scrub(s ?? ""));
        var outcomes = ReportWriter.Outcomes(entries, mask);
        var exit = info.ExitCode ?? ExitPolicy.Decide(outcomes);
        var verdictKind = VerdictKinds.Of(outcomes);
        var verdictText = PlainLanguage.Verdict(outcomes, info.PossibleTotal, info.Partial);
        var infos = entries.Select(e => e.Info ?? TestInfo.Basic(e.Id, e.Group, e.Call, e.Test)).ToList();
        var cases = entries.Select((e, i) => (Entry: e, Info: infos[i], Plain: e.Plain(mask))).ToList();

        double Secs(ReportEntry e) => e.Result.Exchanges.Sum(x => x.ElapsedMs) / 1000.0;
        static string Kind(FailureCause? c) => c is { } x ? CauseCodes.Code(x) : "";
        bool IsFailure(FailureCause? c) => c is FailureCause.System or FailureCause.Expectation;
        bool IsError(FailureCause? c) => c is FailureCause.Auth or FailureCause.TestData or FailureCause.Environment;

        var sb = new StringBuilder();
        using (var w = Create(sb))
        {
            w.WriteStartDocument();
            w.WriteStartElement("testsuites");
            w.WriteAttributeString("name", "testgen");
            w.WriteAttributeString("tests", cases.Count.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("failures", cases.Count(c => c.Entry.Outcome != "OK" && IsFailure(c.Plain.Cause)).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("errors", cases.Count(c => c.Entry.Outcome != "OK" && IsError(c.Plain.Cause)).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("time", Num((info.Duration ?? TimeSpan.Zero).TotalSeconds));

            void Properties()
            {
                w.WriteStartElement("properties");
                void P(string name, string? value) { if (value is null) return; w.WriteStartElement("property"); w.WriteAttributeString("name", name); w.WriteAttributeString("value", S(value)); w.WriteEndElement(); }
                P("toolVersion", info.Version);
                P("api", info.Api);
                P("environment", info.Env);
                P("seed", info.Seed.ToString(CultureInfo.InvariantCulture));
                P("spec", info.Spec);
                P("excel", info.Excel);
                P("startedAt", JsonResult.Iso(info.When));
                P("verdict", verdictKind);
                P("verdictText", verdictText);
                P("exitCode", exit.ToString(CultureInfo.InvariantCulture));
                P("partial", info.Partial ? "true" : "false");
                P("notice", info.Notice);
                if (info.Comparison is { } cmp)
                {
                    P("baseline", cmp.File);
                    P("newFailures", string.Join(" ", cmp.New.Select(i => i.Id)));
                    P("fixed", string.Join(" ", cmp.Fixed.Select(i => i.Id)));
                    P("persisting", string.Join(" ", cmp.Persisting.Select(i => i.Id)));
                }
                w.WriteEndElement();
            }

            void Suite(string name, IReadOnlyList<(ReportEntry Entry, TestInfo Info, PlainText Plain)> items)
            {
                w.WriteStartElement("testsuite");
                w.WriteAttributeString("name", S(name));
                w.WriteAttributeString("tests", items.Count.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("failures", items.Count(c => c.Entry.Outcome != "OK" && IsFailure(c.Plain.Cause)).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("errors", items.Count(c => c.Entry.Outcome != "OK" && IsError(c.Plain.Cause)).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("skipped", items.Count(c => c.Entry.Outcome != "OK" && !IsFailure(c.Plain.Cause) && !IsError(c.Plain.Cause)).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("time", Num(items.Sum(c => Secs(c.Entry))));
                w.WriteAttributeString("timestamp", info.When.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
                Properties();
                foreach (var (e, t, plain) in items) Case(e, t, plain);
                w.WriteEndElement();
            }

            void Case(ReportEntry e, TestInfo t, PlainText plain)
            {
                w.WriteStartElement("testcase");
                w.WriteAttributeString("classname", S(t.OperationId.Length > 0 ? t.OperationId : e.Group));
                w.WriteAttributeString("name", S($"{e.Id} {t.Description}"));
                w.WriteAttributeString("time", Num(Secs(e)));
                if (e.Outcome != "OK")
                {
                    var cause = plain.Cause;
                    var causeName = cause is { } cn ? PlainLanguage.CauseName(cn) : "";
                    if (IsFailure(cause) || IsError(cause))
                    {
                        w.WriteStartElement(IsFailure(cause) ? "failure" : "error");
                        w.WriteAttributeString("type", Kind(cause));
                        w.WriteAttributeString("message", S($"{Message(t, e.Result)} ({causeName})"));
                        var body = new List<string> { plain.Title, plain.Explanation };
                        if (plain.Hint is not null) body.Add("Tolkning: " + plain.Hint);
                        if (e.Result.RootCauseTestId is { } root) body.Add("Följd av " + root);
                        w.WriteString(S(string.Join("\n", body)));
                        w.WriteEndElement();
                    }
                    else
                    {
                        w.WriteStartElement("skipped");
                        if (cause is FailureCause.Tool or FailureCause.Unsupported)
                        {
                            w.WriteAttributeString("message", S($"{causeName}: {plain.Explanation}"));
                            w.WriteString(S(string.Join("\n", new[] { plain.Title, PlainLanguage.CauseExplanation(cause!.Value) })));
                        }
                        else
                            w.WriteAttributeString("message", S(e.Result.RootCauseTestId is { } r ? $"Följd av {r}" : "Följd av ett tidigare fel"));
                        w.WriteEndElement();
                    }
                    var curl = ExchangeText.Curl(e.Result.Exchanges);
                    var rerun = ReportWriter.RerunCommand(info, e.Id);
                    if (curl is not null || rerun is not null)
                    {
                        w.WriteStartElement("system-out");
                        var parts = new List<string>();
                        if (curl is not null) parts.Add("curl för anropet:\n" + curl);
                        if (rerun is not null) parts.Add("kör om: " + rerun);
                        w.WriteString(S(string.Join("\n", parts)));
                        w.WriteEndElement();
                    }
                }
                w.WriteEndElement();
            }

            if (cases.Count == 0)
                Suite("testgen", Array.Empty<(ReportEntry, TestInfo, PlainText)>());
            else
                foreach (var g in cases.GroupBy(c => c.Entry.Group))
                    Suite(g.Key, g.ToList());
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        return Declaration + sb;
    }

    /// <summary>Körningen kunde inte genomföras: ett enda test som är ett fel av typen environment, så att CI-systemet visar det i testlistan.</summary>
    public static string TextFailure(ReportInfo info, string what, string action, HeaderMask mask)
    {
        string S(string? s) => Clean(mask.Scrub(s ?? ""));
        var time = Num((info.Duration ?? TimeSpan.Zero).TotalSeconds);
        var sb = new StringBuilder();
        using (var w = Create(sb))
        {
            w.WriteStartDocument();
            w.WriteStartElement("testsuites");
            w.WriteAttributeString("name", "testgen");
            w.WriteAttributeString("tests", "1"); w.WriteAttributeString("failures", "0"); w.WriteAttributeString("errors", "1");
            w.WriteAttributeString("time", time);
            w.WriteStartElement("testsuite");
            w.WriteAttributeString("name", "testgen");
            w.WriteAttributeString("tests", "1"); w.WriteAttributeString("failures", "0"); w.WriteAttributeString("errors", "1"); w.WriteAttributeString("skipped", "0");
            w.WriteAttributeString("time", time);
            w.WriteAttributeString("timestamp", info.When.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
            w.WriteStartElement("properties");
            foreach (var (n, v) in new[] { ("toolVersion", info.Version ?? ""), ("api", info.Api), ("seed", info.Seed.ToString(CultureInfo.InvariantCulture)), ("verdict", "unreachable"), ("exitCode", ExitCodes.Unreachable.ToString(CultureInfo.InvariantCulture)) })
            {
                w.WriteStartElement("property"); w.WriteAttributeString("name", n); w.WriteAttributeString("value", S(v)); w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteStartElement("testcase");
            w.WriteAttributeString("classname", "testgen");
            w.WriteAttributeString("name", S($"Körning mot {info.Api}"));
            w.WriteAttributeString("time", time);
            w.WriteStartElement("error");
            w.WriteAttributeString("type", "environment");
            w.WriteAttributeString("message", S("Körningen kunde inte genomföras: " + what));
            w.WriteString(S(what + "\nGör så här: " + action));
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        return Declaration + sb;
    }

    /// <summary>Kort orsak till felet: "väntade 400, fick 500", "inget svar (tidsgräns)" eller testets egen dom ("svaret stämmer inte").</summary>
    private static string Message(TestInfo t, CaseResult r)
    {
        var expected = string.Join("/", t.ExpectedStatus.Select(x => x.ToString()));
        if (r.TimedOut) return "inget svar inom tidsgränsen";
        if (r.Setup is { } s) return $"förberedande anrop {s.Operation} gav {s.Status}";
        if (r.Status is { } status && (t.ExpectedStatus.Count == 0 || !t.ExpectedStatus.Any(x => x.Matches(status))))
            return expected.Length > 0 ? $"väntade {expected}, fick {status}" : $"fick {status}";
        var v = r.Verdict;
        var open = v.IndexOf('(');
        return open >= 0 && v.EndsWith(')') ? v[(open + 1)..^1] : r.Details.FirstOrDefault() ?? (r.Status is { } st ? $"fick {st}" : "inget svar");
    }
}
