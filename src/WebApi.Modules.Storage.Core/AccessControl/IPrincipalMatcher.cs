namespace soft.square.net.WebApi.Modules.Storage.Core.AccessControl;

/// <summary>
/// The principal whose access is evaluated (the caller, or another user for "effective access"): which entries are
/// theirs. Permission principals are resolved before evaluation, so matching is synchronous.
/// </summary>
public interface IPrincipalMatcher
{
    /// <summary>The user's <c>sub</c>; owner checks and CREATOR OWNER entries use it.</summary>
    string UserId { get; }

    /// <summary>The entry names this principal (User, Everyone, Permission, Group). CREATOR OWNER is decided by the evaluator.</summary>
    bool Matches(AccessEntry entry);
}
