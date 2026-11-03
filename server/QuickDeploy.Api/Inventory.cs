using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace QuickDeploy;

public sealed class InventoryDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Environment { get; set; } = "demo";
    public List<InventoryServer> Servers { get; set; } = [];
    public List<InventoryApplication> Applications { get; set; } = [];
    public List<InventoryInstance> Instances { get; set; } = [];
}
public sealed record InventoryServer
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public string SshProfile { get; init; } = "";
    public int Port { get; init; } = 22;
}
public sealed record InventoryApplication
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? TeamCityBuildType { get; init; }
    public string? ArtifactPath { get; init; }
}
public sealed record InventoryInstance
{
    public string Id { get; init; } = "";
    public string ApplicationId { get; init; } = "";
    public string ServerId { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public string InstallRoot { get; init; } = "";
    public string? VersionUrl { get; init; }
    public string? HealthUrl { get; init; }
}
public sealed record InventorySnapshot(string Revision, InventoryDocument Inventory, string Yaml);
public sealed record SaveInventory(string BaseRevision, InventoryDocument? Inventory);
public sealed record ImportInventory(string BaseRevision, string Yaml);
public sealed class InventoryValidationException(string[] errors) : Exception("Inventory validation failed")
{
    public string[] Errors { get; } = errors;
}
public sealed class InventoryConflictException() : Exception("The inventory changed. Reload it before saving; your unsaved draft has been kept.");

public static class InventoryValidation
{
    public static bool Id(string? value) => value is not null && Regex.IsMatch(value, "^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    public static bool Host(string? value) => value is not null && value.Length <= 253 && Regex.IsMatch(value, "^[a-zA-Z0-9][a-zA-Z0-9.-]*$", RegexOptions.CultureInvariant);
    public static bool LocalUrl(string? value) => string.IsNullOrEmpty(value) ||
        (value.Length <= 1024 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "http" &&
         new[] { "127.0.0.1", "localhost", "[::1]" }.Contains(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment));

    public static void Validate(InventoryDocument? inventory)
    {
        List<string> errors = [];
        if (inventory is null) throw new InventoryValidationException(["Inventory is required."]);
        if (inventory.SchemaVersion != 1) errors.Add("schemaVersion must be 1.");
        if (!Id(inventory.Environment)) errors.Add("Environment must be a lowercase identifier.");
        if (inventory.Servers is null || inventory.Applications is null || inventory.Instances is null)
            throw new InventoryValidationException(["servers, applications and instances must be lists."]);
        if (inventory.Servers.Count > 500 || inventory.Applications.Count > 500 || inventory.Instances.Count > 5000)
            errors.Add("Inventory exceeds the supported size (500 servers, 500 applications, 5000 instances).");
        void Identifiers(IEnumerable<string?> ids, string kind)
        {
            var values = ids.ToArray();
            if (values.Any(id => !Id(id))) errors.Add($"{kind} IDs must match [a-z0-9][a-z0-9_-] and be at most 64 characters.");
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) errors.Add($"Duplicate {kind} ID.");
        }
        Identifiers(inventory.Servers.Select(s => s?.Id), "server");
        Identifiers(inventory.Applications.Select(a => a?.Id), "application");
        Identifiers(inventory.Instances.Select(i => i?.Id), "instance");
        foreach (var server in inventory.Servers)
        {
            if (server is null) { errors.Add("Server entries cannot be null."); continue; }
            if (string.IsNullOrWhiteSpace(server.Name) || server.Name.Length > 120) errors.Add($"{server.Id}: server name is required (max 120 characters).");
            if (!Host(server.Host)) errors.Add($"{server.Id}: host must be a DNS name or IPv4 address, without a user or port.");
            if (!Id(server.SshProfile)) errors.Add($"{server.Id}: SSH profile must be a lowercase identifier.");
            if (server.Port is < 1 or > 65535) errors.Add($"{server.Id}: port must be between 1 and 65535.");
        }
        foreach (var application in inventory.Applications)
        {
            if (application is null) { errors.Add("Application entries cannot be null."); continue; }
            if (string.IsNullOrWhiteSpace(application.Name) || application.Name.Length > 120) errors.Add($"{application.Id}: application name is required (max 120 characters).");
            if (application.TeamCityBuildType?.Length > 200 || application.ArtifactPath?.Length > 500) errors.Add($"{application.Id}: TeamCity metadata is too long.");
        }
        var units = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in inventory.Instances)
        {
            if (instance is null) { errors.Add("Instance entries cannot be null."); continue; }
            if (!inventory.Servers.Any(s => s?.Id == instance.ServerId)) errors.Add($"{instance.Id}: server does not exist.");
            if (!inventory.Applications.Any(a => a?.Id == instance.ApplicationId)) errors.Add($"{instance.Id}: application does not exist.");
            if (instance.ServiceName is null || !Regex.IsMatch(instance.ServiceName, "^[a-zA-Z0-9][a-zA-Z0-9_@.:-]{0,180}\\.service$")) errors.Add($"{instance.Id}: specify a systemd .service unit name.");
            if (instance.InstallRoot is null || instance.InstallRoot.Length > 500 || !Regex.IsMatch(instance.InstallRoot, "^/[a-zA-Z0-9_./@-]+$") || instance.InstallRoot.Split('/').Any(p => p is "." or "..")) errors.Add($"{instance.Id}: installRoot must be an absolute Linux path without traversal or spaces.");
            if (!LocalUrl(instance.VersionUrl) || !LocalUrl(instance.HealthUrl)) errors.Add($"{instance.Id}: version and health URLs must use HTTP on localhost (queried through SSH).");
            if (!units.Add($"{instance.ServerId}:{instance.ServiceName}")) errors.Add($"{instance.Id}: service is already mapped on that server.");
            if (!paths.Add($"{instance.ServerId}:{instance.InstallRoot?.TrimEnd('/')}")) errors.Add($"{instance.Id}: install root is already mapped on that server.");
        }
        // A host alias must not allow the same real service to be mapped twice.
        if (inventory.Servers.Where(s => s is not null).GroupBy(s => $"{s.Host}:{s.Port}", StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) errors.Add("A host and SSH port may only have one server record.");
        if (errors.Count > 0) throw new InventoryValidationException(errors.ToArray());
    }
}

public sealed class InventoryStore
{
    public const int MaxBytes = 512 * 1024;
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IDeserializer reader = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).WithDuplicateKeyChecking().Build();
    private readonly ISerializer writer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).DisableAliases().Build();
    public InventoryStore(string path) => this.path = Path.GetFullPath(path);
    public InventoryDocument Parse(string? yaml)
    {
        if (yaml is null || Encoding.UTF8.GetByteCount(yaml) > MaxBytes) throw new InventoryValidationException(["YAML is required and must be under 512 KiB."]);
        try { var inventory = reader.Deserialize<InventoryDocument>(yaml); InventoryValidation.Validate(inventory); return inventory; }
        catch (YamlException exception) { throw new InventoryValidationException([$"Invalid YAML: {exception.Message}"]); }
    }
    public async Task<InventorySnapshot> Read(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new("empty", new(), writer.Serialize(new InventoryDocument()));
        if (new FileInfo(path).Length > MaxBytes) throw new InventoryValidationException(["Inventory file exceeds 512 KiB."]);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var yaml = Encoding.UTF8.GetString(bytes);
        return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), Parse(yaml), yaml);
    }
    public async Task<InventorySnapshot> Save(string baseRevision, InventoryDocument? inventory, CancellationToken cancellationToken = default)
    {
        InventoryValidation.Validate(inventory);
        var yaml = writer.Serialize(inventory);
        if (Encoding.UTF8.GetByteCount(yaml) > MaxBytes) throw new InventoryValidationException(["Inventory exceeds 512 KiB."]);
        await gate.WaitAsync(cancellationToken);
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // File-level lock also protects optimistic concurrency across local API processes.
            using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = await Read(cancellationToken);
            if (baseRevision != current.Revision) throw new InventoryConflictException();
            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, yaml, new UTF8Encoding(false), cancellationToken);
            File.Move(temp, path, true);
            return await Read(cancellationToken);
        }
        finally { if (temp is not null && File.Exists(temp)) File.Delete(temp); gate.Release(); }
    }
}
