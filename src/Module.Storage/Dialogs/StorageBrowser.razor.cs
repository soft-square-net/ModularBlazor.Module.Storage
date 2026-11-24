using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.Localization;
using soft.square.net.ModularBlazor.Abstractions;
using soft.square.net.WebApi.Modules.Storage.Client;

namespace soft.square.net.Module.Storage.Dialogs;

public partial class StorageBrowser : ComponentBase
{
    public enum Place { Home, Files, Shared }

    [Inject] private IHttpClientFactory HttpClients { get; set; } = default!;
    [Inject] private IStringLocalizerFactory Texts { get; set; } = default!;

    /// <summary>The folder to open first (else My files).</summary>
    [Parameter] public Guid? Start { get; set; }
    [Parameter] public bool Multiple { get; set; }
    /// <summary>Whether files are listed (pick-folder shows folders only).</summary>
    [Parameter] public bool FilesShown { get; set; } = true;
    /// <summary>Why a file cannot be chosen (shown as its tooltip), or null when it can.</summary>
    [Parameter] public Func<ItemDto, FolderView?, string?>? Refuse { get; set; }
    [Parameter] public IReadOnlySet<Guid> Selected { get; set; } = new HashSet<Guid>();
    /// <summary>A file that can be chosen was clicked.</summary>
    [Parameter] public EventCallback<ItemDto> OnFile { get; set; }
    /// <summary>Another folder (or the Shared with me list, as null) is open.</summary>
    [Parameter] public EventCallback<FolderView?> OnFolder { get; set; }

    private IStringLocalizer? _l;
    private IStringLocalizer L => _l ??= Texts.Create(typeof(StorageBrowser));

    public Roots? Roots { get; private set; }
    /// <summary>The open folder; null while Shared with me (a list, not a folder) is shown.</summary>
    public FolderView? View { get; private set; }

    private Place _place = Place.Home;
    private IReadOnlyList<ItemDto> _shared = [];
    private string _search = "";
    private bool _loading = true;
    private string? _error;
    private int _loadNumber;

    private StorageClient Api => new(HttpClients.CreateClient(HttpClientNames.WebApi));

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        if (!await RunAsync(async () => Roots = await Api.GetRootsAsync())) return;
        if (Start is { } start && await OpenAsync(start)) return;
        await GoToAsync(Place.Home);
    }

    public async Task GoToAsync(Place place)
    {
        if (Roots is null) return;
        _place = place;
        _search = "";
        if (place == Place.Shared)
        {
            var number = ++_loadNumber;
            IReadOnlyList<SharedItemDto>? shared = null;
            if (!await RunAsync(async () => shared = await Api.GetSharedWithMeAsync()) || number != _loadNumber) return;
            _shared = [.. shared!.Select(s => s.Item)];
            View = null;
            await OnFolder.InvokeAsync(null);
            StateHasChanged();
            return;
        }
        await OpenAsync(place == Place.Files ? Roots.Files.Id : Roots.Home.Id);
    }

    public async Task<bool> OpenAsync(Guid folderId)
    {
        var number = ++_loadNumber;
        FolderView? view = null;
        if (!await RunAsync(async () => view = await Api.ListFolderAsync(folderId)) || number != _loadNumber) return false;
        View = view;
        _search = "";
        // The place follows the top of the path, except inside Shared with me (folders shared from elsewhere).
        if (_place != Place.Shared || view!.Path.Count == 0)
            _place = view!.Path.FirstOrDefault()?.Role == FolderRole.Root ? Place.Files : Place.Home;
        await OnFolder.InvokeAsync(view);
        StateHasChanged();
        return true;
    }

    private IEnumerable<ItemDto> Visible()
    {
        var items = View?.Items ?? (_place == Place.Shared ? _shared : []);
        var search = _search.Trim();
        return search.Length == 0 ? items : items.Where(i => NameOf(i).Contains(search, StringComparison.CurrentCultureIgnoreCase));
    }

    private Task FileAsync(ItemDto file, string? refused) => refused is null ? OnFile.InvokeAsync(file) : Task.CompletedTask;

    public string CrumbName(Crumb crumb) => DisplayName(crumb.Id, crumb.Role, crumb.Name);

    public string NameOf(ItemDto item) => DisplayName(item.Id, item.Role, item.Name);

    private string DisplayName(Guid id, FolderRole role, string name) => role switch
    {
        FolderRole.Root => L["Files"].Value,
        FolderRole.Homes => L["Users"].Value,
        FolderRole.Home when id == Roots?.Home.Id => L["My files"].Value,
        _ => name,
    };

    private string SizeText(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} {L["bytes"]}" : $"{value:0.#} {units[unit]}";
    }

    private async Task<bool> RunAsync(Func<Task> call)
    {
        _loading = true;
        _error = null;
        try
        {
            await call();
            return true;
        }
        catch (AccessTokenNotAvailableException ex)
        {
            ex.Redirect();
            return false;
        }
        catch (Exception ex) when (ex is StorageApiException or HttpRequestException)
        {
            _error = Describe(ex);
            return false;
        }
        finally
        {
            _loading = false;
            StateHasChanged(); // called from OnAfterRenderAsync too, where Blazor does not render by itself
        }
    }

    private string Describe(Exception ex) => ex switch
    {
        StorageApiException { StatusCode: 403 } api => Detail(api) ?? L["You do not have access to this folder."],
        StorageApiException { StatusCode: 404 } api => Detail(api) ?? L["The folder does not exist, or it is in the recycle bin."],
        StorageApiException api => $"{api.StatusCode}: {Detail(api) ?? api.Message}",
        HttpRequestException http => L["The gateway is not reachable ({0}).", http.Message],
        _ => ex.Message,
    };

    private static string? Detail(StorageApiException api)
    {
        if (string.IsNullOrWhiteSpace(api.Response) || !api.Response.TrimStart().StartsWith('{')) return null;
        try
        {
            using var json = JsonDocument.Parse(api.Response);
            foreach (var name in new[] { "detail", "title" })
                if (json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                    return text;
        }
        catch (JsonException) { }
        return null;
    }
}
