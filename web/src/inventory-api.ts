export type InventoryServer = {id: string; name: string; host: string; sshProfile: string; port: number};
export type InventoryApplication = {id: string; name: string; teamCityBuildType: string; artifactPath: string};
export type InventoryInstance = {id: string; applicationId: string; serverId: string; serviceName: string; installRoot: string; versionUrl: string; healthUrl: string};
export type Inventory = {schemaVersion: number; environment: string; servers: InventoryServer[]; applications: InventoryApplication[]; instances: InventoryInstance[]};
export type Snapshot = {revision: string; inventory: Inventory; yaml: string};
export type Observation = {instanceId: string; inventoryRevision: string; checkedAt: string; outcome: string; serviceState: string; health: string; installedVersion: string | null; runningVersion: string | null; buildId: string | null; commit: string | null; versionSource: string; detail: string | null; lastSuccessfulAt: string | null; lastKnownRunningVersion: string | null};
export class ApiError extends Error {constructor(public status: number, message: string) {super(message);}}
export async function api<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
  let response: Response;
  try {response = await fetch(`/api/${path}`, {method, headers: body === undefined ? undefined : {'Content-Type':'application/json'}, body: body === undefined ? undefined : JSON.stringify(body)});}
  catch {throw new Error('Cannot reach the API. Start the backend on port 5080, then reload.');}
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new ApiError(response.status, Array.isArray(problem.errors) ? problem.errors.join('\n') : problem.detail || problem.title || 'API request failed. Check that the backend is running on port 5080.');
  }
  return response.json();
}
