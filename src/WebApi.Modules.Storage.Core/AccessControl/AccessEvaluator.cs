using soft.square.net.WebApi.Modules.Storage.Core.ItemAggregate;

namespace soft.square.net.WebApi.Modules.Storage.Core.AccessControl;

/// <summary>An entry that applies to an item: its own (<see cref="Distance"/> 0) or inherited from the folder <see cref="Source"/>.</summary>
public sealed record EffectiveEntry(AccessEntry Entry, Item Source, int Distance)
{
    public bool Inherited => Distance > 0;
}

/// <summary>
/// Computes access the NTFS way, on read, along the item's chain of folders (inherited entries are never copied down,
/// so moving a folder or changing a parent takes effect at once):
/// <list type="number">
/// <item>The entries in canonical order: the item's own denies, its own allows, then each ancestor's inheritable entries
/// (denies before allows), nearest folder first, up to the first item that does not inherit.</item>
/// <item>For each right the first entry naming it decides: a deny takes the rights not yet allowed, an allow the
/// rights not yet denied. So an explicit allow beats an inherited deny, as in Windows.</item>
/// <item>The owner may always read and change the permissions.</item>
/// </list>
/// </summary>
public static class AccessEvaluator
{
    /// <param name="chain">The item's folders from the top down, the item last.</param>
    public static StorageRights Evaluate(IReadOnlyList<Item> chain, IPrincipalMatcher who)
    {
        if (chain.Count == 0) return StorageRights.None;
        var target = chain[^1];
        StorageRights granted = StorageRights.None, denied = StorageRights.None;
        foreach (var effective in Entries(chain))
        {
            if (!Matches(effective, target, who)) continue;
            var rights = effective.Entry.Rights;
            if (effective.Entry.Type == AceType.Deny) denied |= rights & ~granted;
            else granted |= rights & ~denied;
        }
        if (string.Equals(target.OwnerId, who.UserId, StringComparison.Ordinal))
            granted |= StorageRights.ReadPermissions | StorageRights.ChangePermissions;
        return granted;
    }

    /// <summary>The entries that apply to the last item of <paramref name="chain"/>, in canonical (evaluation) order.</summary>
    public static IEnumerable<EffectiveEntry> Entries(IReadOnlyList<Item> chain)
    {
        if (chain.Count == 0) yield break;
        var target = chain[^1];
        foreach (var entry in Ordered(target.Entries.Where(e => e.AppliesToSelf)))
            yield return new EffectiveEntry(entry, target, 0);

        for (var distance = 1; distance < chain.Count; distance++)
        {
            // The item one level below this ancestor must inherit, or nothing above it applies.
            if (!chain[^distance].InheritsAccess) yield break;
            var source = chain[^(distance + 1)];
            foreach (var entry in Ordered(source.Entries.Where(e => e.InheritedBy(target.IsFolder, distance))))
                yield return new EffectiveEntry(entry, source, distance);
        }
    }

    /// <summary>
    /// What "disable inheritance, convert inherited permissions into explicit permissions" copies onto the last item of
    /// <paramref name="chain"/>: every entry it inherits, with the flags it has there. On a folder, entries keep passing
    /// to its contents where they did (a files-only entry becomes inherit-only, so it still reaches the files below).
    /// </summary>
    public static IReadOnlyList<AccessEntry> InheritedAsExplicit(IReadOnlyList<Item> chain) =>
        Inherited(chain).Select(e => e.Entry).ToArray();

    /// <summary>
    /// Every entry the last item of <paramref name="chain"/> inherits, as it stands on that item (with the flags it has
    /// there, see <see cref="InheritedAsExplicit"/>) and the folder it comes from: the Advanced Security dialog's
    /// inherited rows. Unlike <see cref="Entries"/> it includes inherit-only rows that pass through a folder to its files.
    /// </summary>
    public static IReadOnlyList<EffectiveEntry> Inherited(IReadOnlyList<Item> chain)
    {
        var result = new List<EffectiveEntry>();
        if (chain.Count == 0) return result;
        var target = chain[^1];
        for (var distance = 1; distance < chain.Count; distance++)
        {
            if (!chain[^distance].InheritsAccess) break;
            var source = chain[^(distance + 1)];
            foreach (var entry in Ordered(source.Entries))
            {
                if (AsOnTarget(entry, target.IsFolder, distance) is { } flags)
                    result.Add(new EffectiveEntry(entry.WithInheritance(flags), source, distance));
            }
        }
        return result;
    }

    /// <summary>The flags an entry has on an item <paramref name="distance"/> levels below its folder, or null when it does not reach it.</summary>
    private static AceInheritance? AsOnTarget(AccessEntry entry, bool targetIsFolder, int distance)
    {
        var flags = entry.Inheritance;
        if (distance > 1 && flags.HasFlag(AceInheritance.NoPropagate)) return null;
        if (!targetIsFolder) return flags.HasFlag(AceInheritance.Files) ? AceInheritance.None : null;
        if (flags.HasFlag(AceInheritance.NoPropagate)) return flags.HasFlag(AceInheritance.Subfolders) ? AceInheritance.None : null;
        var copied = flags & (AceInheritance.Subfolders | AceInheritance.Files);
        if (copied == AceInheritance.None) return null;
        return copied.HasFlag(AceInheritance.Subfolders) ? copied : copied | AceInheritance.InheritOnly;
    }

    private static bool Matches(EffectiveEntry effective, Item target, IPrincipalMatcher who) =>
        effective.Entry.PrincipalKind == PrincipalKind.CreatorOwner
            ? effective.Inherited && string.Equals(target.OwnerId, who.UserId, StringComparison.Ordinal)
            : who.Matches(effective.Entry);

    private static IEnumerable<AccessEntry> Ordered(IEnumerable<AccessEntry> entries)
    {
        var list = entries.ToArray();
        return list.Where(e => e.Type == AceType.Deny).Concat(list.Where(e => e.Type == AceType.Allow));
    }
}
