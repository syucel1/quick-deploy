var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// This API deliberately exposes inventory only until real deployment adapters exist.
// Bind to loopback while authentication is outside the scope of this version.
var applicationNames = new[] { "Pricing Service", "Order Gateway", "Market Data", "Risk Engine", "Trade Capture", "Position Service", "Reference Data", "FIX Gateway", "Execution Router", "Quote Engine", "Settlement", "Reporting", "Limit Service", "Session Manager", "Trade Enrichment", "Notification Service" };
var applications = applicationNames.Select(name => new Application(name.ToLowerInvariant().Replace(' ', '-'), name)).ToArray();
var servers = Enumerable.Range(1, 8).Select(i => new Server($"demo-srv-{i:00}", "demo", "linux")).ToArray();
var instances = applications.SelectMany((application, index) => Enumerable.Range(0, 3).Select(i => new Instance(
    $"{application.Id}-{i + 1:00}", application.Id, servers[(index + i) % servers.Length].Id,
    $"{application.Id}@{i + 1}.service", $"/opt/quickdeploy/{application.Id}/{i + 1}"))).ToArray();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", mode = "sample", deploymentEnabled = false }));
app.MapGet("/api/inventory", () => Results.Ok(new { applications, servers, instances }));
app.MapGet("/api/servers/{id}/instances", (string id) => servers.Any(server => server.Id == id)
    ? Results.Ok(instances.Where(instance => instance.ServerId == id)) : Results.NotFound());
app.MapGet("/api/applications/{id}/instances", (string id) => applications.Any(application => application.Id == id)
    ? Results.Ok(instances.Where(instance => instance.ApplicationId == id)) : Results.NotFound());
app.MapPost("/api/deployments", () => Results.Problem(
    title: "Deployment adapter not configured",
    detail: "Configure TeamCity, inventory persistence, and an SSH/systemd execution adapter before enabling deployments.",
    statusCode: StatusCodes.Status501NotImplemented));

app.Run("http://127.0.0.1:5080");

record Application(string Id, string Name);
record Server(string Id, string Environment, string OperatingSystem);
record Instance(string Id, string ApplicationId, string ServerId, string ServiceName, string InstallationPath);
