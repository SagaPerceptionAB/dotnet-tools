namespace SystemMap;

/// <summary>Ett anrop i API-beskrivningen (OpenAPI). Kartan kopplar anropen i koden till dem och listar det som bara finns på ena stället.</summary>
/// <param name="OperationId">operationId i specen, eller null om specen inte har något.</param>
/// <param name="Method">HTTP-metoden med stora bokstäver, t.ex. <c>POST</c>.</param>
/// <param name="Path">Sökvägen som i specen, t.ex. <c>/orders/{id}</c>.</param>
public sealed record ApiOperation(string? OperationId, string Method, string Path);
