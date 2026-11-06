import {useEffect, useRef, useState, type FormEvent} from 'react';
import {AlertCircle, ArrowDownToLine, ArrowUpRight, Box, Check, Link2, PackageCheck, RefreshCw, ShieldAlert, ShieldCheck} from 'lucide-react';
import {api, type Snapshot} from './inventory-api';
import {formatBytes, jobActive, jobPercent, matches, stateLabel, type ArtifactEntry, type ImportJob, type TeamCityStatus} from './artifacts';
import './artifacts.css';

const date = (value: string) => new Date(value).toLocaleString('en-GB', {day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit'});

export function ArtifactLibrary() {
  const [teamCity, setTeamCity] = useState<TeamCityStatus | null>(null);
  const [entries, setEntries] = useState<ArtifactEntry[]>([]);
  const [jobs, setJobs] = useState<ImportJob[]>([]);
  const [names, setNames] = useState<Record<string, string>>({});
  const [mapped, setMapped] = useState(0);
  const [link, setLink] = useState('');
  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');
  const finished = useRef(new Set<string>());

  async function load() {
    setLoading(true); setError('');
    try {
      const [status, library, recent, inventory] = await Promise.all([api<TeamCityStatus>('teamcity'), api<ArtifactEntry[]>('artifacts'), api<ImportJob[]>('artifacts/imports'), api<Snapshot>('inventory')]);
      setTeamCity(status); setEntries(library); setJobs(recent);
      recent.filter(j => !jobActive(j)).forEach(j => finished.current.add(j.id));
      setNames(Object.fromEntries(inventory.inventory.applications.map(a => [a.id, a.name])));
      setMapped(inventory.inventory.applications.filter(a => a.teamCityBuildType?.trim()).length);
    } catch (e) {setError((e as Error).message);} finally {setLoading(false);}
  }
  useEffect(() => {void load();}, []);
  const polling = jobs.some(jobActive);
  useEffect(() => {
    if (!polling) return;
    const timer = window.setInterval(async () => {
      try {
        const recent = await api<ImportJob[]>('artifacts/imports');
        setJobs(recent);
        const completed = recent.filter(j => !jobActive(j) && !finished.current.has(j.id));
        completed.forEach(j => finished.current.add(j.id));
        if (completed.some(j => j.state === 'completed')) setEntries(await api<ArtifactEntry[]>('artifacts'));
      } catch (e) {setError((e as Error).message);}
    }, 750);
    return () => clearInterval(timer);
  }, [polling]);

  async function submit(event: FormEvent) {
    event.preventDefault(); setSubmitting(true); setError('');
    try {const job = await api<ImportJob>('artifacts/imports', 'POST', {link: link.trim()}); setJobs(previous => [job, ...previous.filter(j => j.id !== job.id)]); setLink('');}
    catch (e) {setError((e as Error).message);} finally {setSubmitting(false);}
  }

  if (loading && !teamCity) return <section className="card inventory-loading"><RefreshCw size={20}/>Loading artifact library…</section>;
  if (!teamCity) return <section className="card inventory-offline"><AlertCircle size={30}/><h2>Artifact API is unavailable</h2><p>{error}</p><code>./gradlew serverRun</code><button className="primary" onClick={() => void load()}>Retry connection</button></section>;
  const visible = entries.filter(e => matches(e, names[e.artifact.applicationId], query));
  const recentJobs = jobs.slice(0, 6);
  return <div className="inventory-workspace artifact-library">
    <div className="inventory-intro"><div className="inventory-symbol"><PackageCheck size={22}/></div><div><strong>Verified TeamCity artifacts</strong><p>Import one immutable build. Its version, commit, size and checksum travel with it.</p></div>{teamCity.configured ? <span className="inventory-connected"><span className="dot"/>TeamCity {teamCity.baseUrl}</span> : <span className="inventory-connected artifact-offline">TeamCity not configured</span>}</div>
    {!teamCity.configured && <div className="inventory-alert error" role="alert"><AlertCircle size={17}/><span>{teamCity.detail} Imports stay disabled until the backend has an approved base URL and token.</span></div>}
    {error && <div className="inventory-alert error" role="alert"><AlertCircle size={17}/><span>{error}</span></div>}
    <section className="card inventory-main">
      <div className="inventory-section-head"><div><h2>Import a build</h2><p>Paste a link to one specific, finished, successful build. The backend resolves the application from its TeamCity build type, downloads the configured artifact path{teamCity.requireChecksum ? ' and requires a published .sha256 file' : ' and verifies a published .sha256 file when present'}. Incomplete downloads are rejected.</p></div></div>
      <form className="artifact-import" onSubmit={submit}><label htmlFor="teamcity-link" className="sr-only">TeamCity build link</label><div className="artifact-link"><Link2 size={16}/><input id="teamcity-link" type="url" required maxLength={2048} placeholder={`Paste a build link, e.g. ${teamCity.baseUrl ?? 'https://teamcity.example.com/'}buildConfiguration/<build type>/<build ID>`} value={link} onChange={e => setLink(e.target.value)} disabled={!teamCity.configured || submitting}/></div><button className="primary" disabled={!teamCity.configured || submitting || !link.trim()}><ArrowDownToLine size={15}/>{submitting ? 'Starting…' : 'Import build'}</button></form>
      {teamCity.configured && mapped === 0 && <div className="inventory-inline-note">No saved application has a TeamCity build type yet. Set one in Inventory → Applications so builds can be matched to an application.</div>}
      {recentJobs.length > 0 && <ul className="artifact-jobs" aria-live="polite">{recentJobs.map(job => <li key={job.id} className={job.state}>
        <div className="artifact-job-head"><strong>{job.applicationId ? names[job.applicationId] ?? job.applicationId : 'TeamCity build'} <span>#{job.buildId}{job.buildNumber ? ` · v${job.buildNumber}` : ''}</span></strong><span className={`state-pill ${job.state === 'completed' ? 'good' : 'neutral'} ${job.state === 'failed' ? 'bad' : ''}`}>{job.state === 'completed' && job.alreadyImported ? 'Already in library' : stateLabel[job.state]}</span></div>
        {jobActive(job) && <div className="artifact-progress" role="progressbar" aria-label={`Import of build ${job.buildId}`} aria-valuenow={jobPercent(job)} aria-valuemin={0} aria-valuemax={100}><span style={{width: `${jobPercent(job)}%`}}/></div>}
        <small>{job.error ?? (job.totalBytes ? `${formatBytes(job.bytesReceived)} of ${formatBytes(job.totalBytes)}` : job.link)}</small>
      </li>)}</ul>}
    </section>
    <section className="card inventory-main">
      <div className="inventory-section-head"><div><h2>Artifact library <span className="count">{entries.length}</span></h2><p>Stored on the backend, outside application releases. Each build is imported once and never overwritten.</p></div><button className="inventory-secondary" disabled={loading} onClick={() => void load()}><RefreshCw size={14}/>Refresh</button></div>
      <div className="inventory-filter"><input aria-label="Search artifacts" placeholder="Search application, version, build or commit…" value={query} onChange={e => setQuery(e.target.value)}/></div>
      <div className="table-scroll"><table className="inventory-table"><thead><tr><th>APPLICATION</th><th>VERSION / BUILD</th><th>SOURCE</th><th>PACKAGE</th><th>CHECKSUM</th><th>IMPORTED</th><th/></tr></thead><tbody>{visible.map(({artifact: a, status}) => <tr key={`${a.applicationId}/${a.buildId}`}>
        <td><strong>{names[a.applicationId] ?? a.applicationId}</strong><small>{a.applicationId}</small></td>
        <td><strong>v{a.version}</strong><small>Build #{a.buildId} · {a.buildTypeId}</small></td>
        <td>{a.commit ? <code className="artifact-commit">{a.commit.slice(0, 12)}</code> : 'No commit'}{a.branch && <small>{a.branch}</small>}</td>
        <td>{a.fileName}<small>{formatBytes(a.size)}</small>{status === 'damaged' && <small className="warning-text">Stored file is damaged · import again</small>}</td>
        <td><span className={`artifact-checksum ${a.checksumVerified ? 'verified' : ''}`} title={`SHA-256 ${a.sha256}`}>{a.checksumVerified ? <ShieldCheck size={14}/> : <ShieldAlert size={14}/>}{a.checksumVerified ? 'Verified' : 'Recorded'}</span><small className="artifact-sha">{a.sha256.slice(0, 16)}…</small></td>
        <td>{date(a.importedAt)}</td>
        <td><div className="row-actions">{status === 'available' && <a className="inventory-icon-button" aria-label={`Download ${a.fileName} build ${a.buildId}`} title="Download package" href={`/api/artifacts/${encodeURIComponent(a.applicationId)}/${encodeURIComponent(a.buildId)}/download`}><ArrowDownToLine size={14}/></a>}<a className="inventory-icon-button" aria-label={`Open build ${a.buildId} in TeamCity`} title="Open in TeamCity" href={a.buildUrl} target="_blank" rel="noreferrer noopener"><ArrowUpRight size={14}/></a></div></td>
      </tr>)}</tbody></table></div>
      {!visible.length && <div className="inventory-empty"><Box size={32}/><h3>{query ? 'No matches' : 'No artifacts yet'}</h3><p>{query ? 'Try another search.' : 'Import a TeamCity build to add its verified package here.'}</p></div>}
      <div className="inventory-footnote"><Check size={12}/> “Verified” means the download matched the build's published SHA-256 file. “Recorded” means TeamCity published no checksum, so the digest was computed on import for later integrity checks.</div>
    </section>
  </div>;
}
