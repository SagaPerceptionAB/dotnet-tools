namespace TestGen.Mock;

/// <summary>Ett anrop till mocken. Hemliga värden i frågan och kroppen är maskade.</summary>
/// <param name="TimeMs">Millisekunder sedan mocken startade.</param>
/// <param name="Query">Frågan utan inledande frågetecken, annars tom.</param>
/// <param name="OperationId">Operationen anropet matchade, annars null.</param>
/// <param name="Problems">Skillnader mot specen (<c>name: saknas (...)</c>); tom om anropet stämde.</param>
public sealed record MockCall(
    long TimeMs,
    string Method,
    string Path,
    string Query,
    string? Body,
    string? OperationId,
    int Status,
    IReadOnlyList<string> Problems);
