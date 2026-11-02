export const applicationNames = ['Pricing Service', 'Order Gateway', 'Market Data', 'Risk Engine', 'Trade Capture', 'Position Service', 'Reference Data', 'FIX Gateway', 'Execution Router', 'Quote Engine', 'Settlement', 'Reporting', 'Limit Service', 'Session Manager', 'Trade Enrichment', 'Notification Service'];
export const servers = Array.from({length: 8}, (_, i) => `demo-srv-${String(i + 1).padStart(2, '0')}`);
export const applications = applicationNames.map((name, i) => ({id: name.toLowerCase().replaceAll(' ', '-'), name, color: ['blue', 'violet', 'teal'][i % 3]}));
export const instances = applications.flatMap((app, index) => Array.from({length: 3}, (_, i) => ({id: `${app.id}-${String(i + 1).padStart(2, '0')}`, applicationId: app.id, server: servers[(index + i) % 8], service: `${app.id}@${i + 1}.service`, path: `/opt/quickdeploy/${app.id}/${i + 1}`})));
export type Stage = 'install' | 'replicate' | 'restart';
export const stages: Stage[] = ['install', 'replicate', 'restart'];
export type Artifact = {id: string; applicationId: string; version: string; build: string; imported: string; source: string; progress: Record<Stage, number[]>};
export function createArtifact(applicationId: string, version: string, build: string, source = 'Sample TeamCity build'): Artifact {
  return {id: crypto.randomUUID(), applicationId, version, build, source, imported: new Date().toISOString(), progress: {install: [0, 0, 0], replicate: [0, 0, 0], restart: [0, 0, 0]}};
}
export function seedArtifacts(): Artifact[] { return applications.slice(0, 3).map((app, i) => createArtifact(app.id, ['2.18.4', '3.7.1', '1.24.0'][i], ['1842', '967', '1206'][i])); }
export function complete(artifact: Artifact, stage: Stage) {return artifact.progress[stage].every(value => value === 100);}
export function canRun(artifact: Artifact, stage: Stage) {return !complete(artifact, stage) && (stage === 'install' || complete(artifact, stage === 'replicate' ? 'install' : 'replicate'));}
