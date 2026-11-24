using soft.square.net.ModularBlazor.Abstractions;

// Default settings of this module: read by the WebApi's client-modules core module when the file is uploaded.
// Open to everyone (no sign-in): a share link is its own authorization, checked by the Storage API. No menu entry; the
// links the Share dialog hands out (/s/{tenant}/{token}) are the way in.
[assembly: ClientModule(Name = "Shared links", Layout = "public", RequiresAuth = false, RoutePrefixes = new[] { "s" },
    Dependencies = new[] { "WebApi.Modules.Storage.Client" }, Styles = new[] { "shares.css" })]
