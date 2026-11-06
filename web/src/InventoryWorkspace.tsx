import {useEffect, useRef, useState, type FormEvent} from 'react';
import {Activity, AlertCircle, Box, Check, Download, FileCode2, Layers, Pencil, Plus, RefreshCw, Save, Server, Trash2, Upload, X} from 'lucide-react';
import {api, type Inventory, type InventoryServer, type InventoryApplication, type InventoryInstance, type Snapshot, type Observation} from './inventory-api';
import './inventory.css';

type Tab = 'servers' | 'applications' | 'instances' | 'discovery' | 'yaml';
type Entity = InventoryServer | InventoryApplication | InventoryInstance;
type Editor = {kind: 'servers' | 'applications' | 'instances'; existing: boolean; values: Record<string, string>};
const title = {servers:'Servers', applications:'Applications', instances:'Instances', discovery:'Version discovery', yaml:'YAML import / export'};
const date = (value?: string | null) => value ? new Date(value).toLocaleString('en-GB') : 'Never';
const copy = <T,>(value: T): T => structuredClone(value);

export function InventoryWorkspace({section}: {section: string}) {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [draft, setDraft] = useState<Inventory | null>(null);
  const [observations, setObservations] = useState<Observation[]>([]);
  const [tab, setTab] = useState<Tab>('instances');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [scanning, setScanning] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [editor, setEditor] = useState<Editor | null>(null);
  const [yaml, setYaml] = useState('');
  const [filter, setFilter] = useState('');
  const [now, setNow] = useState(Date.now());
  const dialog = useRef<HTMLDialogElement>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const dirty = !!snapshot && !!draft && JSON.stringify(snapshot.inventory) !== JSON.stringify(draft);
  useEffect(() => {setTab(section === 'Servers' ? 'servers' : section === 'Applications' ? 'applications' : section === 'Discovery' ? 'discovery' : 'instances');setFilter('');}, [section]);
  async function load() {
    setLoading(true); setError('');
    try {
      const next = await api<Snapshot>('inventory'); setSnapshot(next);setDraft(copy(next.inventory));setYaml(next.yaml);
      try {setObservations(await api<Observation[]>('discovery'));} catch (e) {setError(`Inventory loaded, but discovery state could not be read: ${(e as Error).message}`);}
    } catch(e) {setError((e as Error).message);} finally {setLoading(false);}
  }
  useEffect(() => {void load();}, []);
  useEffect(() => {if (editor && dialog.current && !dialog.current.open) dialog.current.showModal();}, [editor]);
  useEffect(() => {const timer = window.setInterval(() => setNow(Date.now()), 15000);return () => clearInterval(timer);}, []);
  useEffect(() => {
    if (!dirty) return;
    const handler = (event: BeforeUnloadEvent) => {event.preventDefault();};
    window.addEventListener('beforeunload',handler); return () => window.removeEventListener('beforeunload',handler);
  },[dirty]);
  function accept(next: Snapshot) {setSnapshot(next);setDraft(copy(next.inventory));setYaml(next.yaml);setMessage('Inventory saved on the backend.');}
  async function save() {
    if (!snapshot || !draft) return;setSaving(true);setError('');setMessage('');
    try {accept(await api<Snapshot>('inventory','PUT',{baseRevision:snapshot.revision,inventory:draft}));}
    catch(e) {setError((e as Error).message);} finally {setSaving(false);}
  }
  async function importYaml(event: FormEvent) {
    event.preventDefault();if (!snapshot) return;
    if (dirty && !window.confirm('Importing will replace your unsaved inventory draft. Continue?')) return;
    setSaving(true);setError('');setMessage('');
    try {accept(await api<Snapshot>('inventory/import','POST',{baseRevision:snapshot.revision,yaml}));}
    catch(e) {setError((e as Error).message);} finally {setSaving(false);}
  }
  async function scan(instanceId?: string) {
    setScanning(true);setError('');setMessage('');
    try {setObservations(await api<Observation[]>(`discovery${instanceId ? `?instanceId=${encodeURIComponent(instanceId)}` : ''}`,'POST'));setNow(Date.now());setMessage('Read-only discovery finished. Check each instance for results or configuration issues.');}
    catch(e) {setError((e as Error).message);} finally {setScanning(false);}
  }
  function edit(kind: Editor['kind'], entity?: Entity) {
    const empty = kind === 'servers' ? {id:'',name:'',host:'',sshProfile:'demo-reader',port:22} : kind === 'applications' ? {id:'',name:'',teamCityBuildType:'',artifactPath:''} : {id:'',applicationId:draft?.applications[0]?.id || '',serverId:draft?.servers[0]?.id || '',serviceName:'',installRoot:'',versionUrl:'',healthUrl:''};
    setEditor({kind,existing:!!entity,values:Object.fromEntries(Object.entries(entity || empty).map(([key,value]) => [key,String(value ?? '')]))});
  }
  function submitEntity(event: FormEvent) {
    event.preventDefault();if (!editor || !draft) return;
    const values = Object.fromEntries(Object.entries(editor.values).map(([key,value]) => [key,value.trim()]));
    if (!editor.existing && draft[editor.kind].some(e => e.id === values.id)) {setError(`The ID '${values.id}' already exists.`);return;}
    const entity = {...values,...(editor.kind === 'servers' ? {port:Number(values.port)} : {})} as unknown as Entity;
    const next = copy(draft);
    const list = next[editor.kind] as Entity[];
    if (editor.existing) list[list.findIndex(e => e.id === entity.id)] = entity;else list.push(entity);
    setDraft(next);setEditor(null);setMessage('Draft updated. Save inventory to persist these changes.');setError('');
  }
  function remove(kind: Editor['kind'], id: string) {
    if (!draft || !window.confirm(`Remove '${id}' from the inventory draft? This takes effect when you save.`)) return;
    const next = copy(draft);(next[kind] as Entity[]) = next[kind].filter(e => e.id !== id);setDraft(next);setMessage('Draft updated. Save inventory to persist these changes.');
  }
  function discard() {if (dirty && !window.confirm('Discard your unsaved draft and reload the backend inventory?')) return;void load();}
  if (loading && !draft) return <section className="card inventory-loading"><RefreshCw size={20}/>Connecting to inventory API…</section>;
  if (!draft || !snapshot) return <section className="card inventory-offline"><AlertCircle size={30}/><h2>Inventory API is unavailable</h2><p>{error}</p><code>./gradlew serverRun</code><p>The inventory editor needs the backend. No sample data will be presented as a saved inventory.</p><button className="primary" onClick={() => void load()}>Retry connection</button></section>;
  const selectedRows = tab === 'servers' || tab === 'applications' || tab === 'instances' ? draft[tab].filter(e => JSON.stringify(e).toLowerCase().includes(filter.toLowerCase())) : [];
  const fieldNames: Record<Editor['kind'], Record<string,string>> = {
    servers:{id:'Server ID',name:'Display name',host:'Hostname / IPv4 address',sshProfile:'Backend SSH profile',port:'SSH port'},
    applications:{id:'Application ID',name:'Display name',teamCityBuildType:'TeamCity build type (optional)',artifactPath:'Artifact path (optional)'},
    instances:{id:'Instance ID',applicationId:'Application',serverId:'Server',serviceName:'systemd service name',installRoot:'Installation root',versionUrl:'Version URL (optional)',healthUrl:'Health URL (optional)'},
  };
  return <div className="inventory-workspace">
    <div className="inventory-intro"><div className="inventory-symbol"><Layers size={22}/></div><div><strong>Backend inventory</strong><p>Your saved topology and observed service state, together.</p></div><span className="inventory-connected"><span className="dot"/>API connected</span></div>
    <div className="inventory-summary">{[{icon:Server,label:'Servers',count:draft.servers.length},{icon:Box,label:'Applications',count:draft.applications.length},{icon:Layers,label:'Instances',count:draft.instances.length}].map(({icon:Icon,label,count}) => <div key={label}><Icon size={19}/><strong>{count}</strong><span>{label}</span></div>)}<label>Environment<input aria-label="Inventory environment" value={draft.environment} maxLength={64} onChange={e => setDraft({...draft,environment:e.target.value})} disabled={saving || scanning}/></label></div>
    <div className="inventory-savebar"><div><span className={`inventory-status ${dirty ? 'draft' : ''}`}>{dirty ? 'Unsaved changes' : 'Saved inventory'}</span><span className="inventory-revision">Revision {snapshot.revision.slice(0,12)}</span></div><div><button className="inventory-secondary" disabled={saving || scanning || loading} onClick={discard}><RefreshCw size={14}/>Reload</button><button className="primary" disabled={!dirty || saving || scanning} onClick={() => void save()}><Save size={14}/>{saving ? 'Saving…' : 'Save inventory'}</button></div></div>
    {error && <div className="inventory-alert error" role="alert"><AlertCircle size={17}/><span>{error}</span></div>}
    {message && <div className="inventory-alert info" role="status"><Check size={17}/><span>{message}</span></div>}
    <section className="card inventory-main"><div className="inventory-tabs" role="tablist" aria-label="Inventory sections">{(Object.keys(title) as Tab[]).map(t => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'active' : ''} onClick={() => {setTab(t);setFilter('');}}>{t === 'discovery' ? <Activity size={14}/> : t === 'yaml' ? <FileCode2 size={14}/> : null}{title[t]}</button>)}</div>
    {tab === 'discovery' ? <><div className="inventory-section-head"><div><h2>What is actually running?</h2><p>Read-only SSH checks against saved inventory. Refresh manually; observations become stale after 90 seconds.</p></div><button className="primary" disabled={scanning || dirty || saving || !snapshot.inventory.instances.length} onClick={() => void scan()}><RefreshCw size={14}/>{scanning ? 'Checking instances…' : 'Refresh all'}</button></div>{dirty && <div className="inventory-inline-note">Save or discard inventory changes before starting discovery.</div>}<div className="table-scroll"><table className="discovery-table"><thead><tr><th>INSTANCE / SERVER</th><th>SERVICE / HEALTH</th><th>INSTALLED</th><th>RUNNING</th><th>LAST CHECKED</th><th/></tr></thead><tbody>{snapshot.inventory.instances.map(instance => {
      const observation = observations.find(o => o.instanceId === instance.id);
      const changed = !!observation && observation.inventoryRevision !== snapshot.revision;
      const aged = !!observation && now - new Date(observation.checkedAt).getTime() > 90000;
      const current = observation && !changed && !aged;
      const mismatch = current && observation.installedVersion && observation.runningVersion && observation.installedVersion !== observation.runningVersion;
      return <tr key={instance.id}><td><strong>{instance.id}</strong><small>{snapshot.inventory.servers.find(s => s.id === instance.serverId)?.name || instance.serverId}</small><small>{instance.serviceName}</small></td><td><span className={`state-pill ${current && observation.serviceState === 'active' ? 'good' : 'neutral'}`}>{current ? observation.serviceState : 'unknown'}</span><small>{current ? observation.health : 'Not currently verified'}</small>{observation && observation.outcome !== 'observed' && <small className="warning-text">{observation.outcome}</small>}</td><td>{current ? observation.installedVersion || 'Unknown' : 'Unknown'}</td><td><strong>{current ? observation.runningVersion || 'Unknown' : 'Unknown'}</strong>{mismatch && <small className="warning-text">Version mismatch · check restart</small>}{current && observation.buildId && <small>Build #{observation.buildId}</small>}{current && observation.commit && <small>Commit {observation.commit.slice(0,12)}</small>}{observation && !current && observation.runningVersion && <small>Last observed: {observation.runningVersion}</small>}{observation?.lastKnownRunningVersion && observation.outcome !== 'observed' && <small>Last known: {observation.lastKnownRunningVersion} ({date(observation.lastSuccessfulAt)})</small>}</td><td><span>{date(observation?.checkedAt)}</span><small className="warning-text">{changed ? 'Inventory changed · refresh' : aged ? 'Stale · refresh' : !observation ? 'Not checked' : ''}</small>{observation?.detail && <details><summary>Details</summary><p>{observation.detail}</p></details>}</td><td><button className="inventory-icon-button" aria-label={`Refresh ${instance.id}`} disabled={scanning || dirty || saving} onClick={() => void scan(instance.id)}><RefreshCw size={15}/></button></td></tr>;
    })}</tbody></table></div>{!snapshot.inventory.instances.length && <div className="inventory-empty"><Activity size={32}/><h3>No instances to inspect yet</h3><p>Add a server, an application and an instance, then save your inventory.</p><button className="inventory-secondary" onClick={() => setTab('servers')}>Set up inventory</button></div>}<div className="inventory-footnote">Running versions come from the configured version endpoint, with application identity validation and a stable systemd PID. Installed metadata alone never counts as a running version.</div></> : tab === 'yaml' ? <><div className="inventory-section-head"><div><h2>Portable, readable configuration</h2><p>Import replaces the saved inventory after backend validation. Export includes saved data only.</p></div><a className="inventory-secondary" href="/api/inventory/export" download="demo.yaml"><Download size={14}/>Export saved YAML</a></div><form className="yaml-form" onSubmit={importYaml}><label htmlFor="yaml-import">Inventory YAML</label><textarea id="yaml-import" value={yaml} onChange={e => setYaml(e.target.value)} spellCheck={false} maxLength={524288}/><div className="yaml-actions"><input ref={fileInput} type="file" accept=".yaml,.yml,text/yaml,application/yaml" hidden onChange={async e => {const file = e.target.files?.[0];if (!file) return;if (file.size > 524288) {setError('YAML file must be under 512 KiB.');return;}setYaml(await file.text());e.target.value='';}}/><button type="button" className="inventory-secondary" onClick={() => fileInput.current?.click()}><Upload size={14}/>Choose YAML file</button><button className="primary" disabled={saving || scanning}><Save size={14}/>Import and save</button></div></form></> : <><div className="inventory-section-head"><div><h2>{title[tab]}</h2><p>{tab === 'servers' ? 'Linux hosts and their backend SSH profile references.' : tab === 'applications' ? 'Deployable applications and TeamCity build metadata.' : 'Map each application instance to its server and systemd unit.'}</p></div><button className="primary" disabled={saving || scanning || (tab === 'instances' && (!draft.servers.length || !draft.applications.length))} onClick={() => edit(tab)}><Plus size={15}/>Add {tab === 'applications' ? 'application' : tab === 'servers' ? 'server' : 'instance'}</button></div><div className="inventory-filter"><input aria-label={`Filter ${tab}`} placeholder={`Search ${tab}…`} value={filter} onChange={e => setFilter(e.target.value)}/></div><div className="table-scroll"><table className="inventory-table"><thead><tr>{(tab === 'servers' ? ['SERVER','HOST','SSH PROFILE','PORT'] : tab === 'applications' ? ['APPLICATION','TEAMCITY BUILD TYPE','ARTIFACT PATH'] : ['INSTANCE','APPLICATION','SERVER','SYSTEMD UNIT','INSTALL ROOT']).map(t => <th key={t}>{t}</th>)}<th>ACTIONS</th></tr></thead><tbody>{selectedRows.map(row => {const cells = tab === 'servers' ? [(row as InventoryServer).host,(row as InventoryServer).sshProfile,String((row as InventoryServer).port)] : tab === 'applications' ? [(row as InventoryApplication).teamCityBuildType || '—',(row as InventoryApplication).artifactPath || '—'] : [draft.applications.find(a => a.id === (row as InventoryInstance).applicationId)?.name || (row as InventoryInstance).applicationId,draft.servers.find(s => s.id === (row as InventoryInstance).serverId)?.name || (row as InventoryInstance).serverId,(row as InventoryInstance).serviceName,(row as InventoryInstance).installRoot];const referenced = tab !== 'instances' && draft.instances.some(i => tab === 'servers' ? i.serverId === row.id : i.applicationId === row.id);return <tr key={row.id}><td><strong>{'name' in row ? row.name : row.id}</strong>{'name' in row && <small>{row.id}</small>}</td>{cells.map((cell,index) => <td key={index}>{cell}</td>)}<td><div className="row-actions"><button className="inventory-icon-button" aria-label={`Edit ${row.id}`} disabled={saving || scanning} onClick={() => edit(tab,row)}><Pencil size={14}/></button><button className="inventory-icon-button delete" aria-label={`Remove ${row.id}`} disabled={saving || scanning || referenced} title={referenced ? 'Remove or reassign dependent instances first' : 'Remove from draft'} onClick={() => remove(tab,row.id)}><Trash2 size={14}/></button></div></td></tr>;})}</tbody></table></div>{!selectedRows.length && <div className="inventory-empty"><Layers size={32}/><h3>{filter ? 'No matches' : `No ${tab} yet`}</h3><p>{filter ? 'Try another search.' : tab === 'instances' ? 'Create servers and applications first, then map your instances.' : `Add your first ${tab === 'servers' ? 'Linux server' : 'application'} to begin.`}</p></div>}<div className="inventory-footnote">Changes stay in your draft until you save. IDs stay fixed when editing. Remove dependent instances before removing servers or applications.</div></>}
    </section>
    {editor && <dialog ref={dialog} className="inventory-dialog" onCancel={() => setEditor(null)} onClose={() => setEditor(null)}><button className="inventory-dialog-close" aria-label="Close editor" onClick={() => setEditor(null)}><X size={19}/></button><h2>{editor.existing ? 'Edit' : 'Add'} {editor.kind === 'servers' ? 'server' : editor.kind === 'applications' ? 'application' : 'instance'}</h2><p>Update the draft, then save your inventory.</p><form onSubmit={submitEntity}>{Object.entries(fieldNames[editor.kind]).map(([key,label]) => <label key={key}>{label}{key === 'applicationId' || key === 'serverId' ? <select required value={editor.values[key] || ''} onChange={e => setEditor({...editor,values:{...editor.values,[key]:e.target.value}})}>{(key === 'applicationId' ? draft.applications : draft.servers).map(e => <option key={e.id} value={e.id}>{e.name}</option>)}</select> : <input autoFocus={key === (editor.existing ? 'name' : 'id')} required={!['teamCityBuildType','artifactPath','versionUrl','healthUrl'].includes(key)} disabled={editor.existing && key === 'id'} type={key === 'port' ? 'number' : 'text'} min={key === 'port' ? 1 : undefined} max={key === 'port' ? 65535 : undefined} pattern={key === 'id' || key === 'sshProfile' ? '[a-z0-9][a-z0-9_-]{0,63}' : undefined} maxLength={key.endsWith('Url') ? 1024 : 500} placeholder={key === 'serviceName' ? 'pricing@01.service' : key === 'installRoot' ? '/opt/trading/pricing-01' : key.endsWith('Url') ? 'http://127.0.0.1:8101/…' : ''} value={editor.values[key] || ''} onChange={e => setEditor({...editor,values:{...editor.values,[key]:e.target.value}})}/>}</label>)}<p className="inventory-dialog-hint">{editor.kind === 'servers' ? 'SSH credentials and the host allowlist are configured on the backend, never stored here.' : editor.kind === 'instances' ? 'Version and health endpoints must use HTTP on localhost. They are queried on the remote host through SSH.' : 'The TeamCity build type matches imported builds to this application; the artifact path names the file to download from each build.'}</p><div className="yaml-actions"><button type="button" className="inventory-secondary" onClick={() => setEditor(null)}>Cancel</button><button className="primary"><Check size={14}/>Apply to draft</button></div></form></dialog>}
  </div>;
}
