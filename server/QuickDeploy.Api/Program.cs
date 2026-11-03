using QuickDeploy;

var builder = WebApplication.CreateBuilder(args);
var repository = new DirectoryInfo(builder.Environment.ContentRootPath);
while (repository is not null && !(Directory.Exists(Path.Combine(repository.FullName, "web")) && Directory.Exists(Path.Combine(repository.FullName, "server")))) repository = repository.Parent;
var localConfiguration = repository is null ? Path.Combine(builder.Environment.ContentRootPath, "appsettings.Local.json") : Path.Combine(repository.FullName, "server/QuickDeploy.Api/appsettings.Local.json");
builder.Configuration.AddJsonFile(localConfiguration, optional: true).AddEnvironmentVariables();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
var defaultInventoryPath = repository is not null ? Path.Combine(repository.FullName, "data/inventory/demo.yaml") :
    OperatingSystem.IsLinux() ? "/var/lib/quickdeploy/inventory/demo.yaml" : Path.Combine(builder.Environment.ContentRootPath, "data/inventory/demo.yaml");
var inventoryPath = Path.GetFullPath(builder.Configuration["Inventory:Path"] ?? defaultInventoryPath);
builder.Services.AddSingleton(new InventoryStore(inventoryPath));
builder.Services.AddSingleton<IReadOnlyProbe, SshReadOnlyProbe>();
builder.Services.AddSingleton(services => new DiscoveryService(services.GetRequiredService<InventoryStore>(),
    services.GetRequiredService<IReadOnlyProbe>(), inventoryPath + ".discovery.json"));
var app = builder.Build();

app.Use(async (context, next) =>
{
    // Local-only editor; no login in this version. Vite proxies API requests to this loopback service.
    if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers.TryGetValue("Origin", out var origin) &&
        origin != $"{context.Request.Scheme}://{context.Request.Host}" &&
        origin != "http://127.0.0.1:5174" && origin != "http://localhost:5174")
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (InventoryValidationException exception) { await Error(400, "Invalid inventory", exception.Message, exception.Errors); }
    catch (InventoryConflictException exception) { await Error(409, "Inventory conflict", exception.Message); }
    catch (DiscoveryBusyException exception) { await Error(409, "Discovery already running", exception.Message); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
    {
        app.Logger.LogError(exception, "Inventory/discovery storage operation failed");
        await Error(503, "Storage unavailable", "Cannot read or save backend data. Check file permissions, file validity and whether another backend is saving it.");
    }
    Task Error(int status, string title, string detail, string[]? errors = null) => Results.Problem(statusCode: status,
        title: title, detail: detail, extensions: errors is null ? null : new Dictionary<string, object?> { ["errors"] = errors }).ExecuteAsync(context);
});
app.MapGet("/api/health", () => new { status = "ok", mode = "inventory-and-readonly-discovery", deploymentEnabled = false });
app.MapGet("/api/inventory", (InventoryStore store, CancellationToken token) => store.Read(token));
app.MapPut("/api/inventory", (SaveInventory request, InventoryStore store, CancellationToken token) => store.Save(request.BaseRevision, request.Inventory, token));
app.MapPost("/api/inventory/import", (ImportInventory request, InventoryStore store, CancellationToken token) => store.Save(request.BaseRevision, store.Parse(request.Yaml), token));
app.MapGet("/api/inventory/export", async (InventoryStore store, CancellationToken token) => Results.Text((await store.Read(token)).Yaml, "application/yaml"));
app.MapGet("/api/discovery", (DiscoveryService discovery, CancellationToken token) => discovery.Read(token));
app.MapPost("/api/discovery", (string? instanceId, DiscoveryService discovery, CancellationToken token) => discovery.Scan(instanceId, token));
app.MapGet("/api/servers/{id}/instances", async (string id, InventoryStore store, CancellationToken token) =>
{
    var snapshot = await store.Read(token);
    return snapshot.Inventory.Servers.Any(s => s.Id == id) ? Results.Ok(snapshot.Inventory.Instances.Where(i => i.ServerId == id)) : Results.NotFound();
});
app.MapGet("/api/applications/{id}/instances", async (string id, InventoryStore store, CancellationToken token) =>
{
    var snapshot = await store.Read(token);
    return snapshot.Inventory.Applications.Any(a => a.Id == id) ? Results.Ok(snapshot.Inventory.Instances.Where(i => i.ApplicationId == id)) : Results.NotFound();
});
app.MapPost("/api/deployments", () => Results.Problem(title: "Deployment adapter not configured",
    detail: "Inventory and discovery are available. Installation and restart execution are not enabled.", statusCode: 501));
app.Run("http://127.0.0.1:5080");
