using soft.square.net.WebApi.Modules.Storage.Core.GroupAggregate;

namespace soft.square.net.WebApi.Modules.Storage.UseCases.Access;

/// <summary>
/// The caller of the current request as Storage sees them (scoped): their id, whether they administer Storage, and
/// their rights on an item. Also builds the principal of another user, for the Security dialog's "effective access".
/// </summary>
public sealed class StorageCaller(IAccessScopes access, IReadRepository<StorageGroup> groups, TimeProvider clock)
{
    private bool? _isAdmin;
    private PrincipalMatcher? _matcher;

    public DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>The caller's <c>sub</c>. Every endpoint but signed download links requires a signed-in caller.</summary>
    public string UserId => access.UserId ?? throw new StorageAccessDeniedException("Sign in first.");

    public async Task<bool> IsAdminAsync(CancellationToken ct) =>
        _isAdmin ??= await access.HasPermissionAsync(StoragePermissions.Admin, ct);

    /// <summary>The caller's rights on the last item of <paramref name="chain"/> (full control for Storage admins).</summary>
    public async Task<StorageRights> RightsAsync(IReadOnlyList<Item> chain, CancellationToken ct)
    {
        if (await IsAdminAsync(ct)) return StorageRights.FullControl;
        var matcher = await MatcherAsync(ct);
        await matcher.PrepareAsync(chain, ct);
        return AccessEvaluator.Evaluate(chain, matcher);
    }

    /// <summary>The caller as a principal.</summary>
    public async Task<PrincipalMatcher> MatcherAsync(CancellationToken ct) =>
        _matcher ??= new PrincipalMatcher(UserId, await GroupIdsAsync(UserId, ct),
            (permission, scope, token) => access.HasPermissionAsync(permission, [scope], token));

    /// <summary>Another member as a principal (effective access): their groups and their grants right now.</summary>
    public async Task<PrincipalMatcher> MatcherForAsync(string userId, CancellationToken ct) =>
        new(userId, await GroupIdsAsync(userId, ct),
            (permission, scope, token) => access.UserHasPermissionAsync(userId, permission, [scope], token));

    private async Task<IReadOnlySet<string>> GroupIdsAsync(string userId, CancellationToken ct) =>
        (await groups.ListAsync(new GroupsOfUserSpec(userId), ct)).Select(g => g.Id.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Thrown when there is no signed-in caller; the endpoints answer 403.</summary>
public sealed class StorageAccessDeniedException(string message) : Exception(message);
