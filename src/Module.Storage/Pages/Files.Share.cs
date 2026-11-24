using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using soft.square.net.ModularBlazor.Abstractions;
using soft.square.net.WebApi.Modules.Storage.Client;

namespace soft.square.net.Module.Storage.Pages;

/// <summary>
/// The Share dialog: people (Storage's own list: members who have used Files), "anyone with the link" links, and for a
/// folder whether it is public. Every change is one API call; the dialog shows the server's answer.
/// </summary>
public partial class Files
{
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private Picked? _shareOf;
    private SharingDto? _sharing;
    private bool _shareBusy;
    private string _peopleSearch = "";
    private IReadOnlyList<PersonDto> _peopleFound = [];
    private ShareAccess _newAccess = ShareAccess.View;
    private DateTime? _linkExpires;
    private string _linkPassword = "";
    private int? _linkLimit;
    private CancellationTokenSource? _searchDelay;

    private async Task ShowShareAsync(Picked item)
    {
        _shareOf = item;
        _sharing = null;
        _peopleSearch = "";
        _peopleFound = [];
        _newAccess = ShareAccess.View;
        _linkExpires = null;
        _linkPassword = "";
        _linkLimit = null;
        StateHasChanged();
        await ShareDoAsync(() => Api.GetSharingAsync(item.Id));
    }

    private void CloseShare()
    {
        _searchDelay?.Cancel();
        _shareOf = null;
    }

    /// <summary>Searches as the user types, a quarter of a second after the last key.</summary>
    private async Task SearchPeopleAsync(ChangeEventArgs e)
    {
        _peopleSearch = e.Value?.ToString() ?? "";
        _searchDelay?.Cancel();
        var delay = _searchDelay = new CancellationTokenSource();
        try { await Task.Delay(250, delay.Token); }
        catch (TaskCanceledException) { return; }
        if (_peopleSearch.Trim().Length < 2)
        {
            _peopleFound = [];
            return;
        }
        try
        {
            var me = _roots?.Home.OwnerId;
            var already = _sharing?.People.Select(p => p.UserId).ToHashSet() ?? [];
            _peopleFound = (await Api.SearchPeopleAsync(_peopleSearch.Trim(), delay.Token))
                .Where(p => p.UserId != me && !already.Contains(p.UserId)).ToList();
        }
        catch (Exception ex) when (ex is StorageApiException or HttpRequestException)
        {
            await MudTail.ToastAsync(Describe(ex), "error");
        }
    }

    private async Task ShareWithAsync(PersonDto person)
    {
        _peopleSearch = "";
        _peopleFound = [];
        await ShareDoAsync(() => Api.ShareWithPeopleAsync(_shareOf!.Id, new ShareWithPeopleRequest { UserIds = [person.UserId], Access = _newAccess }),
            L["Shared with {0}.", person.Name].Value);
    }

    private Task ChangeAccessAsync(SharedPersonDto person, ChangeEventArgs e) =>
        Enum.TryParse<ShareAccess>(e.Value?.ToString(), out var access) && access != person.Access
            ? ShareDoAsync(() => Api.ShareWithPeopleAsync(_shareOf!.Id, new ShareWithPeopleRequest { UserIds = [person.UserId], Access = access }))
            : Task.CompletedTask;

    private Task StopSharingAsync(SharedPersonDto person) =>
        ShareDoAsync(() => Api.StopSharingAsync(_shareOf!.Id, person.UserId), L["{0} no longer has access through this dialog.", person.Name].Value);

    private async Task CreateLinkAsync()
    {
        var created = await ShareCallAsync(() => Api.CreateShareLinkAsync(_shareOf!.Id, new CreateShareLinkRequest
        {
            // The end of the chosen day, in the user's time zone.
            ExpiresAt = _linkExpires is { } day ? new DateTimeOffset(day.Date.AddDays(1).AddTicks(-1), TimeZoneInfo.Local.GetUtcOffset(day)) : null,
            Password = string.IsNullOrEmpty(_linkPassword) ? null : _linkPassword,
            MaxDownloads = _linkLimit,
        }));
        if (created is null) return;
        _linkExpires = null;
        _linkPassword = "";
        _linkLimit = null;
        await CopyAsync(LinkUrl(created));
        await ShareDoAsync(() => Api.GetSharingAsync(_shareOf!.Id));
    }

    private async Task DeleteLinkAsync(ShareLinkDto link)
    {
        if (!await ConfirmAsync(L["Remove this link? Anyone who has it will no longer be able to open it."].Value, L["Remove"].Value)) return;
        if (await ShareCallAsync(async () => { await Api.DeleteShareLinkAsync(link.Id); return true; }))
            await ShareDoAsync(() => Api.GetSharingAsync(_shareOf!.Id));
    }

    private Task SetPublicAsync(ChangeEventArgs e) =>
        ShareDoAsync(() => Api.SetPublicAsync(_shareOf!.Id, new SetPublicRequest { Public = e.Value is true }));

    /// <summary>The page that opens a link (the anonymous Shared links module): /s/{tenant}/{token}.</summary>
    private string LinkUrl(ShareLinkDto link) =>
        $"{Nav.BaseUri}s/{Uri.EscapeDataString(_sharing!.TenantId)}/{link.Token}";

    /// <summary>A file's address in a public folder: stable, for published pages (&lt;img src&gt;, links).</summary>
    private string PublicUrl()
    {
        var gateway = HttpClients.CreateClient(HttpClientNames.WebApi).BaseAddress?.ToString() ?? Nav.BaseUri;
        if (!gateway.EndsWith('/')) gateway += "/";
        return $"{gateway}api/storage/public/{Uri.EscapeDataString(_sharing!.TenantId)}/{_sharing.ItemId}/{Uri.EscapeDataString(_sharing.Name)}";
    }

    private async Task CopyAsync(string text)
    {
        try
        {
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            await MudTail.ToastAsync(L["Link copied."].Value, "success");
        }
        catch (JSException)
        {
            await MudTail.ToastAsync(L["Copy the link from the box: the browser did not allow copying."].Value, "info");
        }
    }

    private string LinkSummary(ShareLinkDto link)
    {
        var parts = new List<string>();
        if (!link.Active) parts.Add(L["no longer works"].Value);
        parts.Add(link.ExpiresAt is { } end ? L["until {0}", end.ToLocalTime().ToString("d")].Value : L["no expiry"].Value);
        if (link.HasPassword) parts.Add(L["password"].Value);
        parts.Add(link.MaxDownloads is { } max ? L["{0} of {1} downloads", link.Downloads, max].Value : L["{0} downloads", link.Downloads].Value);
        return string.Join(" · ", parts);
    }

    private async Task ShareDoAsync(Func<Task<SharingDto>> call, string? done = null)
    {
        var sharing = await ShareCallAsync(call);
        if (sharing is null) return;
        _sharing = sharing;
        StateHasChanged(); // called from the explorer's events too, where Blazor does not render by itself
        if (done is not null) await MudTail.ToastAsync(done, "success");
    }

    private async Task<T?> ShareCallAsync<T>(Func<Task<T>> call)
    {
        _shareBusy = true;
        StateHasChanged();
        try { return await call(); }
        catch (Exception ex) when (ex is StorageApiException or HttpRequestException)
        {
            await MudTail.ToastAsync(Describe(ex), "error");
            return default;
        }
        finally
        {
            _shareBusy = false;
            StateHasChanged();
        }
    }
}
