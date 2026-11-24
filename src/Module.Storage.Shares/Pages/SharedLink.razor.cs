using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using soft.square.net.WebApi.Modules.Storage.Client;

namespace soft.square.net.Module.Storage.Shares.Pages;

/// <summary>
/// Opens a share link through the anonymous Storage endpoints (/api/storage/shares/{tenant}/{token}): no token is
/// sent, the link is the authorization. The password stays in this page's memory and goes in the X-Share-Password
/// header, never in a URL.
/// </summary>
public partial class SharedLink : ComponentBase
{
    /// <summary>The gateway's HttpClient without a token, registered by the Blazor host.</summary>
    private const string PublicClient = "WebApi.Public";

    [Inject] private IHttpClientFactory HttpClients { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizerFactory Texts { get; set; } = default!;

    [Parameter] public string Tenant { get; set; } = "";
    [Parameter] public string Token { get; set; } = "";
    [SupplyParameterFromQuery(Name = "folder")] public Guid? Folder { get; set; }

    private IStringLocalizer? _l;
    private IStringLocalizer L => _l ??= Texts.Create(GetType());

    private SharedView? _view;
    private bool _loading = true;
    private bool _needsPassword;
    private bool _wrongPassword;
    private string? _password;
    private string _passwordInput = "";
    private string? _gone;
    private string? _error;

    private StorageClient Api => new(HttpClients.CreateClient(PublicClient));

    protected override Task OnParametersSetAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            _view = await Api.OpenShareAsync(Tenant, Token, Folder, _password);
            _needsPassword = false;
        }
        catch (StorageApiException ex) when (ex.StatusCode == 401)
        {
            _wrongPassword = _password is not null;
            _needsPassword = true;
            _password = null;
        }
        catch (StorageApiException ex) when (ex.StatusCode == 404)
        {
            _gone = L["This link does not work any more: it was removed, it expired, or it reached its download limit."];
        }
        catch (Exception ex) when (ex is StorageApiException or HttpRequestException)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task UnlockAsync()
    {
        _password = _passwordInput;
        _passwordInput = "";
        await LoadAsync();
    }

    /// <summary>Asks for a short-lived URL of the file (one download on the link's count) and opens it.</summary>
    private async Task OpenAsync(SharedEntryDto file, bool inline)
    {
        _loading = true;
        _error = null;
        try
        {
            var link = await Api.OpenSharedFileAsync(Tenant, Token, file.Id, inline, _password);
            var url = link.Url.IsAbsoluteUri || HttpClients.CreateClient(PublicClient).BaseAddress is not { } gateway
                ? link.Url.ToString() : new Uri(gateway, link.Url).ToString();
            if (inline) await Js.InvokeVoidAsync("open", url, "_blank", "noopener");
            else await Js.InvokeVoidAsync("location.assign", url); // an attachment: the page stays
        }
        catch (StorageApiException ex) when (ex.StatusCode == 404)
        {
            _gone = L["This link does not work any more: it was removed, it expired, or it reached its download limit."];
        }
        catch (Exception ex) when (ex is StorageApiException or HttpRequestException or JSException)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private string FolderHref(Guid id) =>
        _view is { } v && id == v.Shared.Id ? $"s/{Uri.EscapeDataString(Tenant)}/{Token}" : $"s/{Uri.EscapeDataString(Tenant)}/{Token}?folder={id}";

    private string SizeText(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} {L["bytes"]}" : $"{value:0.#} {units[unit]}";
    }
}
