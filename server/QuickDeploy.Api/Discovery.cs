using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuickDeploy;

public sealed class SshProfile
{
    public string User { get; set; } = "";
    public string IdentityFile { get; set; } = "";
    public string KnownHostsFile { get; set; } = "";
    public string[] AllowedHosts { get; set; } = [];
}
public sealed record DiscoveryObservation(string InstanceId, string InventoryRevision, DateTimeOffset CheckedAt,
    string Outcome, string ServiceState, string Health, string? InstalledVersion, string? RunningVersion,
    string? BuildId, string? Commit, string VersionSource, string? Detail,
    DateTimeOffset? LastSuccessfulAt = null, string? LastKnownRunningVersion = null);
public interface IReadOnlyProbe
{
    Task<DiscoveryObservation> Inspect(InventoryServer server, InventoryInstance instance, string revision, CancellationToken cancellationToken);
}
public sealed class SshReadOnlyProbe(IConfiguration configuration) : IReadOnlyProbe
{
    // Fixed read-only script: no deployment, file writes, sudo or service control.
    public const string Script = """
        unit=$1
        root=$2
        version_url=$3
        health_url=$4
        printf 'QD_SERVICE\n'
        systemctl show "$unit" --no-pager --property=LoadState --property=ActiveState --property=SubState --property=MainPID 2>/dev/null || exit 11
        pid_before=$(systemctl show "$unit" --property=MainPID --value 2>/dev/null)
        printf 'QD_MANIFEST\n'
        if [ -r "$root/current/release.json" ]; then
          head -c 16384 "$root/current/release.json" | base64 | tr -d '\n'
        fi
        printf '\nQD_VERSION\n'
        if [ -n "$version_url" ]; then
          body=$(curl --silent --fail --noproxy '*' --connect-timeout 2 --max-time 5 --max-filesize 16384 --proto '=http' "$version_url" 2>/dev/null) && printf '%s' "$body" | base64 | tr -d '\n'
        fi
        printf '\nQD_HEALTH\n'
        if [ -n "$health_url" ]; then
          curl --silent --noproxy '*' --connect-timeout 2 --max-time 5 --max-filesize 16384 --proto '=http' --output /dev/null --write-out '%{http_code}' "$health_url" 2>/dev/null
        fi
        printf '\nQD_PID_AFTER\n'
        systemctl show "$unit" --property=MainPID --value 2>/dev/null
        printf 'QD_PID_BEFORE\n%s\n' "$pid_before"
        """;

    public async Task<DiscoveryObservation> Inspect(InventoryServer server, InventoryInstance instance, string revision, CancellationToken cancellationToken)
    {
        DiscoveryObservation Failure(string outcome, string detail) => new(instance.Id, revision, DateTimeOffset.UtcNow, outcome, "unknown", "unknown", null, null, null, null, "none", detail);
        var profile = configuration.GetSection($"Discovery:Profiles:{server.SshProfile}").Get<SshProfile>();
        if (profile is null) return Failure("unconfigured", $"SSH profile '{server.SshProfile}' is not configured on the backend.");
        if (profile.AllowedHosts is null || !profile.AllowedHosts.Contains(server.Host, StringComparer.OrdinalIgnoreCase)) return Failure("unconfigured", "Host is not in this SSH profile's backend allowlist.");
        if (profile.User is null || !Regex.IsMatch(profile.User, "^[a-zA-Z_][a-zA-Z0-9_-]{0,63}$") || !File.Exists(profile.IdentityFile) || !File.Exists(profile.KnownHostsFile))
            return Failure("unconfigured", "SSH profile requires a valid user, readable identity file and pinned known_hosts file.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var start = new ProcessStartInfo(configuration["Discovery:SshExecutable"] ?? "ssh")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-F", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", "-T",
            "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "UpdateHostKeys=no",
            "-o", "ForwardAgent=no", "-o", "ClearAllForwardings=yes", "-o", "IdentitiesOnly=yes",
            "-o", "ConnectTimeout=5", "-o", "ConnectionAttempts=1", "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=2",
            "-o", $"UserKnownHostsFile=\"{Path.GetFullPath(profile.KnownHostsFile)}\"",
            "-i", Path.GetFullPath(profile.IdentityFile), "-p", server.Port.ToString(), "-l", profile.User, "--", server.Host,
            $"sh -s -- {Quote(instance.ServiceName)} {Quote(instance.InstallRoot)} {Quote(instance.VersionUrl ?? "")} {Quote(instance.HealthUrl ?? "")}" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var stdout = ReadBounded(process.StandardOutput, timeout.Token);
            var stderr = ReadBounded(process.StandardError, timeout.Token);
            await process.StandardInput.WriteAsync(Script.Replace("\r\n", "\n").AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr;
            if (process.ExitCode != 0) return Failure("unreachable", $"SSH probe exited with code {process.ExitCode}. Check connectivity, host keys and reader permissions.");
            return Parse(output, instance, revision);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("unreachable", "Discovery timed out after 25 seconds."); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or FormatException or JsonException)
        { return Failure("error", "Discovery could not read a valid response. Check SSH availability and the service's version/manifest format."); }
        finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    }
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            output.Append(buffer, 0, count);
            if (output.Length > 128 * 1024) throw new IOException("Probe response exceeded its limit.");
        }
        return output.ToString();
    }
    public static DiscoveryObservation Parse(string output, InventoryInstance instance, string revision)
    {
        var parts = Regex.Split(output.Replace("\r\n", "\n"), "(?m)^QD_(SERVICE|MANIFEST|VERSION|HEALTH|PID_AFTER|PID_BEFORE)\\n");
        var sections = new Dictionary<string, string>();
        for (var index = 1; index + 1 < parts.Length; index += 2) sections[parts[index]] = parts[index + 1].Trim();
        if (!sections.ContainsKey("SERVICE") || !sections.ContainsKey("PID_AFTER") || !sections.ContainsKey("PID_BEFORE")) throw new IOException("Incomplete probe output.");
        var properties = sections["SERVICE"].Split('\n').Where(s => s.Contains('=')).Select(s => s.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var state = properties.GetValueOrDefault("LoadState") == "not-found" ? "not-found" : properties.GetValueOrDefault("ActiveState", "unknown");
        var stableProcess = sections["PID_BEFORE"] == sections["PID_AFTER"] && sections["PID_BEFORE"] == properties.GetValueOrDefault("MainPID") &&
            int.TryParse(sections["PID_AFTER"], out var pid) && pid > 0;
        List<string> detail = [];
        Dictionary<string, string?> Metadata(string name)
        {
            if (!sections.TryGetValue(name, out var encoded) || string.IsNullOrWhiteSpace(encoded)) return [];
            try
            {
                using var json = JsonDocument.Parse(Convert.FromBase64String(encoded));
                return json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length <= 256)
                    .ToDictionary(p => p.Name, p => p.Value.GetString());
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException)
            { detail.Add($"{name.ToLowerInvariant()} metadata is not a valid JSON object."); return []; }
        }
        var installed = Metadata("MANIFEST");
        var running = Metadata("VERSION");
        var identityMatches = running.GetValueOrDefault("application") == instance.ApplicationId &&
            running.GetValueOrDefault("instanceId") == instance.Id;
        string? runningVersion = null;
        if (state == "active" && stableProcess && identityMatches && !string.IsNullOrWhiteSpace(running.GetValueOrDefault("version"))) runningVersion = running["version"];
        else if (state == "active") detail.Add(!stableProcess ? "Process changed during discovery; running version is unknown." : "Running version unavailable or endpoint identity does not match this application/instance.");
        var health = string.IsNullOrEmpty(instance.HealthUrl) ? "not-configured" : state != "active" ? "unknown" :
            int.TryParse(sections.GetValueOrDefault("HEALTH"), out var code) && code is >= 200 and < 300 ? "healthy" : "unhealthy";
        var installedVersion = installed.GetValueOrDefault("application") == instance.ApplicationId ? installed.GetValueOrDefault("version") : null;
        if (installed.Count > 0 && installedVersion is null) detail.Add("Installed manifest identity does not match the application, or version is missing.");
        return new(instance.Id, revision, DateTimeOffset.UtcNow, "observed", state, health, installedVersion,
            runningVersion, runningVersion is null ? null : running.GetValueOrDefault("buildId"),
            runningVersion is null ? null : running.GetValueOrDefault("commit"), runningVersion is null ? "none" : "version-endpoint",
            detail.Count == 0 ? null : string.Join(" ", detail));
    }
}

public sealed class DiscoveryBusyException() : Exception("A discovery scan is already running. Try again when it finishes.");
public sealed class DiscoveryService(InventoryStore inventory, IReadOnlyProbe probe, string statePath)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<List<DiscoveryObservation>> Read(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(statePath)) return [];
        if (new FileInfo(statePath).Length > 16 * 1024 * 1024) throw new IOException("Discovery state is too large.");
        return JsonSerializer.Deserialize<List<DiscoveryObservation>>(await File.ReadAllTextAsync(statePath, cancellationToken), Json) ?? [];
    }
    public async Task<List<DiscoveryObservation>> Scan(string? instanceId, CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new DiscoveryBusyException();
        try
        {
            var snapshot = await inventory.Read(cancellationToken);
            var targets = snapshot.Inventory.Instances.Where(i => instanceId is null || i.Id == instanceId).ToArray();
            if (instanceId is not null && targets.Length == 0) throw new InventoryValidationException(["Instance does not exist in saved inventory."]);
            var previous = await Read(cancellationToken);
            var results = new System.Collections.Concurrent.ConcurrentBag<DiscoveryObservation>();
            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (instance, token) =>
            {
                var server = snapshot.Inventory.Servers.Single(s => s.Id == instance.ServerId);
                var observation = await probe.Inspect(server, instance, snapshot.Revision, token);
                var last = previous.FirstOrDefault(o => o.InstanceId == instance.Id && o.InventoryRevision == snapshot.Revision);
                observation = observation.Outcome == "observed" ? observation with { LastSuccessfulAt = observation.CheckedAt, LastKnownRunningVersion = observation.RunningVersion } :
                    observation with { LastSuccessfulAt = last?.LastSuccessfulAt, LastKnownRunningVersion = last?.LastKnownRunningVersion };
                results.Add(observation);
            });
            var combined = previous.Where(o => !targets.Any(t => t.Id == o.InstanceId) && snapshot.Inventory.Instances.Any(i => i.Id == o.InstanceId)).Concat(results).OrderBy(o => o.InstanceId).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
            var temp = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(combined, Json), cancellationToken);
                File.Move(temp, statePath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return combined;
        }
        finally { gate.Release(); }
    }
}
