# QuickDeploy

First development increment of a Linux service deployment dashboard. React + TypeScript UI; ASP.NET Core API foundation. No login or audit trail.

## Run the dashboard

Requires Node.js 22+ and npm.

```powershell
cd web
npm ci
npm run dev
```

Open the local URL printed by Vite. `npm run build` type-checks and creates `web/dist`.

### Available now

- Responsive dashboard matching the approved visual direction.
- Sample inventory: 16 applications, 8 Linux hosts, 48 systemd instances.
- Explore instances by server or application.
- Searchable artifact library and TeamCity link/metadata form.
- Sequential Install → Replicate → Restart workflow, with independent progress per target.
- Artifact state persisted in browser local storage; interrupted simulations can resume by clicking the unfinished stage after refreshing.
- Deployment details showing systemd unit, path, source, and stage progress.

**This is simulation mode.** Link imports save metadata only; they do not fetch or verify packages. All progress and running-state examples are simulated. There is no SSH connection or service control. No real deployment has been performed. Browser data is local to this origin, not shared inventory.

## API foundation

Requires .NET 10 SDK, which was not available in the initial development environment.

```powershell
dotnet run --project server/QuickDeploy.Api
```

The API listens on `http://127.0.0.1:5080` and serves `/api/health`, `/api/inventory`, `/api/servers/{id}/instances`, and `/api/applications/{id}/instances`. Deployment requests explicitly return HTTP 501 until adapters are configured. The UI currently uses its own sample inventory and is not connected to this API.

## Next integration increment

1. Confirm systemd service conventions, installation package format, paths, instance-specific configuration, health checks, and restart concurrency.
2. Add PostgreSQL inventory, versioned configuration imports, artifact records and durable deployment jobs. The application/instance/server relationship remains separate from observed service health.
3. Configure a fixed TeamCity base URL and server-side token; download immutable builds to artifact storage and verify checksums. Do not fetch arbitrary user-supplied URLs without host validation.
4. Add SSH execution using host-key verification, a restricted deployment account, release directories, bounded parallelism, timeouts, per-instance locks and recovery for failed operations.
5. Deploy the same artifact independently to targets while preserving instance configuration. Atomically activate a release, restart via systemd and verify health before reporting success.
6. Stream job progress through SignalR. Add cancellation, retries and rollback behavior before live use.

Login and audit trails are deferred. Keep this initial unauthenticated prototype on loopback. Live shared-network deployment needs a separately agreed access boundary.
