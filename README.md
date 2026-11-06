# QuickDeploy

QuickDeploy helps development teams deploy application artifacts across multiple servers, manage application inventories, and discover running versions and service health. It is designed to speed up demo releases and sanity testing.

React + TypeScript client, ASP.NET Core backend, and Gradle build orchestration. Login and audit trails are deferred.

## Roadmap

See the [development roadmap](ROADMAP.md) for delivered capabilities, upcoming milestones and future plans. Open [the visual HTML roadmap](ROADMAP.htm) for a standalone page with milestone filters and print styling.

## Run locally

Prerequisites: JDK 17+ (21 recommended), Node.js 22.18+ with npm, .NET 10 SDK, and an OpenSSH client on the API host for discovery. Put the toolchains on PATH and set JAVA_HOME.

From the repository root, in two terminals:

```powershell
.\gradlew.bat serverRun
.\gradlew.bat webDev
```

Open http://127.0.0.1:5174/ and select **Inventory**. Vite proxies `/api` to the API on loopback port 5080. Both processes must run. Missing API connections show an explicit error, not a sample inventory.

A project-local .NET SDK was installed under the ignored `.tools/dotnet` directory during development. If using it, add its absolute directory to PATH for the terminal running Gradle. Otherwise install .NET 10 normally.

## Inventory

- Create/edit/remove servers, applications and instances in the UI. Changes remain a draft until **Save inventory**.
- Stable IDs identify records. Instances reference a server, application, systemd service name and Linux installation root.
- The API validates identifiers, references, duplicate hosts/service mappings/paths and endpoint URLs. Referenced servers/applications cannot be removed until dependent instances are reassigned or removed.
- Saves use a revision check (HTTP 409 on a conflict), a local file lock and atomic replacement. Conflicts preserve the browser draft; reload explicitly to discard it.
- YAML import replaces the whole saved inventory after validation. YAML export exports saved data, not unsaved draft changes. Formatting/comments are normalized by saves.
- Start from `config/inventory.example.yaml` if preferred. Sample hosts are illustrative; no inventory is seeded automatically.

Storage defaults:

| Location | Purpose |
| --- | --- |
| `data/inventory/demo.yaml` at the repository root | Development inventory |
| `/var/lib/quickdeploy/inventory/demo.yaml` | Published Linux backend default |
| `<inventory-path>.discovery.json` | Latest observations and explicitly historical last-known versions |
| `<inventory-path>.lock` | Inventory save coordination |
| `data/artifacts` at the repository root | Development artifact library |
| `/var/lib/quickdeploy/artifacts` | Published Linux artifact library default (`Artifacts__Path` overrides) |

Set `Inventory__Path` to override the path. Keep it outside the app release directory and back up the data directory. The API service account needs write access to that directory. This file implementation supports one active discovery backend; it is not a distributed state store.

Real inventory, discovery state, credentials and the development SDK are ignored by Git. Never put private keys or passwords in inventory YAML.

## Read-only version discovery

In **Discovery**, use **Refresh all** or refresh a single instance. Four instances are checked concurrently, with a 25-second deadline per SSH process. This is on-demand discovery, not background polling; observations older than 90 seconds or tied to another inventory revision are marked stale and never presented as current facts.

The fixed remote script uses only:

- `systemctl show` for service state and PID before/after inspection.
- `head`/`base64` to read `<installRoot>/current/release.json` (installed version only).
- `curl` for local version and health endpoints (HTTP on loopback only, with request deadlines, body limits, no proxy or redirects).

There are no installation, restart, sudo or remote file-write commands. Remote hosts need systemd, POSIX sh, curl, head, base64 and tr. The reader account needs permission to inspect the units and read release metadata.

### SSH configuration

Copy `config/backend.example.json` to `server/QuickDeploy.Api/appsettings.Local.json` for source development and edit the paths, allowed hosts and user. Remove/change its Inventory.Path if you want the development default. For a published backend, provision `appsettings.Local.json` alongside its configuration, or use .NET environment variables such as `Discovery__Profiles__demo-reader__User`.

Profiles contain backend-owned identity/known-hosts paths and an explicit AllowedHosts list. Inventory edits can reference profiles but cannot supply credentials or SSH commands. SSH uses BatchMode, pinned host keys, no agent forwarding and no interactive password prompts. Provision verified host keys before scanning. Unknown profiles, disallowed hosts and missing key files show **Unconfigured** without contacting the server. Local configuration is excluded from release output.

### Version contract

The version endpoint must report build identity from the running process, plus application and instance identity:

```json
{
  "application": "pricing",
  "instanceId": "pricing-01",
  "version": "2.18.4",
  "buildId": "1842",
  "commit": "a8e27bd"
}
```

Both identities must match inventory, the service must be active, and its PID must remain stable. Build ID and commit are optional strings. A missing, malformed or mismatched endpoint yields **Unknown** running version. If a process restarts during inspection, refresh again. The version endpoint contract is trusted; the probe does not independently map its listening socket to a PID.

`current/release.json` uses application/version/build metadata, but is only evidence of the installed release. An installed/running mismatch is highlighted for investigation. Health endpoints are considered healthy for HTTP 2xx; application-specific response-body checks are not yet supported. A failed scan preserves last-known version/time separately and never reports that historical value as current.

## Verified TeamCity artifacts

Open **Artifacts**, paste a TeamCity build link and select **Import build**. The backend downloads the package into its artifact library and the page shows live progress, failures and the verified result.

Accepted links pin one build ID on the approved server: a build results page (`…/buildConfiguration/<buildType>/<buildId>`, `…/build/<buildId>`, `…/viewLog.html?buildId=<buildId>`) or an artifact link (`…/repository/download/<buildType>/<buildId>:id/<path>`, `…/app/rest/builds/id:<buildId>/artifacts/files/<path>`). Links to other hosts, credentials in the URL, relative path segments and moving targets such as `.lastSuccessful` are rejected.

The import then:

1. Reads the build from the TeamCity REST API. It must be finished and `SUCCESS`.
2. Finds the one saved inventory application whose **TeamCity build type** equals the build's configuration ID, and uses its **artifact path** (or the path in an artifact link, which must agree with it).
3. Reads the artifact size, downloads it into a staging folder while hashing it, and rejects any size mismatch, oversize file or interrupted download. If the build publishes `<artifact path>.sha256` (`sha256sum` format), the digest must match; otherwise the computed SHA-256 is recorded as unverified.
4. Moves the package and `artifact.json` into `<library>/<applicationId>/<buildId>/`. Imported builds are immutable: importing the same build again returns the stored copy.

Recorded metadata: application, TeamCity build ID and build type, version (the TeamCity build number), first VCS revision as the commit, branch, artifact path, file name, size, SHA-256, whether the checksum was verified, import time and the TeamCity build URL. The library marks a stored file whose size no longer matches as damaged; importing the build again replaces it.

Configure TeamCity in `appsettings.Local.json` (see `config/backend.example.json`) and supply the token through an environment variable rather than a file in the repository:

```powershell
$env:TeamCity__Token = '<TeamCity access token>'
.\gradlew.bat serverRun
```

`TeamCity:BaseUrl` must be HTTPS (plain HTTP is accepted only for a loopback TeamCity). The token is sent as `Authorization: Bearer` only to that address; redirects are never followed. The token needs view access to the build configurations and their artifacts. Optional settings: `RequireChecksum` (reject builds without a `.sha256` file), `MaxArtifactBytes` (default 2 GiB) and `DownloadTimeoutMinutes` (default 30). Two imports run at a time; import jobs are kept in memory, so a backend restart discards unfinished imports and their partial files. There is no retention or deletion policy yet.

## Build and checks

The checked-in wrapper pins Gradle; a global Gradle install is unnecessary.

```powershell
.\gradlew.bat webBuild webTest  # Frontend only
.\gradlew.bat serverTest        # Twelve backend checks with isolated temporary data, a fake SSH executable and a fake TeamCity
.\gradlew.bat test              # Frontend + backend checks
.\gradlew.bat build             # Frontend + API + all checks
.\gradlew.bat distZip           # build/distributions/quick-deploy.zip
.\gradlew.bat clean
```

Linux/macOS: use `sh ./gradlew` with the same tasks (or chmod +x gradlew). Windows: stop Vite before a dependency refresh; `npm ci` may need to replace a locked esbuild executable. The install task tracks npm's receipt instead of hashing the dependency tree.

The ZIP contains separate `web/`, `server/`, configuration examples and this README. It does not provision hosting, credentials, live inventory or a reverse proxy. In a deployed setup, serve the client and proxy `/api` to the loopback API under the same origin. API writes reject unexpected browser origins; authentication is still deferred. Keep this version local or behind your existing trusted access boundary.

The API includes inventory GET/PUT, YAML import/export, discovery GET/POST, health, instance lookup, TeamCity status (`GET /api/teamcity`), artifact import jobs (`POST`/`GET /api/artifacts/imports`, `GET /api/artifacts/imports/{id}`) and the artifact library (`GET /api/artifacts`, `GET /api/artifacts/{applicationId}/{buildId}` and `…/download`) under `/api`. Deployment POST returns HTTP 501.

## Deployment preview and next work

**Overview remains a separately labelled deployment simulation** using browser-local sample artifacts/topology. It does not deploy the saved real inventory. Its TeamCity link entry records sample metadata only; real, verified downloads are in **Artifacts**. Service-changing operations are still disabled.

Next: validate artifact import against the real TeamCity server and agree a retention policy, then durable deployment jobs, bounded deployment execution, health-gated restarts, retries/rollback, then authentication/audit trails and later container support. YAML remains the inventory source of truth; a database may be added for job state.
