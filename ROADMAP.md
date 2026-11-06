# QuickDeploy roadmap

Here’s what’s coming up next on the application roadmap.

QuickDeploy helps development teams deploy application artifacts across multiple servers, manage application inventories, and discover running versions and service health. It is designed to speed up demo releases and sanity testing.

**Last reviewed:** 6 November 2026  
**North star:** a full demo deployment in under one hour, from artifact import through successful service health checks.  
**Current platform:** React/TypeScript client, ASP.NET Core backend, Gradle build, Linux/systemd targets.

This is a sequenced product roadmap, not a release-date commitment. Priorities will be refined after real demo-environment feedback.

## At a glance

| Phase | Focus | Status |
| --- | --- | --- |
| 01 | [Inventory & visibility](#foundation) | Delivered |
| 02 | [Validate against the real estate](#pilot) | Next |
| 03 | [Verified TeamCity artifacts](#artifacts) | In progress |
| 04 | [Install, replicate & restart](#deployment) | Planned |
| 05 | [Operate & collaborate](#operations) | Later |
| 06 | [Container deployment support](#containers) | Future |

## Roadmap

<a id="foundation"></a>

### 01 · Inventory & visibility

**Status: Delivered.** A dependable map of applications, instances and Linux hosts.

- Create and edit servers, applications and instance mappings in the UI; import and export YAML.
- Validate inventory and save atomically with revision conflict protection.
- Inspect systemd state, installed and running versions, and health through read-only SSH checks.
- Show stale observations, version mismatches and missing SSH configuration explicitly.

**Current outcome:** Inventory and discovery are implemented and covered by automated checks. Deployment actions in Overview remain simulations.

**Dependencies / scope:** Available now. Real server connectivity and endpoint contracts still need a pilot.

<a id="pilot"></a>

### 02 · Validate against the real estate

**Status: Next.** Prove the read-only workflow with representative demo services.

- Configure reader SSH profiles, allowed hosts and verified host keys for pilot servers.
- Confirm systemd units, installation paths, application/instance identities and version endpoints.
- Exercise unreachable hosts, unhealthy services, permission failures and version mismatches.
- Document the service version contract and agree rollout, health-check and restart conventions.

**Success criteria:** a pilot application shows trustworthy installed/running versions and health across its real demo instances.

**Dependencies / scope:** Requires demo host access and a representative service with version and health endpoints.

<a id="artifacts"></a>

### 03 · Verified TeamCity artifacts

**Status: In progress.** Turn a build link into a reproducible deployment input.

- Connect to an approved TeamCity base URL using backend-managed credentials.
- Resolve the exact application, build ID and artifact path from a submitted build link.
- Download packages into backend artifact storage; record version, commit, checksum and size.
- Show real import progress and failures, verify expected checksums where available, and reject incomplete downloads.

**Current outcome:** Import is implemented in the backend and the **Artifacts** view, and covered by automated checks against a fake TeamCity server. Only links that pin one finished, successful build are accepted; the application is matched by its TeamCity build type; downloads are size- and SHA-256-checked (against a published `<artifact>.sha256` file when present) before they enter the library. It still needs validation against the real TeamCity server.

**Success criteria:** a selected immutable build can be imported, verified and retrieved from the artifact library with traceable build metadata.

**Dependencies / scope:** Requires a TeamCity access token, agreement on artifact conventions (version = TeamCity build number; optional `.sha256` sidecar) and a retention policy. No automatic retention or deletion is implemented yet.

<a id="deployment"></a>

### 04 · Install, replicate & restart

**Status: Planned.** Make the full deployment workflow real, observable and recoverable.

- Add durable deployment jobs with an inventory snapshot, target selection and per-instance locks.
- Install a versioned release on an initial target, then distribute the same verified package to the remaining targets while preserving instance configuration.
- Activate release directories and orchestrate systemd restarts with bounded parallelism, timeouts and post-restart health checks.
- Stream per-server and per-instance progress; support retrying failed steps, controlled cancellation and rollback to a known release.

**Success criteria:** a representative full demo deployment finishes within the one-hour target, including health verification, and failure recovery is demonstrated.

**Dependencies / scope:** Requires pilot acceptance, verified artifacts, deployment permissions and agreed rollback semantics.

<a id="operations"></a>

### 05 · Operate & collaborate

**Status: Later.** Prepare QuickDeploy for a shared, supported team service.

- Provision Linux hosting, TLS/reverse proxy configuration, managed secrets and persistent data outside application releases.
- Add monitoring, actionable errors, backup/restore procedures and recovery checks for interrupted jobs.
- Introduce authentication, roles and an audit trail for inventory changes and deployment actions.
- Add scheduled discovery and drift notifications; evaluate PostgreSQL for shared job state and multi-backend coordination while retaining YAML inventory ownership.

**Success criteria:** agreed access controls, recovery procedures and operational monitoring are verified before broader shared use.

**Dependencies / scope:** Authentication and audit trails remain deferred from the initial local/trusted demo version; shared use needs an agreed access boundary.

<a id="containers"></a>

### 06 · Container deployment support

**Status: Future.** Extend the deployment model when the applications are ready.

- Package suitable services as containers and define image/version identity conventions.
- Add registry integration and a container deployment adapter alongside systemd support.
- Reuse inventory relationships, health checks, progress reporting and rollback concepts.
- Evaluate the required container runtime or orchestration platform after a service pilot.

**Success criteria:** a containerised pilot service can be deployed and recovered through the same application/instance workflow.

**Dependencies / scope:** Requires a separate containerisation decision and pilot. Current demo services continue to use Linux/systemd.

## Delivery principles

- Prove read-only visibility before enabling service-changing operations.
- Keep installed versions, running versions and historical observations distinct.
- Preserve instance-specific configuration and keep credentials outside inventory YAML.
- Build for partial failures: bounded execution, explicit outcomes and practical recovery.
- Measure the complete demo deployment, including health verification, against the one-hour target.

## Current limits

Inventory editing, read-only discovery and verified TeamCity artifact import are implemented. Real discovery requires configured SSH profiles, host keys and service endpoints; real imports require a configured TeamCity base URL and token. The Overview deployment preview still uses sample data; installation, replication and restarts are future work. No production-readiness or live-server validation is implied by the delivered foundation.

[Project setup](README.md) · [Visual roadmap](ROADMAP.htm)
