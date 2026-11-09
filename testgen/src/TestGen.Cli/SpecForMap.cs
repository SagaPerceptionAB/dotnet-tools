using SystemMap;
using TestGen.Core;

namespace TestGen.Cli;

/// <summary>Specens operationer i testgen som anropen systemkartan kopplar koden till (testgen map och kartan i testgen web).</summary>
internal static class SpecForMap
{
    public static IReadOnlyList<ApiOperation> ForMap(this IEnumerable<OperationInfo> operations) =>
        operations.Select(o => new ApiOperation(o.OperationId, o.Method, o.Path)).ToList();
}
