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
