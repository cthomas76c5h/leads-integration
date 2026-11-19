import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { api } from '../api/client';
import type { Kind, MapRow, Setup, SetupState, SourceField } from '../api/types';
import { Journey } from '../components/Deliveries';
import { FirmPicker } from '../components/FirmTools';
import { Loading } from '../components/Shell';
import { useCatalog } from '../lib/catalog';
import { problem } from '../lib/delivery';
import { ago, errorText } from '../lib/format';
import { usePoll, useTick } from '../lib/poll';
import { replaceQuery } from '../lib/route';
import { fieldLabel, kindLabel, RECIPES, recipeLabel, sameRows } from '../lib/setup';
import { useToast } from '../lib/toast';

const ORDER: SetupState[] = ['Changes requested', 'Test failed', 'Ready to go live', 'Test accepted', 'Ready to test', 'Connection needed', 'Draft', 'Awaiting client approval', 'Testing', 'Live'];
const FLOW: SetupState[] = ['Connection needed', 'Draft', 'Awaiting client approval', 'Changes requested', 'Ready to test', 'Testing', 'Test failed', 'Test accepted', 'Ready to go live', 'Live'];
const MINE: SetupState[] = ['Changes requested', 'Test failed', 'Ready to go live', 'Test accepted', 'Ready to test', 'Draft'];
const idx = (s: SetupState) => FLOW.indexOf(s);

function Check({ n, title, done, active, children }: { n: number; title: string; done: boolean; active: boolean; children?: ReactNode }) {
  return (
    <li className={`check ${done ? 'done' : ''} ${active ? 'active' : ''}`} aria-current={active ? 'step' : undefined}>
      <span className={`tick ${done ? 'done' : ''}`} aria-hidden="true">{done ? '✓' : n}</span>
      <div className="grow">
        <strong>{title}</strong>{done && <span className="visually-hidden"> (done)</span>}
        {children && <div className="check-body">{children}</div>}
      </div>
    </li>
  );
}

/** Mapping sources come from GET /api/setups/{id}/fields (standard fields, full_message, custom.<slug>). */
/** Rows of the mapping; saving makes a new version (a draft revision when live). */
export function MappingEditor({ setup, kind, onSaved }: { setup: Setup; kind: string; onSaved?: () => void }) {
  const { act } = useToast();
  const [rows, setRows] = useState<MapRow[]>(setup.rows);
  const [fields, setFields] = useState<SourceField[] | null>(null);
  const [fieldsError, setFieldsError] = useState<string | null>(null);
  // A new version from the server replaces local edits.
  useEffect(() => setRows(setup.rows), [setup.id, setup.version]);
  useEffect(() => {
    let alive = true;
    api.setupFields(setup.id)
      .then((f) => { if (alive) { setFields(f); setFieldsError(null); } })
      .catch((e: unknown) => { if (alive) setFieldsError(errorText(e)); });
    return () => { alive = false; };
  }, [setup.id]);

  const dirty = !sameRows(rows, setup.rows);
  const options = fields ?? [];
  const firstKey = options.find((f) => f.key === 'phone')?.key ?? options[0]?.key ?? 'phone';
  const update = (i: number, patch: Partial<MapRow>) => setRows(rows.map((x, j) => (j === i ? { ...x, ...patch } : x)));

  return (
    <div className="stack">
      {!setup.enforced && <p className="small note">Plan only: for this kind the receiver's own format is the body. We show the mapping for review; we cannot enforce or verify it.</p>}
      {fieldsError && <p className="small bad">Could not load the firm's fields: {fieldsError}</p>}
      <div className="maprows">
        {rows.map((r, i) => (
          <div key={i} className="maprow">
            <select aria-label={`Answering AI field, row ${i + 1}`} value={r.from} onChange={(e) => update(i, { from: e.target.value })}>
              {!options.some((f) => f.key === r.from) && <option value={r.from}>{fieldLabel(r.from, fields)}</option>}
              {options.map((f) => <option key={f.key} value={f.key}>{f.label}</option>)}
            </select>
            <span aria-hidden="true">→</span>
            <input aria-label={`${kind} field, row ${i + 1}`} value={r.to} onChange={(e) => update(i, { to: e.target.value })} />
            <button type="button" className="btn icon" aria-label={`Remove row ${i + 1}`} onClick={() => setRows(rows.filter((_, j) => j !== i))}>✕</button>
          </div>
        ))}
      </div>
      <div className="row">
        <button type="button" className="btn" onClick={() => setRows([...rows, { from: firstKey, to: '' }])}>Add field</button>
        <button type="button" className="btn primary" disabled={!dirty || rows.some((r) => !r.to.trim())}
          onClick={() => void act(() => api.patchSetup(setup.id, { rows: rows.map((r) => ({ ...r, to: r.to.trim() })) }), 'Mapping saved as a new version').then((r) => { if (r) onSaved?.(); })}>
          Save mapping
        </button>
        {dirty && (
          <span className="small warn">
            Saving makes a new version; the client must approve it again.
            {setup.live ? ` Live leads keep version ${setup.activeVersion} until you activate the new one.` : ''}
          </span>
        )}
      </div>
    </div>
  );
}

function SetupRecord({ setup, catalog }: { setup: Setup; catalog: Kind[] | null }) {
  const { act, say } = useToast();
  const kind = kindLabel(catalog, setup.kind);
  const link = `${location.origin}/review.html?t=${encodeURIComponent(setup.token)}`;
  const test = usePoll(() => api.delivery(setup.testDeliveryId ?? ''), setup.testDeliveryId ? `delivery:${setup.testDeliveryId}` : null).data;
  const testD = test && test.id === setup.testDeliveryId ? test : null;
  const approved = setup.approvals.find((a) => a.version === setup.version && a.decision === 'approved');
  const changes = setup.approvals.find((a) => a.version === setup.version && a.decision === 'changes');
  const st = setup.state;
  const onActive = st === 'Live'; // live and no draft revision ahead of it
  const run = (action: 'send' | 'test' | 'confirm' | 'go-live' | 'discard', ok: string) => void act(() => api.setupAction(setup.id, action), ok);
  const copy = async () => {
    try { await navigator.clipboard.writeText(link); say('Link copied'); } catch { say('Could not copy; select the link instead', true); }
  };

  return (
    <section className="card record" aria-label={`Setup for ${setup.firmName}`}>
      <div className="card-head">
        <div className="grow">
          <h2><a href={`#/firms/${encodeURIComponent(setup.firmId)}`}>{setup.firmName}</a></h2>
          <p className="muted small">
            {kind} · {recipeLabel(setup.recipe)} · version {setup.version}
            {setup.revising ? ` (draft) · live on version ${setup.activeVersion}` : ''}
          </p>
        </div>
        <span className={`badge ${setup.tone}`}>{st}</span>
      </div>

      <div className="linkbox">
        <span className="small"><strong>Client link</strong> (connect, review, approve)</span>
        <code className="ellipsis">{link}</code>
        <button type="button" className="btn" onClick={() => void copy()}>Copy</button>
        <a className="btn" href={link} target="client-view">Open as client</a>
      </div>

      <ol className="checklist">
        <Check n={1} title={setup.live ? 'Draft a revision' : 'Draft the configuration'} done active={st === 'Draft' || st === 'Changes requested'}>
          {onActive && <p className="small muted">Changing the recipe or mapping makes a draft revision. Live leads keep version {setup.activeVersion} until the revision is approved, tested and activated.</p>}
          <div className="row">
            <label className="field small">Recipe
              <select value={setup.recipe} onChange={(e) => void act(() => api.patchSetup(setup.id, { recipe: e.target.value }), 'Recipe changed')}>
                {Object.entries(RECIPES).map(([k, r]) => <option key={k} value={k}>{r.label}</option>)}
                {!RECIPES[setup.recipe] && <option value={setup.recipe}>{setup.recipe}</option>}
              </select>
            </label>
          </div>
          <MappingEditor setup={setup} kind={kind} />
          {setup.revising && (
            <div className="row">
              <button type="button" className="btn danger" onClick={() => {
                if (confirm(`Discard draft version ${setup.version}? The setup goes back to live version ${setup.activeVersion}; the draft stays in the history.`)) run('discard', 'Draft discarded');
              }}>Discard draft version {setup.version}</button>
              <span className="small muted">Live leads are unaffected either way.</span>
            </div>
          )}
        </Check>

        <Check n={2} title="Client connects" done={st !== 'Connection needed'} active={st === 'Connection needed'}>
          {st === 'Connection needed'
            ? <p className="small">Send the client link. They {setup.auth === 'url' ? 'paste their hook address' : `authorize ${kind}`} themselves; nothing is read out or emailed.</p>
            : <p className="small ok">Connected.</p>}
        </Check>

        <Check n={3} title="Client approves" done={!!approved} active={st === 'Draft' || st === 'Awaiting client approval' || st === 'Changes requested'}>
          {changes && <div className="errbox"><strong>Changes requested by {changes.by}</strong><span>{changes.comment}</span></div>}
          {approved && <p className="small ok">Approved by {approved.by}, version {approved.version}, {ago(approved.at)}.</p>}
          {!approved && st !== 'Connection needed' && (
            <div className="row">
              <button type="button" className="btn primary" disabled={setup.sentVersion === setup.version && !changes} onClick={() => run('send', 'Sent for approval')}>
                {setup.sentVersion === setup.version && !changes ? `Version ${setup.version} sent` : `Send version ${setup.version} for approval`}
              </button>
            </div>
          )}
        </Check>

        <Check n={4} title="Test with a real delivery" done={testD?.status === 'delivered' && !!setup.clientConfirmedAt} active={idx(st) >= idx('Ready to test') && !onActive}>
          {approved && !onActive && (
            <div className="row"><button type="button" className="btn" onClick={() => run('test', 'Test lead sent')}>{setup.testDeliveryId ? 'Send another test' : 'Send test lead'}</button></div>
          )}
          {testD && <Journey d={testD} />}
          {testD?.status === 'failed' && (
            <div className="errbox"><strong>⚠ {problem(testD)}</strong>
              <span>{setup.enforced ? 'Fix the mapping in step 1 (it makes a new version for the client to approve), then test again.' : 'Check the receiver setup with the client, then test again.'}</span></div>
          )}
          {testD?.status === 'delivered' && !setup.clientConfirmedAt && (
            <div className="row">
              <span className="small">Accepted by the receiver. Ask the client to look for the record in {kind}.</span>
              <button type="button" className="btn primary" onClick={() => run('confirm', 'Confirmation recorded')}>Client sees it in {kind}</button>
            </div>
          )}
          {setup.clientConfirmedAt && <p className="small ok">Client confirmed the record in {kind}.</p>}
        </Check>

        <Check n={5} title={setup.live ? 'Activate' : 'Go live'} done={onActive} active={st === 'Ready to go live'}>
          {onActive
            ? <p className="small ok">Live on version {setup.activeVersion}. Real leads now go here; watch the Delivery Center.</p>
            : setup.live
              ? <>
                  <div className="row"><button type="button" className="btn primary" disabled={st !== 'Ready to go live'} onClick={() => run('go-live', `Version ${setup.version} is live`)}>Activate version {setup.version}</button></div>
                  <p className="muted small">Until then real leads keep using version {setup.activeVersion}.</p>
                </>
              : <>
                  <div className="row"><button type="button" className="btn primary" disabled={st !== 'Ready to go live'} onClick={() => run('go-live', `${setup.firmName} is live`)}>Confirm go-live</button></div>
                  <p className="muted small">Until then only test leads reach this destination.</p>
                </>}
        </Check>
      </ol>

      <details className="history" open>
        <summary>History</summary>
        <ol>
          {setup.history.map((h, i) => (
            <li key={i}><span className={`who who-${h.who}`}>{h.who}</span> {h.what} <span className="muted small">{ago(h.at)}</span></li>
          ))}
        </ol>
      </details>
    </section>
  );
}

function NewSetup({ setups, catalog, onCreated }: { setups: Setup[]; catalog: Kind[] | null; onCreated: (id: string) => void }) {
  const { act } = useToast();
  const [firmId, setFirmId] = useState<string | null>(null);
  const [kind, setKind] = useState('');
  const [recipe, setRecipe] = useState('create-intake');
  const [cloneFrom, setCloneFrom] = useState('');
  const sources = setups.filter((s) => (s.live || s.state === 'Ready to go live') && (!kind || s.kind === kind));
  const k = kind || catalog?.[0]?.kind || '';

  const create = async () => {
    if (!firmId || !k) return;
    const r = await act(() => api.createSetup({ firmId, kind: k, recipe, cloneFrom: cloneFrom || undefined }), 'Draft created');
    if (r) onCreated(r.id);
  };

  return (
    <section className="card" aria-labelledby="new-h">
      <div className="card-head"><h2 id="new-h" className="grow">New onboarding</h2></div>
      <div className="stack">
        <FirmPicker value={firmId} onChange={(id) => setFirmId(id)} />
        <div className="form-grid">
          <label className="field">Destination kind
            <select value={k} onChange={(e) => { setKind(e.target.value); setCloneFrom(''); }}>
              {(catalog ?? []).map((x) => <option key={x.kind} value={x.kind}>{x.label}</option>)}
            </select>
          </label>
          <label className="field">Recipe
            <select value={recipe} onChange={(e) => setRecipe(e.target.value)}>
              {Object.entries(RECIPES).map(([key, r]) => <option key={key} value={key}>{r.label}</option>)}
            </select>
          </label>
          <label className="field">Start the mapping from
            <select value={cloneFrom} onChange={(e) => setCloneFrom(e.target.value)}>
              <option value="">The recipe's defaults</option>
              {sources.map((s) => <option key={s.id} value={s.id}>Copy {s.firmName} ({kindLabel(catalog, s.kind)}, known good)</option>)}
            </select>
          </label>
        </div>
        <p className="small muted">{RECIPES[recipe]?.help}</p>
      </div>
      <div className="row end">
        <button type="button" className="btn primary" disabled={!firmId || !k} onClick={() => void create()}>Create draft</button>
      </div>
    </section>
  );
}

export function Onboarding({ query }: { query: URLSearchParams }) {
  useTick(5000);
  const catalog = useCatalog();
  const { data: setups, error } = usePoll(api.setups, 'setups');
  const [selected, setSelectedState] = useState<string | null>(query.get('setup'));
  const [creating, setCreating] = useState(false);
  const setSelected = (id: string | null) => { setSelectedState(id); replaceQuery('/onboarding', { setup: id }); };

  const queue = useMemo(() => (setups ?? []).slice().sort((a, b) => ORDER.indexOf(a.state) - ORDER.indexOf(b.state)), [setups]);
  if (!setups) return <><h1>Onboarding</h1><Loading error={error} /></>;
  const sel = setups.find((s) => s.id === selected) ?? null;
  const counts = {
    open: queue.filter((s) => s.state !== 'Live').length,
    mine: queue.filter((s) => MINE.includes(s.state)).length,
    live: queue.filter((s) => s.live).length,
  };

  return (
    <>
      <div className="row between">
        <div>
          <h1>Onboarding</h1>
          <p className="muted">You run the setup with the firm on a call. The firm connects and approves; the system tests and delivers.</p>
        </div>
        <button type="button" className="btn primary" aria-expanded={creating} onClick={() => setCreating(!creating)}>{creating ? 'Close' : '+ New onboarding'}</button>
      </div>
      {error && <p className="small bad">Live update failed: {error}</p>}
      <div className="tiles">
        <div className="tile"><span>Open onboardings</span><strong>{counts.open}</strong></div>
        <div className="tile"><span>Waiting on you</span><strong>{counts.mine}</strong></div>
        <div className="tile"><span>Live</span><strong>{counts.live}</strong></div>
      </div>
      {creating && <NewSetup setups={setups} catalog={catalog} onCreated={(id) => { setSelected(id); setCreating(false); }} />}
      <div className={`ops-grid ${sel ? 'with-record' : ''}`}>
        <section className="card" aria-label="Onboarding queue">
          <div className="table-wrap">
            <table className="log queue">
              <thead><tr><th>Firm</th><th>Destination</th><th>Setup state</th><th>Next action</th></tr></thead>
              <tbody>
                {queue.map((s) => (
                  <tr key={s.id} className={s.id === selected ? 'on' : ''} onClick={() => setSelected(s.id)}>
                    <td className="ellipsis">
                      <button type="button" className="link-btn" aria-pressed={s.id === selected} onClick={(e) => { e.stopPropagation(); setSelected(s.id); }}>
                        <strong>{s.firmName}</strong>
                      </button>
                    </td>
                    <td>{kindLabel(catalog, s.kind)}</td>
                    <td>
                      <span className={`badge ${s.tone}`}>{s.state}</span>
                      {s.revising && <> <span className="muted small">(live on v{s.activeVersion})</span></>}
                    </td>
                    <td className="muted">{s.next}</td>
                  </tr>
                ))}
                {queue.length === 0 && <tr><td colSpan={4} className="muted empty">No onboardings yet.</td></tr>}
              </tbody>
            </table>
          </div>
        </section>
        {sel && <SetupRecord key={sel.id} setup={sel} catalog={catalog} />}
      </div>
    </>
  );
}
