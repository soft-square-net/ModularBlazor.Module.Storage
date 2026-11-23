namespace soft.square.net.WebApi.Modules.Storage.UseCases.Access;

/// <summary>
/// One user as a principal: their id, their Storage groups, and the platform permissions they hold right now. Permission
/// principals are looked up once per (permission, scope) through the platform's grants (<see cref="PrepareAsync"/>), so
/// shift grants count only while the shift runs.
/// </summary>
public sealed class PrincipalMatcher(string userId, IReadOnlySet<string> groupIds, Func<string, string, CancellationToken, Task<bool>> holds)
    : IPrincipalMatcher
{
    private readonly Dictionary<(string Permission, string Scope), bool> _permissions = [];

    public string UserId { get; } = userId;

    /// <summary>Resolves the permission principals named by these items' entries.</summary>
    public async Task PrepareAsync(IEnumerable<Item> items, CancellationToken ct)
    {
        foreach (var entry in items.SelectMany(i => i.Entries).Where(e => e.PrincipalKind == PrincipalKind.Permission))
        {
            var key = (entry.PrincipalId!, entry.PrincipalScope!.ToLowerInvariant());
            if (!_permissions.ContainsKey(key)) _permissions[key] = await holds(key.Item1, key.Item2, ct);
        }
    }

    public bool Matches(AccessEntry entry) => entry.PrincipalKind switch
    {
        PrincipalKind.User => string.Equals(entry.PrincipalId, UserId, StringComparison.Ordinal),
        PrincipalKind.Everyone => true,
        PrincipalKind.Group => entry.PrincipalId is not null && groupIds.Contains(entry.PrincipalId),
        PrincipalKind.Permission => _permissions.TryGetValue((entry.PrincipalId!, entry.PrincipalScope!.ToLowerInvariant()), out var holds) && holds,
        _ => false
    };
}
