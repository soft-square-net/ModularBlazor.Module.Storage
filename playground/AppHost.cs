// Storage playground: a small platform with this repo's module in it, for local development. Two loops:
//
//   dev (default, launch profile "dev"): the WebApi loads the module, and its API client, straight from their build
//     output (Modules:Local and ClientModules:Local, Development only). Change code, rebuild, restart the "webapi"
//     resource in the dashboard (or F5 again): the new build is installed and active, with no packing or uploading.
//   install (launch profile "install"): the platform starts without the module, as in production. Every build zips
//     the module to playground/out/storage.zip; upload it on the client's Host Modules page (http://localhost:5070,
//     sign in as the dev platform admin) and activate it.
//
//   postgres ─► identity (5090)    tenant store + tenant databases + identity database, one server
//            └► webapi (5080)  ◄── blzwebclient (5070)
//
// Blobs (the local disk provider) go to playground/.data/blobs. MinIO joins in phase 2 (S3 provider).
// The platform's projects build from the ModularBlazor.Platform checkout next to this repo (Storage.Playground.csproj).
// It uses the same ports as the platform's Hybird lab, so stop the lab first. Data stays in the Docker volume
// "storage-playground-data" and in playground/.data.

using System.Reflection;

var builder = DistributedApplication.CreateBuilder(args);

var mode = (builder.Configuration["Playground:Mode"] ?? "dev").Trim().ToLowerInvariant();
if (mode is not ("dev" or "install"))
    throw new InvalidOperationException($"Playground:Mode '{mode}' is not supported: use dev or install.");

// Fixed by the platform: the identity service registers the client's callbacks on 5070 and the client calls the API on 5080.
const int IdentityPort = 5090;
const int WebApiPort = 5080;
const int ClientPort = 5070;
const string Issuer = "http://localhost:5090/";
const string ClientOrigin = "http://localhost:5070";

// One PostgreSQL server, named like the lab's ("tenant-data"): the tenant store, the identity database and the tenants'
// databases. Create a tenant on it with POST /api/tenancy/tenants {"id":"acme","databaseServer":"tenant-data"}.
var tenantData = builder.AddPostgres("tenant-data")
    .WithDataVolume("storage-playground-data")
    .WithPgAdmin();
var tenantStore = tenantData.AddDatabase("TenantStore", databaseName: "PlaygroundTenants");
var identityDb = tenantData.AddDatabase("identity-db", databaseName: "identity");

// Development settings of the identity service: dev accounts (admin@modular.local, ...) and the dev client secrets.
var identity = builder.AddProject<Projects.WebApi_Identity_Server>("identity", launchProfileName: null)
    .WithHttpEndpoint(port: IdentityPort)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Identity__Issuer", Issuer)
    .WithReference(identityDb, connectionName: "identity").WaitFor(identityDb)
    .WithHttpHealthCheck("/health");

var data = Metadata("PlaygroundData");
var catalog = Path.Combine(data, mode == "dev" ? "modules" : "modules-install");
var clientCatalog = Path.Combine(data, mode == "dev" ? "client-modules" : "client-modules-install");
var webApi = builder.AddProject<Projects.WebApi>("webapi", launchProfileName: null)
    .WithHttpEndpoint(port: WebApiPort)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Modules__Pool", "playground")
    .WithEnvironment("Modules__Role", "Api")
    .WithEnvironment("Modules__Path", catalog)
    .WithEnvironment("Modules__Settings__client-modules__ClientModules__Path", clientCatalog)
    // The module's local disk provider (a host override of the module's setting Storage:Local:Root).
    .WithEnvironment("Modules__Settings__storage__Storage__Local__Root", Path.Combine(data, "blobs"))
    .WithReference(tenantStore).WaitFor(tenantStore)
    .WithEnvironment("DatabaseServers__TenantStore__Provider", "PostgreSql")
    .WithReference(tenantData)
    .WithEnvironment("DatabaseServers__tenant-data__Provider", "PostgreSql")
    .WithEnvironment("Auth__Authority", Issuer)
    .WithEnvironment("Auth__Audience", "modular-webapi")
    .WithEnvironment("Cors__AllowedOrigins__0", ClientOrigin)
    // Development only (Access:DevGrants): the module's permissions for the dev accounts of tenant acme, written as
    // ordinary grants (source "dev") the first time each one calls the API there. acme-admin administers Storage;
    // editor@modular.local is a plain Storage user, so the access lists can be tried with two people.
    .WithEnvironment("Access__DevGrants__0__Tenant", "acme")
    .WithEnvironment("Access__DevGrants__0__Email", "acme-admin@modular.local")
    .WithEnvironment("Access__DevGrants__0__Permissions__0", "storage.*")
    .WithEnvironment("Access__DevGrants__1__Tenant", "acme")
    .WithEnvironment("Access__DevGrants__1__Email", "editor@modular.local")
    .WithEnvironment("Access__DevGrants__1__Permissions__0", "storage.access")
    .WaitFor(identity)
    .WithHttpHealthCheck("/health");
if (mode == "dev")
{
    const string ClientLocal = "Modules__Settings__client-modules__ClientModules__Local__";
    webApi.WithEnvironment("Modules__Local__storage", Metadata("ModuleOutput"))
        .WithEnvironment(ClientLocal + "WebApi.Modules.Storage.Client", Metadata("ApiClientOutput"));
}

builder.AddProject<Projects.ModularBlazor_BlzWebClient>("blzwebclient", launchProfileName: null)
    .WithHttpEndpoint(port: ClientPort)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WaitFor(identity)
    .WaitFor(webApi);

if (mode == "install")
    Console.WriteLine($"Install loop: upload {Metadata("ModulePackage")} on {ClientOrigin}/host-modules, then activate it.");

builder.Build().Run();

// Paths the project file computes (AssemblyMetadata in Storage.Playground.csproj).
static string Metadata(string key) =>
    Assembly.GetEntryAssembly()!.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value!;
