using soft.square.net.ModularBlazor.Abstractions;

// Default settings of this module: read by the WebApi's client-modules core module when the file is uploaded.
// Open to every signed-in user: what each one may see and change is decided by the Storage API (storage.access, then
// the folders' access lists), and the explorer shows its answer. The API is tenant-bound: sign in to a tenant.
[assembly: ClientModule(Name = "Files", Layout = "default", RoutePrefixes = new[] { "files" },
    Dependencies = new[] { "WebApi.Modules.Storage.Client" }, Styles = new[] { "storage.css" },
    NavTitle = "Files", NavHref = "files", NavOrder = 45)]
