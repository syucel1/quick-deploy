using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using QuickDeploy;

// Executed only by the transport test as a stand-in for ssh; no remote host is contacted.
if (args.Contains("-F"))
{
    var script = await Console.In.ReadToEndAsync();
    if (!args.Contains("StrictHostKeyChecking=yes") || !args.Contains("BatchMode=yes") || !args.Contains("ForwardAgent=no") ||
        !args.Contains("IdentitiesOnly=yes") || script != SshReadOnlyProbe.Script.Replace("\r\n", "\n") ||
        !args.Last().StartsWith("sh -s -- 'pricing@01.service' '/opt/trading/pricing-01'")) return 7;
    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"application\":\"pricing\",\"instanceId\":\"pricing-01\",\"version\":\"2.0\"}"));
    Console.Write($"QD_SERVICE\nLoadState=loaded\nActiveState=active\nMainPID=42\nQD_MANIFEST\n{encoded}\nQD_VERSION\n{encoded}\nQD_HEALTH\n200\nQD_PID_AFTER\n42\nQD_PID_BEFORE\n42\n");
    return 0;
}

var directory = Path.Combine(Path.GetTempPath(), "quickdeploy-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var cases = new List<(string Name, Func<Task> Run)>();
var sample = new InventoryDocument
{
    Servers = [new() { Id = "demo-01", Name = "Demo 01", Host = "demo-01.example.internal", SshProfile = "demo-reader" }],
    Applications = [new() { Id = "pricing", Name = "Pricing Service" }],
    Instances = [new() { Id = "pricing-01", ApplicationId = "pricing", ServerId = "demo-01", ServiceName = "pricing@01.service", InstallRoot = "/opt/trading/pricing-01", VersionUrl = "http://127.0.0.1:8101/version", HealthUrl = "http://127.0.0.1:8101/health" }]
};
InventoryDocument Clone() => JsonSerializer.Deserialize<InventoryDocument>(JsonSerializer.Serialize(sample))!;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Invalid(Action action) { try { action(); } catch (InventoryValidationException) { return; } throw new Exception("Expected validation rejection."); }
string ProbeOutput(string? version = "2.0", string app = "pricing", string state = "active", string pidAfter = "42", bool malformed = false)
{
    string Encode(object value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
    var manifest = Encode(new { application = "pricing", version = "3.0" });
    var running = malformed ? Convert.ToBase64String(Encoding.UTF8.GetBytes("not-json")) : version is null ? "" : Encode(new { application = app, instanceId = "pricing-01", version, buildId = "1842", commit = "abc123" });
    return $"QD_SERVICE\nLoadState=loaded\nActiveState={state}\nMainPID=42\nQD_MANIFEST\n{manifest}\nQD_VERSION\n{running}\nQD_HEALTH\n200\nQD_PID_AFTER\n{pidAfter}\nQD_PID_BEFORE\n42\n";
}

cases.Add(("YAML round-trip, revision conflict, and persisted reload", async () =>
{
    var path = Path.Combine(directory, "inventory.yaml");
    var store = new InventoryStore(path);
    Assert((await store.Read()).Revision == "empty", "Missing inventory should be empty.");
    var first = await store.Save("empty", sample);
    Assert(first.Yaml.Contains("serviceName:"), "Inventory must be readable YAML.");
    var reopened = await new InventoryStore(path).Read();
    Assert(reopened.Inventory.Instances[0].ServiceName == "pricing@01.service", "YAML did not round-trip.");
    Assert(first.Revision == reopened.Revision, "Revision is not stable.");
    var changed = Clone();changed.Environment = "staging";
    await store.Save(first.Revision, changed);
    try { await store.Save(first.Revision, sample);throw new Exception("Overwrote a newer revision."); } catch (InventoryConflictException) { }
    Assert((await store.Read()).Inventory.Environment == "staging", "Conflicting save altered the file.");
    Assert(!Directory.GetFiles(directory, "*.tmp").Any(), "Temporary files were not cleaned.");
}));
cases.Add(("Reject missing references, duplicate services/hosts and unsafe probe targets", () =>
{
    var document = Clone();document.Instances[0] = document.Instances[0] with { ServerId = "missing" };Invalid(() => InventoryValidation.Validate(document));
    document = Clone();document.Instances.Add(document.Instances[0] with { Id = "pricing-02", InstallRoot = "/other" });Invalid(() => InventoryValidation.Validate(document));
    document = Clone();document.Servers.Add(document.Servers[0] with { Id = "alias" });Invalid(() => InventoryValidation.Validate(document));
    foreach (var url in new[] { "http://example.com/version", "file:///etc/passwd", "http://127.0.0.1@evil.test/version", "http://127.0.0.1/version#fragment" })
    { document = Clone();document.Instances[0] = document.Instances[0] with { VersionUrl = url };Invalid(() => InventoryValidation.Validate(document)); }
    document = Clone();document.Servers[0] = document.Servers[0] with { Host = "-oProxyCommand=bad" };Invalid(() => InventoryValidation.Validate(document));
    document = Clone();document.Instances[0] = document.Instances[0] with { InstallRoot = "/opt/../etc" };Invalid(() => InventoryValidation.Validate(document));
    document = Clone();document.Instances[0] = document.Instances[0] with { ServiceName = "pricing;shutdown.service" };Invalid(() => InventoryValidation.Validate(document));
    document = Clone();document.Servers = null!;Invalid(() => InventoryValidation.Validate(document));
    return Task.CompletedTask;
}));
cases.Add(("Reject malformed, unknown-field and duplicate-key YAML without replacing saved data", async () =>
{
    var store = new InventoryStore(Path.Combine(directory, "parse.yaml"));
    var original = await store.Save("empty", sample);
    foreach (var yaml in new[] { "schemaVersion: [", "schemaVersion: 1\nschemaVersion: 2", "schemaVersion: 1\npassword: secret", "servers: null", "instances:\n- null", "", new string('a', InventoryStore.MaxBytes + 1) }) Invalid(() => store.Parse(yaml));
    Assert((await store.Read()).Revision == original.Revision, "Invalid YAML altered the inventory.");
}));
cases.Add(("Never infer running version from installed manifest", () =>
{
    var result = SshReadOnlyProbe.Parse(ProbeOutput(version: null), sample.Instances[0], "rev");
    Assert(result.InstalledVersion == "3.0" && result.RunningVersion is null, "Installed version was reported as running.");
    return Task.CompletedTask;
}));
cases.Add(("Version identity, stable process, service status and malformed metadata checks", () =>
{
    var instance = sample.Instances[0];
    var result = SshReadOnlyProbe.Parse(ProbeOutput(), instance, "rev");
    Assert(result.RunningVersion == "2.0" && result.InstalledVersion == "3.0" && result.Health == "healthy" && result.BuildId == "1842", "Observed metadata was not parsed.");
    foreach (var output in new[] { ProbeOutput(app: "other"), ProbeOutput(state: "inactive"), ProbeOutput(pidAfter: "43"), ProbeOutput(malformed: true) })
        Assert(SshReadOnlyProbe.Parse(output, instance, "rev").RunningVersion is null, "Unverified running version was reported.");
    return Task.CompletedTask;
}));
cases.Add(("Missing SSH configuration is explicit and causes no connection attempt", async () =>
{
    var result = await new SshReadOnlyProbe(new ConfigurationBuilder().Build()).Inspect(sample.Servers[0], sample.Instances[0], "rev", default);
    Assert(result.Outcome == "unconfigured" && result.RunningVersion is null, "Missing profile did not fail closed.");
    Assert(SshReadOnlyProbe.Quote("a'b") == "'a'\"'\"'b'", "Shell argument quoting is broken.");
}));
cases.Add(("SSH process transport passes fixed script, strict host checks and parses bounded output", async () =>
{
    var key = Path.Combine(directory, "reader identity");var knownHosts = Path.Combine(directory, "known hosts");
    await File.WriteAllTextAsync(key, "fixture");await File.WriteAllTextAsync(knownHosts, "fixture");
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    {
        ["Discovery:SshExecutable"] = Path.Combine(AppContext.BaseDirectory, "QuickDeploy.Tests" + (OperatingSystem.IsWindows() ? ".exe" : "")),
        ["Discovery:Profiles:demo-reader:User"] = "reader",
        ["Discovery:Profiles:demo-reader:IdentityFile"] = key,
        ["Discovery:Profiles:demo-reader:KnownHostsFile"] = knownHosts,
        ["Discovery:Profiles:demo-reader:AllowedHosts:0"] = sample.Servers[0].Host
    }).Build();
    var result = await new SshReadOnlyProbe(configuration).Inspect(sample.Servers[0],sample.Instances[0],"rev",default);
    Assert(result.Outcome == "observed" && result.RunningVersion == "2.0", "SSH transport fixture failed: " + result.Detail);
    var rejected = await new SshReadOnlyProbe(configuration).Inspect(sample.Servers[0] with {Host = "not-allowed.internal"}, sample.Instances[0], "rev", default);
    Assert(rejected.Outcome == "unconfigured", "A host outside the profile allowlist was contacted.");
}));
cases.Add(("Discovery persists observations separately and preserves last known version after failure", async () =>
{
    var path = Path.Combine(directory, "discovery-inventory.yaml");
    var store = new InventoryStore(path);var saved = await store.Save("empty", sample);
    var probe = new FakeProbe();var discovery = new DiscoveryService(store, probe, path + ".discovery.json");
    var observations = await discovery.Scan(null, default);
    Assert(observations[0].RunningVersion == "2.0", "Discovery did not store running version.");
    probe.Fail = true;observations = await discovery.Scan(null, default);
    Assert(observations[0].RunningVersion is null && observations[0].LastKnownRunningVersion == "2.0", "A failure must preserve explicitly historical data, not current state.");
    Assert((await store.Read()).Revision == saved.Revision, "Discovery modified inventory YAML.");
    Assert((await new DiscoveryService(store, probe, path + ".discovery.json").Read())[0].Outcome == "unreachable", "Observation did not survive reload.");
}));
var teamCityBase = "https://tc.example.internal/teamcity/";
var package = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("verified-package-bytes;", 20000)));
var packageSha = Convert.ToHexStringLower(SHA256.HashData(package));
var teamCityOptions = new TeamCityOptions { BaseUrl = teamCityBase, Token = "tc-token" };
async Task<InventoryStore> ArtifactInventory(string name, Action<InventoryDocument>? change = null)
{
    var document = Clone();
    document.Applications[0] = document.Applications[0] with { TeamCityBuildType = "Pricing_Release", ArtifactPath = "dist/pricing.tar.gz" };
    change?.Invoke(document);
    var store = new InventoryStore(Path.Combine(directory, name + ".yaml"));
    await store.Save("empty", document);
    return store;
}
cases.Add(("TeamCity links resolve one immutable build on the approved server only", () =>
{
    var baseUri = TeamCityLinks.Base("https://tc.example.internal/teamcity");
    Assert(baseUri.ToString() == teamCityBase, "Base URL was not normalised.");
    var link = TeamCityLinks.Parse(baseUri, teamCityBase + "buildConfiguration/Pricing_Release/1842?mode=builds");
    Assert(link.BuildId == 1842 && link.BuildTypeId == "Pricing_Release" && link.ArtifactPath is null, "Build results link was not parsed.");
    Assert(TeamCityLinks.Parse(baseUri, teamCityBase + "viewLog.html?buildId=1842&buildTypeId=Pricing_Release").BuildId == 1842, "viewLog link was not parsed.");
    Assert(TeamCityLinks.Parse(baseUri, teamCityBase + "build/1842").BuildId == 1842, "Short build link was not parsed.");
    link = TeamCityLinks.Parse(baseUri, teamCityBase + "repository/download/Pricing_Release/1842:id/dist/pricing.tar.gz");
    Assert(link.BuildId == 1842 && link.ArtifactPath == "dist/pricing.tar.gz", "Artifact download link was not parsed.");
    Assert(TeamCityLinks.Parse(baseUri, teamCityBase + "app/rest/builds/id:1842/artifacts/files/dist/pricing.tar.gz").ArtifactPath == "dist/pricing.tar.gz", "REST artifact link was not parsed.");
    foreach (var bad in new[] { "https://evil.example/teamcity/build/1842", "http://tc.example.internal/teamcity/build/1842", "https://user:pw@tc.example.internal/teamcity/build/1842",
        "https://tc.example.internal/other/buildConfiguration/Pricing_Release/1842", teamCityBase + "repository/download/Pricing_Release/.lastSuccessful/pricing.tar.gz",
        teamCityBase + "repository/download/Pricing_Release/1842:id/dist/%2E%2E/secret", teamCityBase + "buildConfiguration/Pricing_Release/latest", teamCityBase + "build/0", "1842", "" })
    { try { TeamCityLinks.Parse(baseUri, bad); throw new Exception($"Accepted unsafe or mutable link: {bad}"); } catch (ArtifactImportException e) when (e.Status == 400) { } }
    foreach (var bad in new[] { "", "http://tc.example.internal", "https://u:p@tc.example.internal", "https://tc.example.internal/?a=b" })
    { try { TeamCityLinks.Base(bad); throw new Exception($"Accepted unsafe base URL: {bad}"); } catch (ArtifactImportException e) when (e.Status == 503) { } }
    Assert(TeamCityLinks.Base("http://127.0.0.1:8111").Scheme == "http", "Loopback HTTP base should be allowed for local TeamCity.");
    return Task.CompletedTask;
}));
cases.Add(("Import job downloads with progress, verifies checksum and records traceable build metadata", async () =>
{
    var store = await ArtifactInventory("import");
    var fake = new FakeTeamCity(package, packageSha);
    var library = Path.Combine(directory, "artifacts-ok");
    var service = new ArtifactService(store, teamCityOptions, library, fake);
    Assert(service.Status().Configured && service.Status().BaseUrl == teamCityBase, "Configured TeamCity was reported as unavailable.");
    var job = service.Start(teamCityBase + "buildConfiguration/Pricing_Release/1842", default);
    for (var i = 0; i < 200 && service.Job(job.Id)!.FinishedAt is null; i++) await Task.Delay(25);
    var done = service.Job(job.Id)!;
    Assert(done.State == "completed" && done.Error is null && !done.AlreadyImported, "Import job failed: " + done.Error);
    Assert(done.BytesReceived == package.Length && done.TotalBytes == package.Length, "Progress did not reach the artifact size.");
    var record = done.Artifact!;
    Assert(record.ApplicationId == "pricing" && record.BuildId == "1842" && record.Version == "2.18.5" && record.Commit == "a8e27bd9" && record.Branch == "main", "Build metadata was not recorded.");
    Assert(record.Sha256 == packageSha && record.ChecksumVerified && record.Size == package.Length && record.FileName == "pricing.tar.gz", "Checksum/size metadata is wrong.");
    Assert(record.BuildUrl.StartsWith(teamCityBase), "Build URL must point at the approved TeamCity.");
    var entry = (await service.List()).Single();
    Assert(entry.Status == "available" && (await File.ReadAllBytesAsync(service.FilePath(entry.Artifact))).SequenceEqual(package), "Stored artifact is not retrievable.");
    Assert(fake.Requests.All(r => r.Auth == "Bearer tc-token" && r.Uri.StartsWith(teamCityBase)), "Token sent without bearer auth or to another server.");
    var downloads = fake.Requests.Count(r => r.Uri.EndsWith("/files/dist/pricing.tar.gz"));
    var again = await service.Import(TeamCityLinks.Parse(new Uri(teamCityBase), teamCityBase + "build/1842"), null, default);
    Assert(again.Existing && again.Record.Sha256 == packageSha && fake.Requests.Count(r => r.Uri.EndsWith("/files/dist/pricing.tar.gz")) == downloads, "Re-import downloaded an immutable build again.");
    var reopened = await new ArtifactService(store, teamCityOptions, library, fake).Find("pricing", "1842");
    Assert(reopened?.Status == "available" && reopened.Artifact.Commit == "a8e27bd9", "Library did not survive a backend restart.");
    await File.WriteAllTextAsync(service.FilePath(entry.Artifact), "truncated");
    Assert((await service.Find("pricing", "1842"))!.Status == "damaged", "A truncated stored file was reported as available.");
}));
cases.Add(("Reject unfinished, failed, unmapped, redirected, mismatched and incomplete builds without library changes", async () =>
{
    var store = await ArtifactInventory("reject");
    var link = TeamCityLinks.Parse(new Uri(teamCityBase), teamCityBase + "build/1842");
    var variants = new (string Name, Action<FakeTeamCity> Setup, TeamCityOptions? Options)[]
    {
        ("running build", f => f.State = "running", null), ("failed build", f => f.Status = "FAILURE", null),
        ("unmapped build type", f => f.BuildType = "Other_Release", null), ("checksum mismatch", f => f.Sha = new string('0', 64), null),
        ("truncated download", f => f.Truncate = true, null), ("redirect", f => f.Redirect = true, null), ("bad credentials", f => f.Unauthorized = true, null),
        ("missing checksum when required", f => f.Sha = null, new TeamCityOptions { BaseUrl = teamCityBase, Token = "tc-token", RequireChecksum = true }),
        ("oversized artifact", _ => { }, new TeamCityOptions { BaseUrl = teamCityBase, Token = "tc-token", MaxArtifactBytes = 10 }),
    };
    foreach (var (name, setup, options) in variants)
    {
        var fake = new FakeTeamCity(package, packageSha); setup(fake);
        var library = Path.Combine(directory, "artifacts-" + name.Replace(' ', '-'));
        var service = new ArtifactService(store, options ?? teamCityOptions, library, fake);
        try { await service.Import(link, null, default); throw new Exception($"{name}: import was accepted."); } catch (ArtifactImportException) { }
        Assert((await service.List()).Count == 0, $"{name}: a rejected build entered the library.");
        Assert(!Directory.Exists(Path.Combine(library, ".incoming")) || !Directory.EnumerateFileSystemEntries(Path.Combine(library, ".incoming")).Any(), $"{name}: partial download was left behind.");
    }
    var ambiguous = await ArtifactInventory("ambiguous", d => d.Applications.Add(d.Applications[0] with { Id = "pricing-copy" }));
    try { await new ArtifactService(ambiguous, teamCityOptions, Path.Combine(directory, "artifacts-ambiguous"), new FakeTeamCity(package, packageSha)).Import(link, null, default); throw new Exception("Ambiguous build type accepted."); }
    catch (ArtifactImportException) { }
    var unsigned = await new ArtifactService(store, teamCityOptions, Path.Combine(directory, "artifacts-unsigned"), new FakeTeamCity(package, null)).Import(link, null, default);
    Assert(!unsigned.Record.ChecksumVerified && unsigned.Record.Sha256 == packageSha && unsigned.Record.ChecksumSource == "computed-on-import", "Unpublished checksum must be recorded as computed, not verified.");
}));
cases.Add(("Unconfigured TeamCity fails closed without contacting the server", async () =>
{
    var fake = new FakeTeamCity(package, packageSha);
    var store = await ArtifactInventory("unconfigured");
    foreach (var options in new[] { new TeamCityOptions(), new TeamCityOptions { BaseUrl = teamCityBase } })
    {
        var service = new ArtifactService(store, options, Path.Combine(directory, "artifacts-unconfigured"), fake);
        Assert(!service.Status().Configured, "Missing TeamCity configuration was reported as configured.");
        try { service.Start(teamCityBase + "build/1842", default); throw new Exception("Import started without configuration."); } catch (ArtifactImportException e) when (e.Status == 503) { }
    }
    Assert(fake.Requests.Count == 0, "TeamCity was contacted without configuration.");
}));
var failed = 0;
try
{
    foreach (var (name, run) in cases)
    {
        try { await run();Console.WriteLine($"PASS {name}"); }
        catch (Exception error) { failed++;Console.Error.WriteLine($"FAIL {name}: {error}"); }
    }
}
finally { Directory.Delete(directory, recursive: true); }
Console.WriteLine($"{cases.Count - failed}/{cases.Count} backend checks passed.");
return failed == 0 ? 0 : 1;

sealed class FakeProbe : IReadOnlyProbe
{
    public bool Fail { get; set; }
    public Task<DiscoveryObservation> Inspect(InventoryServer server, InventoryInstance instance, string revision, CancellationToken cancellationToken) =>
        Task.FromResult(new DiscoveryObservation(instance.Id, revision, DateTimeOffset.UtcNow, Fail ? "unreachable" : "observed", Fail ? "unknown" : "active", Fail ? "unknown" : "healthy", Fail ? null : "3.0", Fail ? null : "2.0", null, null, Fail ? "none" : "version-endpoint", null));
}

sealed class FakeTeamCity(byte[] content, string? sha) : HttpMessageHandler
{
    public string State { get; set; } = "finished";
    public string Status { get; set; } = "SUCCESS";
    public string BuildType { get; set; } = "Pricing_Release";
    public string? Sha { get; set; } = sha;
    public bool Truncate { get; set; }
    public bool Redirect { get; set; }
    public bool Unauthorized { get; set; }
    public List<(string Uri, string? Auth)> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!.ToString();
        lock (Requests) Requests.Add((uri, request.Headers.Authorization?.ToString()));
        if (Unauthorized) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        if (Redirect) { var moved = new HttpResponseMessage(HttpStatusCode.Found); moved.Headers.Location = new Uri("https://login.example/"); return Task.FromResult(moved); }
        HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        var path = request.RequestUri.AbsolutePath;
        HttpResponseMessage response =
            path.EndsWith("/app/rest/builds/id:1842") ? Json(new { id = 1842, number = "2.18.5", buildTypeId = BuildType, state = State, status = Status, branchName = "main",
                webUrl = "https://tc.example.internal/teamcity/buildConfiguration/Pricing_Release/1842", revisions = new { revision = new[] { new { version = "a8e27bd9" } } } }) :
            path.EndsWith("/artifacts/metadata/dist/pricing.tar.gz") ? Json(new { name = "pricing.tar.gz", size = content.Length }) :
            path.EndsWith("/artifacts/files/dist/pricing.tar.gz.sha256") ? Sha is null ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new StringContent($"{Sha}  pricing.tar.gz\n") } :
            path.EndsWith("/artifacts/files/dist/pricing.tar.gz") ? new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Truncate ? content[..(content.Length / 2)] : content)) } :
            new(HttpStatusCode.NotFound);
        return Task.FromResult(response);
    }
}
