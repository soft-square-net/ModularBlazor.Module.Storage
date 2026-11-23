namespace soft.square.net.WebApi.Modules.Storage.UseCases;

public static class ResultExtensions
{
    /// <summary>A failed result as a <see cref="Result{T}"/> of another type: same status, same messages.</summary>
    public static Result<T> As<T>(this Ardalis.Result.IResult failure) => failure.Status switch
    {
        ResultStatus.Forbidden => Result<T>.Forbidden([.. failure.Errors]),
        ResultStatus.NotFound => Result<T>.NotFound([.. failure.Errors]),
        ResultStatus.Conflict => Result<T>.Conflict([.. failure.Errors]),
        ResultStatus.Unauthorized => Result<T>.Unauthorized([.. failure.Errors]),
        ResultStatus.Invalid => Result<T>.Invalid([.. failure.ValidationErrors]),
        ResultStatus.Ok => throw new InvalidOperationException("Only a failed result converts."),
        _ => Result<T>.Error(new ErrorList([.. failure.Errors]))
    };
}

/// <summary>Messages shared by the use cases (English; the client module translates what it shows).</summary>
public static class StorageMessages
{
    public const string Denied = "Access is denied.";
    public const string NotFound = "The item does not exist, or it is in the recycle bin.";
    public static string NameTaken(string name) => $"There is already an item named '{name}' in this folder.";
}
