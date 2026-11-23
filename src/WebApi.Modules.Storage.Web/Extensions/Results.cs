using Ardalis.Result;
using Microsoft.EntityFrameworkCore;
using soft.square.net.WebApi.Modules.Storage.UseCases.Access;
using soft.square.net.WebApi.SharedKernel;

namespace soft.square.net.WebApi.Modules.Storage.Web.Extensions;

/// <summary>
/// Turns a use case's Ardalis result into the HTTP response: the value on success, otherwise problem details with the
/// result's messages. The typed results keep the response type in the OpenAPI document.
/// </summary>
public static class ResultExtensions
{
    public static Results<Ok<T>, ProblemHttpResult> ToOkResult<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();

    public static Results<Created<T>, ProblemHttpResult> ToCreatedResult<T>(this Result<T> result, Func<T, string> location) =>
        result.IsSuccess ? TypedResults.Created(location(result.Value), result.Value) : result.ToProblem();

    public static Results<NoContent, ProblemHttpResult> ToNoContentResult(this Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();

    public static ProblemHttpResult ToProblem(this Ardalis.Result.IResult result) =>
        TypedResults.Problem(detail: Detail(result), statusCode: result.Status switch
        {
            ResultStatus.NotFound => StatusCodes.Status404NotFound,
            ResultStatus.Forbidden => StatusCodes.Status403Forbidden,
            ResultStatus.Unauthorized => StatusCodes.Status401Unauthorized,
            ResultStatus.Conflict => StatusCodes.Status409Conflict,
            ResultStatus.Invalid => StatusCodes.Status400BadRequest,
            ResultStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        });

    private static string? Detail(Ardalis.Result.IResult result)
    {
        var messages = result.Errors.Concat(result.ValidationErrors.Select(e => e.ErrorMessage)).ToArray();
        return messages.Length == 0 ? null : string.Join("; ", messages);
    }
}

/// <summary>
/// The exceptions that still reach the endpoints, as problem details: a broken domain rule (an invalid name, a system
/// folder) and a lost race (a concurrency token, a name taken at the same moment) are 409; no signed-in caller is 403.
/// On the module's route group, so it is module code and unloads with the module.
/// </summary>
public sealed class StorageProblemsFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (StorageAccessDeniedException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (DbUpdateConcurrencyException) { return Results.Problem("Someone else changed this item first; refresh and try again.", statusCode: StatusCodes.Status409Conflict); }
        catch (DbUpdateException) { return Results.Problem("The change conflicts with another one (an item with the same name was just added?); refresh and try again.", statusCode: StatusCodes.Status409Conflict); }
        catch (DomainException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
    }
}
