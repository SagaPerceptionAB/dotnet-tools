namespace UiTestGen.Generation;

/// <summary>
/// Paketversionerna i det genererade testprojektet. Det projektet är fristående och ärver inte repots Directory.Packages.props,
/// så versionerna skrivs ut. NUnit, testadaptern och testsdk:n ska vara desamma som i repots Directory.Packages.props
/// (ett test jämför dem).
/// </summary>
internal static class PackageVersions
{
    public const string PlaywrightNUnit = "1.64.1";
    public const string NUnit = "4.3.2";
    public const string NUnit3TestAdapter = "5.0.0";
    public const string TestSdk = "17.14.0";
}
