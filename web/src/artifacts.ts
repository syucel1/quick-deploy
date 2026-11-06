export type ArtifactRecord = {applicationId: string; buildId: string; buildNumber: string; version: string; commit: string | null; branch: string | null; buildTypeId: string; artifactPath: string; fileName: string; size: number; sha256: string; checksumVerified: boolean; checksumSource: string; importedAt: string; buildUrl: string};
export type ArtifactEntry = {artifact: ArtifactRecord; status: 'available' | 'damaged'};
export type ImportJob = {id: string; state: 'queued' | 'resolving' | 'downloading' | 'verifying' | 'completed' | 'failed'; link: string; applicationId: string | null; buildId: string | null; buildNumber: string | null; bytesReceived: number; totalBytes: number | null; error: string | null; artifact: ArtifactRecord | null; alreadyImported: boolean; startedAt: string; finishedAt: string | null};
export type TeamCityStatus = {configured: boolean; baseUrl: string | null; requireChecksum: boolean; detail: string | null};

export const jobActive = (job: ImportJob) => job.finishedAt === null;
export function jobPercent(job: ImportJob): number {
  if (job.state === 'completed') return 100;
  if (!job.totalBytes) return 0;
  return Math.min(99, Math.floor(job.bytesReceived * 100 / job.totalBytes));
}
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes, unit = 0;
  while (value >= 1024 && unit < units.length - 1) {value /= 1024; unit++;}
  return `${unit === 0 ? value : value.toFixed(value >= 10 ? 0 : 1)} ${units[unit]}`;
}
export const stateLabel: Record<ImportJob['state'], string> = {queued: 'Queued', resolving: 'Resolving build', downloading: 'Downloading', verifying: 'Verifying checksum', completed: 'Imported', failed: 'Failed'};
export function matches(entry: ArtifactEntry, applicationName: string | undefined, query: string): boolean {
  const a = entry.artifact;
  return `${applicationName ?? ''} ${a.applicationId} ${a.version} ${a.buildId} ${a.commit ?? ''} ${a.branch ?? ''}`.toLowerCase().includes(query.trim().toLowerCase());
}
