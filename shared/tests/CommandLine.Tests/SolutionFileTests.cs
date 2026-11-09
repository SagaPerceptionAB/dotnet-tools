namespace SagaPerception.CommandLine.Tests;

public class SolutionFileTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("solutionfile-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void Project(string name) => Write($"{name}/{name}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

    [Test]
    public void Slnx_lists_projects_also_inside_folders_and_skips_missing_files()
    {
        Project("A"); Project("B");
        var sln = Write("x.slnx", "<Solution><Folder Name=\"/src/\"><Project Path=\"B/B.csproj\" /></Folder><Project Path=\"A\\A.csproj\" /><Project Path=\"Saknas/Saknas.csproj\" /></Solution>");
        Assert.That(SolutionFile.ProjectPaths(sln).Select(Path.GetFileName), Is.EqualTo(new[] { "A.csproj", "B.csproj" }));
    }

    [Test]
    public void Sln_lists_csproj_projects_and_ignores_solution_folders()
    {
        Project("A");
        var sln = Write("x.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "A", "A\A.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
        Assert.That(SolutionFile.ProjectPaths(sln).Select(Path.GetFileName), Is.EqualTo(new[] { "A.csproj" }));
    }

    [Test]
    public void Broken_xml_gives_no_projects_not_an_error()
    {
        var sln = Write("x.slnx", "<Solution><Project");
        Assert.That(SolutionFile.ProjectPaths(sln), Is.Empty);
    }
}
