using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace QuickDeploy;

public sealed class TeamCityOptions
{
    public string BaseUrl { get; set; } = "";
    // Backend-owned access token (TeamCity "Authorization: Bearer"); provide it via TeamCity__Token, never inventory YAML.
    public string Token { get; set; } = "";
    public bool RequireChecksum { get; set; }
    public long MaxArtifactBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int DownloadTimeoutMinutes { get; set; } = 30;
}
public sealed record TeamCityBuildLink(long BuildId, string? BuildTypeId, string? ArtifactPath);
public sealed record ArtifactRecord(string ApplicationId, string BuildId, string BuildNumber, string Version, string? Commit, string? Branch,
    string BuildTypeId, string ArtifactPath, string FileName, long Size, string Sha256, bool ChecksumVerified, string ChecksumSource,
    DateTimeOffset ImportedAt, string BuildUrl);
public sealed record ArtifactEntry(ArtifactRecord Artifact, string Status);
public sealed record ImportArtifact(string? Link);
public sealed record TeamCityStatus(bool Configured, string? BaseUrl, bool RequireChecksum, string? Detail);
public sealed record ImportJobStatus(string Id, string State, string Link, string? ApplicationId, string? BuildId, string? BuildNumber,
    long BytesReceived, long? TotalBytes, string? Error, ArtifactRecord? Artifact, bool AlreadyImported, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);
public sealed class ArtifactImportException(int status, string title, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
}

public static class TeamCityLinks
{
    private const string SafeSegment = "[A-Za-z0-9._@+-]+";
    public static bool SafeArtifactPath(string? path) => path is not null && path.Length is > 0 and <= 500 &&
        Regex.IsMatch(path, $"^{SafeSegment}(/{SafeSegment})*$", RegexOptions.CultureInvariant) && !path.Split('/').Any(s => s is "." or "..");
    public static bool SafeBuildType(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9_]{1,200}$", RegexOptions.CultureInvariant);

    public static Uri Base(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || !Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new ArtifactImportException(503, "TeamCity is not configured",
                "Set TeamCity:BaseUrl to the approved HTTPS TeamCity address (no credentials, query or fragment) in backend configuration.");
        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
    }

    // Only links that pin one build ID are accepted, so an import is reproducible ("last successful" links are not).
    public static TeamCityBuildLink Parse(Uri baseUri, string? link)
    {
        static ArtifactImportException Bad(string detail) => new(400, "Unsupported TeamCity link", detail);
        if (string.IsNullOrWhiteSpace(link) || link.Length > 2048 || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw Bad("Paste the full TeamCity build link, for example https://teamcity.example.com/buildConfiguration/Pricing_Release/1842.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw Bad("Remove credentials from the link. The backend uses its own TeamCity token.");
        // Uri silently collapses dot segments, so reject them before they can redirect the artifact path.
        if (link.Contains("..", StringComparison.Ordinal) || link.Contains("%2e", StringComparison.OrdinalIgnoreCase) || link.Contains('\\'))
            throw Bad("The link must not contain relative path segments.");
        if (uri.Scheme != baseUri.Scheme || !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != baseUri.Port ||
            !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath + "/" != baseUri.AbsolutePath)
            throw Bad($"The link must point to the approved TeamCity server {baseUri}.");
        var relative = uri.AbsolutePath.Length > baseUri.AbsolutePath.Length ? uri.AbsolutePath[baseUri.AbsolutePath.Length..] : "";
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var query = QueryHelpers.ParseQuery(uri.Query);
        long Id(string? value) => long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? id : throw Bad("The link does not contain a numeric TeamCity build ID.");
        string? Path(IEnumerable<string> parts)
        {
            var path = string.Join('/', parts);
            if (path.Length == 0) return null;
            return SafeArtifactPath(path) ? path : throw Bad("The artifact path in the link is not supported (use letters, digits, . _ - @ + and /; no archive-internal paths).");
        }
        string? BuildType(string? value) => value is null || SafeBuildType(value) ? value : throw Bad("The build configuration ID in the link is not valid.");
        bool Is(int index, string value) => segments.Length > index && string.Equals(segments[index], value, StringComparison.OrdinalIgnoreCase);

        if (Is(0, "buildConfiguration") && segments.Length >= 3) return new(Id(segments[2]), BuildType(segments[1]), null);
        if (Is(0, "build") && segments.Length >= 2) return new(Id(segments[1]), null, null);
        if (Is(0, "viewLog.html") && query.TryGetValue("buildId", out var buildId))
            return new(Id(buildId.ToString()), BuildType(query.TryGetValue("buildTypeId", out var type) ? type.ToString() : null), null);
        if (Is(0, "repository") && Is(1, "download") && segments.Length >= 5 && segments[3].EndsWith(":id", StringComparison.Ordinal))
            return new(Id(segments[3][..^3]), BuildType(segments[2]), Path(segments.Skip(4)));
        if (Is(0, "app") && Is(1, "rest") && Is(2, "builds") && segments.Length >= 4 && segments[3].StartsWith("id:", StringComparison.Ordinal))
            return new(Id(segments[3][3..]), null, segments.Length >= 7 && Is(4, "artifacts") && (Is(5, "files") || Is(5, "content")) ? Path(segments.Skip(6)) : null);
        throw Bad("Use a link to one specific build: a build results page (…/buildConfiguration/<type>/<build ID>) or an artifact download link (…/repository/download/<type>/<build ID>:id/<path>). Links such as 'lastSuccessful' are not immutable.");
    }
}

public sealed class ArtifactService
{
    private const string MetadataFile = "artifact.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly InventoryStore inventory;
    private readonly TeamCityOptions options;
    private readonly string root;
    private readonly HttpClient http;
    private readonly SemaphoreSlim storage = new(1, 1);
    private readonly SemaphoreSlim slots = new(2, 2);
    private readonly Dictionary<string, ImportJob> jobs = [];

    public ArtifactService(InventoryStore inventory, TeamCityOptions options, string root, HttpMessageHandler? handler = null)
    {
        this.inventory = inventory;
        this.options = options;
        this.root = Path.GetFullPath(root);
        // Redirects are never followed: the token must only ever be sent to the approved TeamCity address.
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = DecompressionMethods.None })
        { Timeout = Timeout.InfiniteTimeSpan };
        // Interrupted imports never become library entries; discard their leftovers.
        try { if (Directory.Exists(Incoming)) Directory.Delete(Incoming, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private string Incoming => Path.Combine(root, ".incoming");

    public TeamCityStatus Status()
    {
        try { var baseUri = TeamCityLinks.Base(options.BaseUrl); RequireToken(); return new(true, baseUri.ToString(), options.RequireChecksum, null); }
        catch (ArtifactImportException exception) { return new(false, null, options.RequireChecksum, exception.Message); }
    }
    private void RequireToken()
    {
        if (string.IsNullOrWhiteSpace(options.Token) || options.Token.Any(char.IsWhiteSpace))
            throw new ArtifactImportException(503, "TeamCity is not configured", "Provide a TeamCity access token to the backend (for example the TeamCity__Token environment variable).");
    }

    public ImportJobStatus Start(string? link, CancellationToken stopping)
    {
        var baseUri = TeamCityLinks.Base(options.BaseUrl);
        RequireToken();
        var parsed = TeamCityLinks.Parse(baseUri, link);
        ImportJob job;
        lock (jobs)
        {
            var running = jobs.Values.FirstOrDefault(j => j.Active && j.Link.BuildId == parsed.BuildId);
            if (running is not null) return running.Snapshot();
            foreach (var old in jobs.Values.Where(j => !j.Active).OrderBy(j => j.StartedAt).SkipLast(49).ToArray()) jobs.Remove(old.Id);
            job = new ImportJob(link!.Trim(), parsed);
            jobs[job.Id] = job;
        }
        _ = Task.Run(() => Run(job, stopping), CancellationToken.None);
        return job.Snapshot();
    }
    public ImportJobStatus? Job(string id) { lock (jobs) return jobs.TryGetValue(id, out var job) ? job.Snapshot() : null; }
    public List<ImportJobStatus> Jobs() { lock (jobs) return jobs.Values.OrderByDescending(j => j.StartedAt).Select(j => j.Snapshot()).ToList(); }

    private async Task Run(ImportJob job, CancellationToken stopping)
    {
        try
        {
            await slots.WaitAsync(stopping);
            try { var (record, existing) = await Import(job.Link, job, stopping); job.Complete(record, existing); }
            finally { slots.Release(); }
        }
        catch (ArtifactImportException exception) { job.Fail(exception.Message); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { job.Fail("Import cancelled because the backend is stopping. Nothing was added to the library."); }
        catch (OperationCanceledException) { job.Fail("TeamCity did not respond in time. Nothing was added to the library."); }
        catch (HttpRequestException exception) { job.Fail($"Cannot reach TeamCity ({exception.HttpRequestError}). Check the base URL, network access and TLS trust."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { job.Fail("Artifact storage failed. Check free space and write permission for the artifact directory. Nothing was added to the library."); }
        catch (Exception exception) { job.Fail($"Unexpected import failure ({exception.GetType().Name}). Nothing was added to the library."); }
    }

    public async Task<(ArtifactRecord Record, bool Existing)> Import(TeamCityBuildLink link, ImportJob? job, CancellationToken cancellationToken)
    {
        static ArtifactImportException Reject(string detail) => new(422, "Artifact rejected", detail);
        var baseUri = TeamCityLinks.Base(options.BaseUrl);
        RequireToken();
        job?.Update("resolving");
        var build = await GetJson(baseUri, $"app/rest/builds/id:{link.BuildId}?fields=id,number,buildTypeId,state,status,branchName,webUrl,revisions(revision(version))", cancellationToken)
            ?? throw Reject($"TeamCity build {link.BuildId} was not found, or the backend token cannot see it.");
        string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (!build.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt64() != link.BuildId) throw Reject("TeamCity returned a different build than requested.");
        var buildType = Text(build, "buildTypeId");
        if (!TeamCityLinks.SafeBuildType(buildType)) throw Reject("TeamCity did not return a valid build configuration ID.");
        if (link.BuildTypeId is not null && !string.Equals(link.BuildTypeId, buildType, StringComparison.OrdinalIgnoreCase))
            throw Reject($"Build {link.BuildId} belongs to '{buildType}', not '{link.BuildTypeId}' as the link states.");
        if (Text(build, "state") != "finished") throw Reject($"Build {link.BuildId} has not finished. Import it once TeamCity completes the build.");
        if (Text(build, "status") != "SUCCESS") throw Reject($"Build {link.BuildId} did not succeed (status {Text(build, "status") ?? "unknown"}). Only successful builds can be imported.");
        var number = Text(build, "number");
        if (number is null || !Regex.IsMatch(number, "^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$", RegexOptions.CultureInvariant))
            throw Reject($"Build number '{number}' cannot be used as a release version (letters, digits, . _ + - only, max 64).");
        string? commit = null;
        if (build.TryGetProperty("revisions", out var revisions) && revisions.TryGetProperty("revision", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
        {
            commit = Text(list[0], "version");
            if (commit is not null && !Regex.IsMatch(commit, "^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)) commit = null;
        }
        var branch = Text(build, "branchName") is { Length: <= 200 } name ? name : null;
        var webUrl = Text(build, "webUrl");
        var buildUrl = webUrl is not null && webUrl.StartsWith(baseUri.ToString(), StringComparison.OrdinalIgnoreCase) && webUrl.Length <= 2048
            ? webUrl : new Uri(baseUri, $"buildConfiguration/{buildType}/{link.BuildId}").ToString();

        var applications = (await inventory.Read(cancellationToken)).Inventory.Applications
            .Where(a => string.Equals(a.TeamCityBuildType?.Trim(), buildType, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (applications.Length == 0) throw Reject($"No saved inventory application uses TeamCity build type '{buildType}'. Set it on the application in Inventory first.");
        if (applications.Length > 1) throw Reject($"Several applications use TeamCity build type '{buildType}' ({string.Join(", ", applications.Select(a => a.Id))}). Each build type must identify one application.");
        var application = applications[0];
        job?.Identify(application.Id, link.BuildId.ToString(), number);
        var configuredPath = string.IsNullOrWhiteSpace(application.ArtifactPath) ? null : application.ArtifactPath.Trim();
        if (configuredPath is not null && link.ArtifactPath is not null && configuredPath != link.ArtifactPath)
            throw Reject($"The link points to '{link.ArtifactPath}', but '{application.Id}' is configured to deploy '{configuredPath}'.");
        var artifactPath = configuredPath ?? link.ArtifactPath ?? throw Reject($"Set the artifact path on application '{application.Id}', or paste an artifact download link.");
        if (!TeamCityLinks.SafeArtifactPath(artifactPath)) throw Reject($"Artifact path '{artifactPath}' is not supported (relative path; letters, digits, . _ - @ + and /).");

        var existing = await Find(application.Id, link.BuildId.ToString(), cancellationToken);
        if (existing is { Status: "available" }) return (existing.Artifact, true);

        var escaped = string.Join('/', artifactPath.Split('/').Select(Uri.EscapeDataString));
        var metadata = await GetJson(baseUri, $"app/rest/builds/id:{link.BuildId}/artifacts/metadata/{escaped}", cancellationToken)
            ?? throw Reject($"Build {link.BuildId} has no artifact '{artifactPath}'.");
        if (!metadata.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out var size) || size < 0)
            throw Reject($"'{artifactPath}' is not a file artifact in build {link.BuildId}.");
        if (size > options.MaxArtifactBytes) throw Reject($"'{artifactPath}' is {size} bytes, above the configured limit of {options.MaxArtifactBytes} bytes.");

        var expected = await Checksum(baseUri, $"app/rest/builds/id:{link.BuildId}/artifacts/files/{escaped}.sha256", cancellationToken);
        if (expected is null && options.RequireChecksum) throw Reject($"Build {link.BuildId} does not publish '{artifactPath}.sha256', and checksums are required by backend configuration.");

        var fileName = artifactPath.Split('/')[^1];
        var staging = Path.Combine(Incoming, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            job?.Downloading(size);
            var sha256 = await Download(baseUri, $"app/rest/builds/id:{link.BuildId}/artifacts/files/{escaped}", Path.Combine(staging, fileName), size, job, cancellationToken);
            job?.Update("verifying");
            if (expected is not null && !string.Equals(expected, sha256, StringComparison.OrdinalIgnoreCase))
                throw Reject($"Checksum mismatch for '{artifactPath}': TeamCity published {expected}, download produced {sha256}. Nothing was added to the library.");
            var record = new ArtifactRecord(application.Id, link.BuildId.ToString(), number, number, commit, branch, buildType!, artifactPath, fileName, size, sha256,
                expected is not null, expected is not null ? "teamcity-sha256-file" : "computed-on-import", DateTimeOffset.UtcNow, buildUrl);
            await File.WriteAllTextAsync(Path.Combine(staging, MetadataFile), JsonSerializer.Serialize(record, Json), new UTF8Encoding(false), cancellationToken);
            await storage.WaitAsync(cancellationToken);
            try
            {
                var target = Path.Combine(root, application.Id, record.BuildId);
                if (Directory.Exists(target))
                {
                    var current = await Find(application.Id, record.BuildId, cancellationToken);
                    if (current is { Status: "available" }) return (current.Artifact, true);
                    Directory.Delete(target, true); // A damaged copy is replaced by the freshly verified download.
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(staging, target);
                return (record, false);
            }
            finally { storage.Release(); }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public async Task<List<ArtifactEntry>> List(CancellationToken cancellationToken = default)
    {
        List<ArtifactEntry> entries = [];
        if (!Directory.Exists(root)) return entries;
        foreach (var applicationDirectory in Directory.EnumerateDirectories(root).Where(d => InventoryValidation.Id(Path.GetFileName(d))))
            foreach (var buildDirectory in Directory.EnumerateDirectories(applicationDirectory))
                if (await Find(Path.GetFileName(applicationDirectory), Path.GetFileName(buildDirectory), cancellationToken) is { } entry) entries.Add(entry);
        return entries.OrderByDescending(e => e.Artifact.ImportedAt).ToList();
    }
    public async Task<ArtifactEntry?> Find(string applicationId, string buildId, CancellationToken cancellationToken = default)
    {
        if (!InventoryValidation.Id(applicationId) || !Regex.IsMatch(buildId, "^[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant)) return null;
        var metadataPath = Path.Combine(root, applicationId, buildId, MetadataFile);
        if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length > 64 * 1024) return null;
        ArtifactRecord? record;
        try { record = JsonSerializer.Deserialize<ArtifactRecord>(await File.ReadAllTextAsync(metadataPath, cancellationToken), Json); }
        catch (JsonException) { return null; }
        if (record is null || record.ApplicationId != applicationId || record.BuildId != buildId || !TeamCityLinks.SafeArtifactPath(record.FileName) || record.FileName.Contains('/')) return null;
        var file = new FileInfo(FilePath(record));
        return new(record, file.Exists && file.Length == record.Size ? "available" : "damaged");
    }
    public string FilePath(ArtifactRecord record) => Path.Combine(root, record.ApplicationId, record.BuildId, record.FileName);

    private HttpRequestMessage Request(Uri baseUri, string relative)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token.Trim());
        return request;
    }
    private static void Ensure(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code is >= 300 and < 400) throw new ArtifactImportException(502, "TeamCity redirected the request",
            "TeamCity answered with a redirect, which is not followed so the token stays on the approved server. Check TeamCity:BaseUrl (scheme, host and context path).");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new ArtifactImportException(502, "TeamCity rejected the credentials",
            "TeamCity refused the backend token. Check that it is valid and can view this project's builds and artifacts.");
        if (!response.IsSuccessStatusCode) throw new ArtifactImportException(502, "TeamCity request failed", $"TeamCity answered HTTP {code}.");
    }
    private async Task<JsonElement?> GetJson(Uri baseUri, string relative, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(baseUri, relative);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Ensure(response);
        var body = await ReadBounded(response, 1024 * 1024, timeout.Token);
        try { using var document = JsonDocument.Parse(body); return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : throw new JsonException(); }
        catch (JsonException) { throw new ArtifactImportException(502, "Unexpected TeamCity response", "TeamCity did not return a JSON object. Check that TeamCity:BaseUrl points at the TeamCity server root."); }
    }
    private async Task<string?> Checksum(Uri baseUri, string relative, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(baseUri, relative);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Ensure(response);
        var match = Regex.Match(Encoding.UTF8.GetString(await ReadBounded(response, 4096, timeout.Token)), "^\\s*([A-Fa-f0-9]{64})(\\s|$)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() :
            throw new ArtifactImportException(422, "Artifact rejected", "The published .sha256 file is malformed. Expected a 64-character hex SHA-256 digest (sha256sum format).");
    }
    private async Task<string> Download(Uri baseUri, string relative, string destination, long expectedSize, ImportJob? job, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(options.DownloadTimeoutMinutes, 1, 24 * 60)));
        using var request = Request(baseUri, relative);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new ArtifactImportException(422, "Artifact rejected", "The artifact disappeared from TeamCity during import (it may have been cleaned up).");
        Ensure(response);
        if (response.Content.Headers.ContentLength is { } length && length != expectedSize)
            throw new ArtifactImportException(422, "Artifact rejected", $"TeamCity reported {expectedSize} bytes but is sending {length}. Nothing was added to the library.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
        {
            var buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                received += count;
                if (received > expectedSize) throw new ArtifactImportException(422, "Artifact rejected", $"TeamCity sent more than the expected {expectedSize} bytes. Nothing was added to the library.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                job?.Received(received);
            }
            await output.FlushAsync(timeout.Token);
        }
        if (received != expectedSize)
            throw new ArtifactImportException(422, "Artifact rejected", $"Download incomplete: received {received} of {expectedSize} bytes. Nothing was added to the library.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static async Task<byte[]> ReadBounded(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit) throw new ArtifactImportException(502, "Unexpected TeamCity response", "TeamCity response exceeded the expected size.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            memory.Write(buffer, 0, count);
            if (memory.Length > limit) throw new ArtifactImportException(502, "Unexpected TeamCity response", "TeamCity response exceeded the expected size.");
        }
        return memory.ToArray();
    }
}

public sealed class ImportJob(string source, TeamCityBuildLink link)
{
    private readonly object sync = new();
    private string state = "queued";
    private string? applicationId, buildNumber, error;
    private long received;
    private long? total;
    private ArtifactRecord? artifact;
    private bool alreadyImported;
    private DateTimeOffset? finishedAt;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public TeamCityBuildLink Link { get; } = link;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public bool Active { get { lock (sync) return finishedAt is null; } }
    public void Update(string next) { lock (sync) state = next; }
    public void Identify(string application, string build, string number) { lock (sync) { applicationId = application; buildNumber = number; } }
    public void Downloading(long size) { lock (sync) { state = "downloading"; total = size; received = 0; } }
    public void Received(long bytes) { lock (sync) received = bytes; }
    public void Complete(ArtifactRecord record, bool existing) { lock (sync) { state = "completed"; artifact = record; alreadyImported = existing; received = record.Size; total = record.Size; finishedAt = DateTimeOffset.UtcNow; } }
    public void Fail(string message) { lock (sync) { state = "failed"; error = message; finishedAt = DateTimeOffset.UtcNow; } }
    public ImportJobStatus Snapshot()
    {
        lock (sync) return new(Id, state, source, applicationId, Link.BuildId.ToString(), buildNumber, received, total, error, artifact, alreadyImported, StartedAt, finishedAt);
    }
}
