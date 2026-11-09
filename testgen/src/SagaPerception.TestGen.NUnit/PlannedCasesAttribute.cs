using System.Reflection;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Builders;

namespace SagaPerception.TestGen.NUnit;

/// <summary>Fixturen vet vilka testfall den har, utan att något körs. Implementeras av <see cref="ApiTests"/> och <see cref="ViewTests"/>.</summary>
internal interface IPlannedFixture
{
    IEnumerable<TestCaseData> PlannedCases(Assembly testAssembly);
}

/// <summary>
/// Bygger testfallen när NUnit upptäcker testerna. TestCaseSource duger inte här: NUnit 4 kräver att källan är statisk, men projektnamnet
/// står i den ärvande klassen. Fixturen skapas därför en gång för att läsa det.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class PlannedCasesAttribute : NUnitAttribute, ITestBuilder
{
    public IEnumerable<TestMethod> BuildFrom(IMethodInfo method, Test? suite)
    {
        var type = suite?.TypeInfo?.Type ?? method.TypeInfo.Type;
        var fixture = (IPlannedFixture)Activator.CreateInstance(type)!;
        var builder = new NUnitTestCaseBuilder();
        foreach (var data in fixture.PlannedCases(Assembly.GetAssembly(type)!))
            yield return builder.BuildTestMethod(method, suite, data);
    }
}
