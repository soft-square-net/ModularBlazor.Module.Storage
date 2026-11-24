using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using soft.square.net.ModularBlazor.Abstractions;
using soft.square.net.ModularBlazor.Abstractions.MudTail;
using soft.square.net.WebApi.Modules.Storage.Client;

namespace soft.square.net.Module.Storage.Pages;

/// <summary>
/// The Security dialog, as in Windows: <b>Permissions</b> (each user or group with the preset rights, Allow / Deny),
/// <b>Advanced</b> (the owner, inheritance, every entry with where it comes from and what it applies to) and
/// <b>Effective access</b> (what one person may do, through all their entries, groups and grants). Changes to the
/// access list are kept here until Apply (PUT /items/{id}/acl with the version read); changing the owner is at once.
/// Opening it needs Read permissions; changing it needs Change permissions (the server checks both).
/// </summary>
public partial class SecurityDialog : ComponentBase
{
    [Inject] private IHttpClientFactory HttpClients { get; set; } = default!;
    [Inject] private MudTailRuntime MudTail { get; set; } = default!;
    [Inject] private IStringLocalizerFactory Texts { get; set; } = default!;

    [Parameter, EditorRequired] public Guid ItemId { get; set; }
    [Parameter, EditorRequired] public string Name { get; set; } = "";
    [Parameter] public bool IsFolder { get; set; }
    /// <summary>The caller's rights on the item (StorageRights), which decide what can be changed here.</summary>
    [Parameter] public int Rights { get; set; }
    /// <summary>The caller's user id (the owner line says "you").</summary>
    [Parameter] public string? MyUserId { get; set; }
    /// <summary>A folder's name as the page shows it (its own places: "My files", "Files", "Users").</summary>
    [Parameter] public Func<Guid, string, string>? FolderName { get; set; }
    /// <summary>Turns an API failure into the message the page shows.</summary>
    [Parameter, EditorRequired] public Func<Exception, string> Describe { get; set; } = default!;
    [Parameter] public EventCallback OnClose { get; set; }
    /// <summary>Raised after the access list or the owner changed (the explorer refreshes: the caller's rights may differ).</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private IStringLocalizer? _l;
    private IStringLocalizer L => _l ??= Texts.Create(typeof(Files));
    private StorageClient Api => new(HttpClients.CreateClient(HttpClientNames.WebApi));

    private enum Tab { Permissions, Advanced, Effective }
    private Tab _tab = Tab.Permissions;
    private AclDto? _acl;
    private bool _busy;
    private string? _error;

    // The working copy: the item's own entries and whether it inherits. Inherited rows come from _acl.
    private List<Entry> _own = [];
    private bool _inherits = true;
    private bool _dirty;
    private bool _askInheritance; // "Disable inheritance": convert or remove the inherited entries

    private bool CanChange => Has(Rights, R.ChangePermissions);
    private bool CanTakeOwnership => Has(Rights, R.TakeOwnership);
    private const int DefaultFolderInheritance = Subfolders | Files;

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            Take(await Api.GetAclAsync(ItemId));
        });
    }

    private void Take(AclDto acl)
    {
        _acl = acl;
        _inherits = acl.InheritsAccess;
        _own = acl.Entries.Where(e => !e.Inherited).Select(Entry.From).ToList();
        _dirty = false;
        _askInheritance = false;
        _selected ??= Principals().FirstOrDefault()?.Key;
        if (_selected is not null && Principals().All(p => p.Key != _selected)) _selected = Principals().FirstOrDefault()?.Key;
    }

    /// <summary>The inherited rows that still apply (none once inheritance is turned off here).</summary>
    private IEnumerable<AceDto> Inherited => _inherits && _acl is not null ? _acl.Entries.Where(e => e.Inherited) : [];

    // ───────── principals ─────────

    /// <summary>Who an entry is for, and how the dialog names them.</summary>
    private sealed record Principal(PrincipalKind Kind, string? Id, string? Scope, string? Name)
    {
        public string Key => $"{Kind}|{Id}|{Scope}";
    }

    private IReadOnlyList<Principal> Principals() =>
        _own.Select(e => e.Who).Concat(Inherited.Select(e => new Principal(e.PrincipalKind, e.PrincipalId, e.PrincipalScope, e.PrincipalName)))
            .Concat(_added)
            .GroupBy(p => p.Key).Select(g => g.First())
            .OrderBy(p => p.Kind switch { PrincipalKind.Everyone => 0, PrincipalKind.CreatorOwner => 1, PrincipalKind.Group => 2, PrincipalKind.Permission => 3, _ => 4 })
            .ThenBy(p => NameOf(p), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private string NameOf(Principal p) => p.Kind switch
    {
        PrincipalKind.Everyone => L["Everyone"].Value,
        PrincipalKind.CreatorOwner => L["CREATOR OWNER"].Value,
        PrincipalKind.Permission => p.Scope is null or "tenant"
            ? L["Holders of {0}", p.Id ?? ""].Value
            : L["Holders of {0} in {1}", p.Id ?? "", p.Scope].Value,
        PrincipalKind.Group => L["{0} (group)", p.Name ?? p.Id ?? ""].Value,
        _ => p.Id == MyUserId ? L["{0} (you)", p.Name ?? p.Id ?? ""].Value : p.Name ?? p.Id ?? "",
    };

    private string SourceOf(AceDto e) =>
        e.InheritedFromId is { } id && FolderName is not null ? FolderName(id, e.InheritedFromName ?? "") : e.InheritedFromName ?? "";

    private string NameOf(AceDto e) => NameOf(new Principal(e.PrincipalKind, e.PrincipalId, e.PrincipalScope, e.PrincipalName));

    // ───────── Permissions tab (presets per principal) ─────────

    private string? _selected;
    // Principals added in this session that have no entry yet (Windows shows them until Apply).
    private readonly List<Principal> _added = [];

    private Principal? Selected => Principals().FirstOrDefault(p => p.Key == _selected);

    /// <summary>The presets of the Windows Security tab, with the rights each one stands for.</summary>
    private IEnumerable<(string Label, int Mask)> Presets()
    {
        yield return (L["Full control"], (int)R.FullControl);
        yield return (L["Modify"], (int)R.Modify);
        yield return (L["Read & execute"], (int)R.ReadAndExecute);
        yield return (L["Read"], (int)R.Read);
        yield return (L["Write"], (int)R.Write);
        yield return (L["Share"], (int)R.Share);
    }

    /// <summary>The rights a principal has of one type on this item: from its own (applying) entries and inherited ones.</summary>
    private (int Own, int Inherited) MaskOf(Principal p, AceType type)
    {
        var own = _own.Where(e => e.Who.Key == p.Key && e.Type == type && (e.Inheritance & InheritOnly) == 0).Aggregate(0, (m, e) => m | e.Rights);
        var inherited = Inherited.Where(e => SameAs(e, p) && e.Type == type && (e.Inheritance & InheritOnly) == 0).Aggregate(0, (m, e) => m | e.Rights);
        return (own, inherited);
    }

    private static bool SameAs(AceDto e, Principal p) =>
        e.PrincipalKind == p.Kind && e.PrincipalId == p.Id && e.PrincipalScope == p.Scope;

    /// <summary>The entry the Permissions tab edits for a principal and type: the one with the default inheritance.</summary>
    private Entry? Simple(Principal p, AceType type) =>
        _own.FirstOrDefault(e => e.Who.Key == p.Key && e.Type == type && e.Inheritance == (IsFolder ? DefaultFolderInheritance : 0));

    private void TogglePreset(Principal p, AceType type, int mask, bool on)
    {
        var entry = Simple(p, type);
        if (on)
        {
            if (entry is null) _own.Add(new Entry(p, type, mask, IsFolder ? DefaultFolderInheritance : 0));
            else entry.Rights |= mask;
        }
        else
        {
            // Unchecking takes those rights from every own entry of this principal and type that applies here.
            foreach (var e in _own.Where(e => e.Who.Key == p.Key && e.Type == type && (e.Inheritance & InheritOnly) == 0)) e.Rights &= ~mask;
            _own.RemoveAll(e => e.Rights == 0);
        }
        _dirty = true;
    }

    /// <summary>A principal's entries that the Permissions tab cannot show as presets (other inheritance, odd rights).</summary>
    private bool HasSpecial(Principal p) =>
        _own.Any(e => e.Who.Key == p.Key && (e.Inheritance != (IsFolder ? DefaultFolderInheritance : 0) || !IsPresetCombination(e.Rights)));

    private bool IsPresetCombination(int rights)
    {
        var covered = Presets().Where(x => (rights & x.Mask) == x.Mask).Aggregate(0, (m, x) => m | x.Mask);
        return covered == rights;
    }

    private void RemovePrincipal(Principal p)
    {
        _own.RemoveAll(e => e.Who.Key == p.Key);
        _added.RemoveAll(a => a.Key == p.Key);
        _selected = Principals().FirstOrDefault()?.Key;
        _dirty = true;
    }

    private bool IsOnlyInherited(Principal p) => _own.All(e => e.Who.Key != p.Key) && _added.All(a => a.Key != p.Key);

    // ───────── adding a principal (users, groups, everyone, permission holders, CREATOR OWNER) ─────────

    private bool _adding;
    private PrincipalKind _addKind = PrincipalKind.User;
    private string _addSearch = "";
    private IReadOnlyList<PersonDto> _peopleFound = [];
    private IReadOnlyList<GroupDto>? _groups;
    private string _addPermission = "";
    private string _addScope = "tenant";
    private CancellationTokenSource? _searchDelay;
    /// <summary>Where a principal that is picked goes: the Permissions list, or a new Advanced entry.</summary>
    private Action<Principal>? _onPicked;

    private void StartAdding(Action<Principal> onPicked)
    {
        _adding = true;
        _onPicked = onPicked;
        _addKind = PrincipalKind.User;
        _addSearch = "";
        _peopleFound = [];
        _addPermission = "";
        _addScope = "tenant";
    }

    private void AddToPermissions() => StartAdding(p =>
    {
        if (Principals().All(x => x.Key != p.Key))
        {
            // As in Windows, a new name starts with Read & execute allowed.
            _own.Add(new Entry(p, AceType.Allow, (int)R.ReadAndExecute, IsFolder ? DefaultFolderInheritance : 0));
            _dirty = true;
        }
        _selected = p.Key;
    });

    private async Task AddKindChangedAsync(ChangeEventArgs e)
    {
        _addKind = Enum.TryParse<PrincipalKind>(e.Value?.ToString(), out var kind) ? kind : PrincipalKind.User;
        if (_addKind == PrincipalKind.Group && _groups is null)
            await RunAsync(async () => _groups = await Api.ListGroupsAsync());
    }

    /// <summary>Searches Storage's people a quarter of a second after the last key (at least 2 characters).</summary>
    private async Task SearchPeopleAsync(ChangeEventArgs e, Action<IReadOnlyList<PersonDto>> found)
    {
        var text = e.Value?.ToString() ?? "";
        _searchDelay?.Cancel();
        var delay = _searchDelay = new CancellationTokenSource();
        try { await Task.Delay(250, delay.Token); }
        catch (TaskCanceledException) { return; }
        if (text.Trim().Length < 2)
        {
            found([]);
            return;
        }
        try { found(await Api.SearchPeopleAsync(text.Trim(), delay.Token)); }
        catch (Exception ex) when (ex is StorageApiException or HttpRequestException) { _error = Describe(ex); }
    }

    private Task AddSearchAsync(ChangeEventArgs e)
    {
        _addSearch = e.Value?.ToString() ?? "";
        return SearchPeopleAsync(e, list => _peopleFound = list);
    }

    private void Pick(Principal p)
    {
        _adding = false;
        _onPicked?.Invoke(p);
        _onPicked = null;
    }

    private void PickPermission()
    {
        var permission = _addPermission.Trim();
        if (permission.Length == 0) return;
        var scope = string.IsNullOrWhiteSpace(_addScope) ? "tenant" : _addScope.Trim();
        Pick(new Principal(PrincipalKind.Permission, permission, scope, null));
    }

    // ───────── Advanced tab: entries ─────────

    /// <summary>The entry being edited in the Advanced tab (a copy; null: none). <see cref="_editing"/> is the original.</summary>
    private Entry? _draft;
    private Entry? _editing;
    private bool _showAdvancedRights;

    private void NewEntry() => StartAdding(p =>
    {
        _editing = null;
        _draft = new Entry(p, AceType.Allow, (int)R.ReadAndExecute, IsFolder ? DefaultFolderInheritance : 0);
        _showAdvancedRights = false;
    });

    private void EditEntry(Entry entry)
    {
        _editing = entry;
        _draft = entry.Copy();
        _showAdvancedRights = !IsPresetCombination(entry.Rights);
    }

    private void SaveDraft()
    {
        if (_draft is null || _draft.Rights == 0) return;
        if (_editing is not null) _own[_own.IndexOf(_editing)] = _draft;
        else _own.Add(_draft);
        _draft = null;
        _editing = null;
        _dirty = true;
    }

    private void RemoveEntry(Entry entry)
    {
        _own.Remove(entry);
        _dirty = true;
    }

    /// <summary>The "Applies to" choices of a folder's entry: (label, inheritance flags without NoPropagate).</summary>
    private IEnumerable<(string Label, int Flags)> AppliesTo()
    {
        yield return (L["This folder only"], 0);
        yield return (L["This folder, subfolders and files"], Subfolders | Files);
        yield return (L["This folder and subfolders"], Subfolders);
        yield return (L["This folder and files"], Files);
        yield return (L["Subfolders and files only"], Subfolders | Files | InheritOnly);
        yield return (L["Subfolders only"], Subfolders | InheritOnly);
        yield return (L["Files only"], Files | InheritOnly);
    }

    private string AppliesToText(int inheritance)
    {
        if (!IsFolder && (inheritance & (Subfolders | Files)) == 0) return L["This file"];
        var flags = inheritance & ~NoPropagate;
        var label = AppliesTo().FirstOrDefault(a => a.Flags == flags).Label ?? L["This folder only"];
        return (inheritance & NoPropagate) != 0 ? L["{0}, one level", label].Value : label;
    }

    private void SetAppliesTo(ChangeEventArgs e)
    {
        if (_draft is null || !int.TryParse(e.Value?.ToString(), out var flags)) return;
        _draft.Inheritance = flags == 0 ? 0 : flags | (_draft.Inheritance & NoPropagate);
    }

    /// <summary>The individual rights (NTFS names; a folder's and a file's meaning of each).</summary>
    private IEnumerable<(string Label, int Mask)> Individual()
    {
        yield return (IsFolder ? L["Traverse folder"] : L["Execute file"], (int)R.Traverse);
        yield return (IsFolder ? L["List folder"] : L["Read data"], (int)R.ListFolder);
        yield return (L["Read attributes"], (int)R.ReadAttributes);
        yield return (L["Read extended attributes"], (int)R.ReadExtendedAttributes);
        yield return (IsFolder ? L["Create files"] : L["Write data"], (int)R.CreateFiles);
        yield return (IsFolder ? L["Create folders"] : L["Append data"], (int)R.CreateFolders);
        yield return (L["Write attributes"], (int)R.WriteAttributes);
        yield return (L["Write extended attributes"], (int)R.WriteExtendedAttributes);
        if (IsFolder) yield return (L["Delete subfolders and files"], (int)R.DeleteChildren);
        yield return (L["Delete"], (int)R.Delete);
        yield return (L["Read permissions"], (int)R.ReadPermissions);
        yield return (L["Change permissions"], (int)R.ChangePermissions);
        yield return (L["Take ownership"], (int)R.TakeOwnership);
        yield return (L["Share"], (int)R.Share);
    }

    /// <summary>What an entry allows or denies, in words: the largest preset it holds, else "Special".</summary>
    private string AccessText(int rights)
    {
        foreach (var (label, mask) in Presets().Where(p => p.Mask != (int)R.Share))
            if (rights == mask || rights == (mask | (int)R.Share)) return rights == mask ? label : $"{label} + {L["Share"]}";
        return rights == (int)R.Share ? L["Share"] : L["Special"];
    }

    // ───────── inheritance ─────────

    private void InheritanceClicked()
    {
        if (_inherits) _askInheritance = true; // Windows asks: convert, or remove
        else
        {
            _inherits = true;
            _dirty = true;
        }
    }

    private void StopInheriting(bool convert)
    {
        if (convert && _acl is not null)
            foreach (var e in _acl.Entries.Where(e => e.Inherited))
                _own.Add(Entry.From(e));
        _inherits = false;
        _askInheritance = false;
        _dirty = true;
    }

    // ───────── owner ─────────

    private bool _changingOwner;
    private string _ownerSearch = "";
    private IReadOnlyList<PersonDto> _ownersFound = [];

    private Task OwnerSearchAsync(ChangeEventArgs e)
    {
        _ownerSearch = e.Value?.ToString() ?? "";
        return SearchPeopleAsync(e, list => _ownersFound = list);
    }

    /// <summary>Take ownership (null) or give it to someone (Storage admins); the item's version changes, so it reloads.</summary>
    private async Task SetOwnerAsync(string? userId)
    {
        var changed = await RunAsync(async () =>
        {
            Take(await Api.SetOwnerAsync(ItemId, new SetOwnerRequest { OwnerId = userId }));
            _changingOwner = false;
        });
        if (changed) await OnChanged.InvokeAsync();
    }

    // ───────── Effective access tab ─────────

    private string _effectiveSearch = "";
    private IReadOnlyList<PersonDto> _effectiveFound = [];
    private PersonDto? _effectiveOf;
    private EffectiveAccessDto? _effective;

    private Task EffectiveSearchAsync(ChangeEventArgs e)
    {
        _effectiveSearch = e.Value?.ToString() ?? "";
        return SearchPeopleAsync(e, list => _effectiveFound = list);
    }

    private async Task ShowEffectiveAsync(PersonDto person)
    {
        _effectiveOf = person;
        _effectiveFound = [];
        _effectiveSearch = "";
        _effective = null;
        await RunAsync(async () => _effective = await Api.GetEffectiveAccessAsync(ItemId, person.UserId));
    }

    // ───────── apply / close ─────────

    private async Task<bool> ApplyAsync()
    {
        if (_acl is null || !_dirty) return true;
        var applied = await RunAsync(async () =>
        {
            Take(await Api.SetAclAsync(ItemId, new SetAclRequest
            {
                InheritsAccess = _inherits,
                Entries = _own.Select(e => e.ToDto()).ToList(),
                ConvertInherited = false, // converted here already, so the dialog shows what will be saved
                Version = _acl.Version,
            }));
            _added.Clear();
        });
        if (applied) await OnChanged.InvokeAsync();
        return applied;
    }

    private async Task OkAsync()
    {
        if (await ApplyAsync()) await OnClose.InvokeAsync();
    }

    private Task CancelAsync() => OnClose.InvokeAsync();

    private async Task<bool> RunAsync(Func<Task> call)
    {
        _busy = true;
        _error = null;
        StateHasChanged();
        try
        {
            await call();
            return true;
        }
        catch (Exception ex) when (ex is StorageApiException or HttpRequestException)
        {
            _error = Describe(ex);
            return false;
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }

    // ───────── the working entries ─────────

    private sealed class Entry(Principal who, AceType type, int rights, int inheritance)
    {
        public Principal Who { get; } = who;
        public AceType Type { get; set; } = type;
        public int Rights { get; set; } = rights;
        public int Inheritance { get; set; } = inheritance;

        public static Entry From(AceDto e) =>
            new(new Principal(e.PrincipalKind, e.PrincipalId, e.PrincipalScope, e.PrincipalName), e.Type, e.Rights, e.Inheritance);

        public Entry Copy() => new(Who, Type, Rights, Inheritance);

        public AceDto ToDto() => new()
        {
            PrincipalKind = Who.Kind, PrincipalId = Who.Id, PrincipalScope = Who.Scope, Type = Type, Rights = Rights, Inheritance = Inheritance,
        };
    }

    // AceInheritance (NTFS flags).
    private const int Subfolders = 1, Files = 2, NoPropagate = 4, InheritOnly = 8;

    private static bool Has(int rights, R right) => (rights & (int)right) == (int)right;

    /// <summary>StorageRights of the server (NTFS values plus Share).</summary>
    [Flags]
    private enum R
    {
        ListFolder = 0x1,
        CreateFiles = 0x2,
        CreateFolders = 0x4,
        ReadExtendedAttributes = 0x8,
        WriteExtendedAttributes = 0x10,
        Traverse = 0x20,
        DeleteChildren = 0x40,
        ReadAttributes = 0x80,
        WriteAttributes = 0x100,
        Delete = 0x10000,
        ReadPermissions = 0x20000,
        ChangePermissions = 0x40000,
        TakeOwnership = 0x80000,
        Share = 0x1000000,
        Read = ListFolder | ReadAttributes | ReadExtendedAttributes | ReadPermissions,
        Write = CreateFiles | CreateFolders | WriteAttributes | WriteExtendedAttributes,
        ReadAndExecute = Read | Traverse,
        Modify = ReadAndExecute | Write | Delete,
        FullControl = Modify | DeleteChildren | ChangePermissions | TakeOwnership | Share,
    }
}
