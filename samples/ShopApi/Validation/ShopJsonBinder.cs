using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using ShopApplication;

namespace ShopApi.Validation;

/// <summary>
/// Läser bodyn som JSON, kontrollerar den mot <see cref="RequestRules"/> och gör sedan om den till request-typen.
/// Ett fel blir ett ModelState-fel som <see cref="ErrorResponses.FromModelState"/> svarar på med 400 och <c>{"error": "..."}</c>.
/// Content-Type spelar ingen roll: bodyn läses alltid som JSON.
/// </summary>
public sealed class ShopJsonBinder : IModelBinder
{
    public async Task BindModelAsync(ModelBindingContext context)
    {
        JsonNode? node;
        try { node = await JsonNode.ParseAsync(context.HttpContext.Request.Body, cancellationToken: context.HttpContext.RequestAborted); }
        catch (Exception) { Fail(context, "ogiltig JSON"); return; }
        if (node is not JsonObject body) { Fail(context, "body måste vara ett objekt"); return; }
        if (RequestRules.Check(context.ModelType, body) is { } problem) { Fail(context, problem); return; }

        context.Result = ModelBindingResult.Success(body.Deserialize(context.ModelType, JsonSerializerOptions.Web));
    }

    private static void Fail(ModelBindingContext context, string message)
    {
        context.ModelState.TryAddModelError(context.ModelName, message);
        context.Result = ModelBindingResult.Failed();
    }
}

/// <summary>Felsvaren har alltid formen <c>{"error": "..."}</c>.</summary>
public static class ErrorResponses
{
    public static ObjectResult Error(int status, string message) => new(new ErrorBody(message)) { StatusCode = status };

    public static IActionResult FromModelState(ActionContext context) =>
        Error(StatusCodes.Status400BadRequest,
            context.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault(m => m.Length > 0) ?? "ogiltig indata");
}

public sealed record ErrorBody(string Error);

/// <summary>Fel från tjänsterna: något saknas blir 404, en krock blir 409.</summary>
public sealed class ShopErrorFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        context.Result = context.Exception switch
        {
            NotFoundException e => ErrorResponses.Error(StatusCodes.Status404NotFound, e.Message),
            ConflictException e => ErrorResponses.Error(StatusCodes.Status409Conflict, e.Message),
            _ => null,
        };
        context.ExceptionHandled = context.Result is not null;
    }
}
