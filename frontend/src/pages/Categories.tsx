import { useEffect, useId, useMemo, useState } from 'react';
import type { CallMapping, FirmDetail, ReferenceLead } from '../api/types';
import { api } from '../api/client';
import { Bars } from '../components/Bars';
import { FirmPicker } from '../components/FirmTools';
import { MappingChips } from '../components/Mapping';
import { Loading } from '../components/Shell';
import { CRM_TEXT, CRM_TONE, labels, pct, sameKind, useReferenceLeads, useTaxonomy, type Labels } from '../lib/categories';
import { errorText, plural } from '../lib/format';
import { href, replaceQuery } from '../lib/route';
import { useToast } from '../lib/toast';

/** The call-category taxonomy, a name tester, and the reference leads (docs/call-categories). */
export function Categories({ query }: { query: URLSearchParams }) {
  const t = useTaxonomy();
  const refs = useReferenceLeads();
  const l = labels(t);
  const [filter, setFilter] = useState(query.get('category') ?? '');
  const id = useId();

  if (!t || !refs) return <><h1>Call categories</h1><Loading /></>;

  const a = t.agreement;
  const shown = filter ? refs.filter((r) => r.expect.category === filter) : refs;
  const broken = refs.filter((r) => !r.matches).length;

  return (
    <>
      <h1>Call categories</h1>
      <p className="muted">
        Firms name their kinds of calls in free text: thousands of different names for a few things. Each name maps to one category,
        a practice area for new matters, and modifiers. The rules are in <code>docs/call-categories/taxonomy.json</code>.
      </p>

      <div className="tiles">
        <div className="tile"><span>Categories</span><strong>{t.categories.filter((c) => c.isCallCategory).length}</strong><small className="muted">kinds of call, plus {t.categories.filter((c) => !c.isCallCategory).length} that are not calls</small></div>
        <div className="tile"><span>Practice areas</span><strong>{t.practiceAreas.length}</strong><small className="muted">for new matters</small></div>
        <div className="tile">
          <span>Agree with the legacy lead flag</span>
          <strong>{a.activeTypes ? `${((a.activeTypesClassifiedNewMatter / a.activeTypes) * 100).toFixed(1)}%` : '—'}</strong>
          <small className="muted">{a.activeTypesClassifiedNewMatter} of {a.activeTypes} active types are new matters</small>
        </div>
        <div className="tile">
          <span>Reference leads</span><strong>{refs.length}</strong>
          <small className={broken ? 'bad' : 'muted'}>{broken ? `${broken} map differently than expected` : 'all map as expected'}</small>
        </div>
      </div>

      <FourJobs t={t} />

      <TryName l={l} />

      <section className="card" aria-labelledby="cat-h">
        <div className="card-head">
          <h2 id="cat-h" className="grow">Categories</h2>
          <span className="small muted">In rule order: the first match wins. Share = of firm × name pairs in the import.</span>
        </div>
        <div className="table-wrap">
          <table className="data">
            <thead><tr><th>Category</th><th>CRM by default</th><th className="num">Share</th><th>Common names</th><th className="num">Reference leads</th></tr></thead>
            <tbody>
              {t.categories.map((c) => {
                const n = refs.filter((r) => r.expect.category === c.id).length;
                return (
                  <tr key={c.id}>
                    <td>
                      <strong>{c.label}</strong>{!c.isCallCategory && <span className="badge muted" style={{ marginLeft: '0.4rem' }}>not a call</span>}
                      <span className="sub">{c.description}</span>
                      <span className="sub mono">{c.id}</span>
                    </td>
                    <td><span className={`badge ${CRM_TONE[c.crm]}`}>{CRM_TEXT[c.crm]}</span></td>
                    <td className="num">{pct(c.share)}</td>
                    <td>{c.examples.length ? <div className="chips">{c.examples.map((e) => <span key={e} className="chip mono">{e}</span>)}</div> : <span className="muted">—</span>}</td>
                    <td className="num">{n ? <button type="button" className="link-btn" onClick={() => { setFilter(c.id); replaceQuery('/categories', { category: c.id }); document.getElementById('refs-h')?.scrollIntoView(); }}>{n}</button> : <span className="muted">0</span>}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>

      <div className="ops-grid with-record">
        <section className="card" aria-labelledby="pa-h">
          <h2 id="pa-h">Practice areas (new matters)</h2>
          <div className="table-wrap">
            <table className="data narrow">
              <thead><tr><th>Area</th><th className="num">Firm × names</th><th>Common names</th></tr></thead>
              <tbody>
                {[...t.practiceAreas].sort((x, y) => (y.occurrences ?? 0) - (x.occurrences ?? 0)).map((p) => (
                  <tr key={p.id}>
                    <td>{p.label}<span className="sub mono">{p.id}</span></td>
                    <td className="num">{p.occurrences ?? '—'}</td>
                    <td>{p.examples.length ? <div className="chips">{p.examples.slice(0, 3).map((e) => <span key={e} className="chip mono">{e}</span>)}</div> : <span className="muted">—</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
        <section className="card" aria-labelledby="mod-h">
          <h2 id="mod-h">Modifiers</h2>
          <p className="small muted" style={{ marginTop: 0 }}>Never change the category: “New Client Calls Spanish” is a new matter; “Spanish Calls” is General.</p>
          <ul className="issues">
            {[...t.modifiers].sort((x, y) => (y.occurrences ?? 0) - (x.occurrences ?? 0)).map((m) => (
              <li key={m.id}><span className="chip">{m.label}</span><span className="muted small">{m.occurrences ?? 0} · <code>{m.id}</code></span></li>
            ))}
          </ul>
        </section>
      </div>

      <section className="card" aria-labelledby="refs-h">
        <div className="card-head">
          <h2 id="refs-h" className="grow">Reference leads</h2>
          <label className="field" htmlFor={`${id}-f`}>Category
            <select id={`${id}-f`} value={filter} onChange={(e) => { setFilter(e.target.value); replaceQuery('/categories', { category: e.target.value }); }}>
              <option value="">All ({refs.length})</option>
              {t.categories.map((c) => {
                const n = refs.filter((r) => r.expect.category === c.id).length;
                return n ? <option key={c.id} value={c.id}>{c.label} ({n})</option> : null;
              })}
            </select>
          </label>
        </div>
        <p className="small muted" style={{ marginTop: 0 }}>
          Invented people (555-01xx numbers, example.com), real intake question wording, and the mapping each call type must get.
          Open one to read it or send it to a firm.
        </p>
        <div className="deliveries">
          {shown.map((r) => <RefLead key={r.id} r={r} l={l} />)}
        </div>
      </section>
    </>
  );
}

/** Type any contact-type name and see how it maps. */
function TryName({ l }: { l: Labels }) {
  const [name, setName] = useState('');
  const [m, setM] = useState<CallMapping | null>(null);
  const [error, setError] = useState<string | null>(null);
  const id = useId();

  useEffect(() => {
    if (!name.trim()) { setM(null); return; }
    let alive = true;
    const timer = window.setTimeout(() => {
      api.classify(name)
        .then((x) => { if (alive) { setM(x); setError(null); } })
        .catch((e: unknown) => { if (alive) setError(errorText(e)); });
    }, 150);
    return () => { alive = false; window.clearTimeout(timer); };
  }, [name]);

  return (
    <section className="card" aria-labelledby="try-h">
      <h2 id="try-h">Try a name</h2>
      <div className="row">
        <label className="field grow" htmlFor={`${id}-n`}>Contact type / call type
          <input id={`${id}-n`} type="search" value={name} placeholder="e.g. *New Wills/Trust/Estate/P, AH NO PATCH, General Calls Morgan" onChange={(e) => setName(e.target.value)} />
        </label>
      </div>
      <p aria-live="polite" style={{ marginBottom: 0 }}>
        {error ? <span className="bad small">{error}</span> : m ? <MappingChips m={m} l={l} /> : <span className="muted small">The mapping appears here.</span>}
      </p>
    </section>
  );
}

function RefLead({ r, l }: { r: ReferenceLead; l: Labels }) {
  const x = r.lead;
  const who = [x.firstName, x.lastName].filter(Boolean).join(' ') || '(no name given)';
  return (
    <details className="delivery">
      <summary>
        <code>{x.callType}</code>
        <MappingChips m={r.mapped} l={l} />
        {!r.matches && <span className="badge bad">expected {l.category(r.expect.category)}</span>}
        {x.webchat && <span className="chip">chat</span>}
      </summary>
      <div className="stack" style={{ paddingBlock: '0.6rem' }}>
        <p className="small" style={{ margin: 0 }}><strong>Why it's here:</strong> {r.why}</p>
        <dl className="facts">
          <dt>Caller</dt><dd>{who}</dd>
          <dt>Phone</dt><dd className="mono">{x.phone || '—'}</dd>
          <dt>Email</dt><dd className="mono">{x.email || '—'}</dd>
          <dt>Source</dt><dd>{x.source}</dd>
          <dt>Message</dt><dd>{x.message || <span className="muted">—</span>}</dd>
        </dl>
        {x.custom.length > 0 && (
          <div className="table-wrap">
            <table className="data narrow">
              <thead><tr><th>Intake question</th><th>Answer</th></tr></thead>
              <tbody>{x.custom.map((c) => <tr key={c.label}><td>{c.label}</td><td>{c.data}</td></tr>)}</tbody>
            </table>
          </div>
        )}
        <SendReference r={r} l={l} />
      </div>
    </details>
  );
}

/** Pick a firm, then send this reference lead under the call type as written or the firm's own name for the same kind of call. */
function SendReference({ r, l }: { r: ReferenceLead; l: Labels }) {
  const { act } = useToast();
  const [firmId, setFirmId] = useState<string | null>(null);
  const [firm, setFirm] = useState<FirmDetail | null>(null);
  const [callType, setCallType] = useState('');
  const [sent, setSent] = useState<string | null>(null);
  const id = useId();

  useEffect(() => {
    if (!firmId) return;
    let alive = true;
    api.firm(firmId).then((f) => {
      if (!alive) return;
      setFirm(f);
      // Default: the firm's own active name for this kind of call, if it has one.
      const own = f.contactTypes.find((c) => c.active && sameKind(c.mapping, r.mapped));
      setCallType(own?.name ?? '');
    }).catch(() => { if (alive) setFirm(null); });
    return () => { alive = false; };
  }, [firmId, r.mapped]);

  const matching = useMemo(() => firm?.contactTypes.filter((c) => sameKind(c.mapping, r.mapped)) ?? [], [firm, r.mapped]);
  const others = useMemo(() => firm?.contactTypes.filter((c) => !sameKind(c.mapping, r.mapped)) ?? [], [firm, r.mapped]);

  const send = async () => {
    if (!firm) return;
    const res = await act(
      () => api.sendLead(firm.id, { referenceLead: r.id, callType: callType || undefined }),
      (x) => {
        const skipped = x.deliveries.filter((d) => d.status === 'skipped').length;
        return `Sent “${x.event.lead.callType}” to ${firm.name}: ${plural(x.deliveries.length - skipped, 'delivery', 'deliveries')}${skipped ? `, ${skipped} skipped by routing` : ''}`;
      },
    );
    if (res) setSent(firm.id);
  };

  return (
    <div className="scen-like">
      <strong className="small">Send to a firm</strong>
      <div className="send-lead">
        <FirmPicker value={firmId} onChange={(fid) => { setFirmId(fid); setSent(null); }} />
        {firm && (
          <label className="field" htmlFor={`${id}-ct`}>Call type
            <select id={`${id}-ct`} value={callType} onChange={(e) => setCallType(e.target.value)}>
              <option value="">As written: {r.lead.callType}</option>
              {matching.length > 0 && (
                <optgroup label={`${firm.name}'s names for ${l.category(r.mapped.category)}${r.mapped.practiceArea ? ` · ${l.area(r.mapped.practiceArea)}` : ''}`}>
                  {matching.map((c) => <option key={c.name} value={c.name}>{c.name}{c.active ? '' : ' (inactive: routing skips it)'}</option>)}
                </optgroup>
              )}
              {others.length > 0 && (
                <optgroup label="Other contact types">
                  {others.map((c) => <option key={c.name} value={c.name}>{c.name} · {l.category(c.mapping.category)}{c.active ? '' : ' (inactive)'}</option>)}
                </optgroup>
              )}
            </select>
          </label>
        )}
        <button type="button" className="btn primary" disabled={!firm} onClick={() => void send()}>Send reference lead</button>
      </div>
      {firm && firm.contactTypes.length > 0 && matching.length === 0 && (
        <span className="small warn">{firm.name} has no contact type of this kind: CRM routing will skip it unless you pick another call type.</span>
      )}
      {sent && <a className="small" href={href('/deliveries', { firm: sent })}>See the deliveries →</a>}
    </div>
  );
}

/** The free-text call-type field does four jobs today; only one of them is "what kind of call" (docs/call-categories/why-it-matters.md). */
function FourJobs({ t }: { t: NonNullable<ReturnType<typeof useTaxonomy>> }) {
  const share = (ids: string[]) => t.categories.filter((c) => ids.includes(c.id)).reduce((n, c) => n + (c.share ?? 0), 0);
  const notCalls = ['system', 'staff-routing', 'line-handling'];
  const rows = [
    { id: 'kinds', label: 'The kind of call', value: share(t.categories.map((c) => c.id).filter((id) => !notCalls.includes(id))), note: 'new matter, court, existing client…', strong: true },
    { id: 'flags', label: 'System and billing flags', value: share(['system']), note: '"- Admin", "* No Charge"' },
    { id: 'people', label: 'Staff names', value: share(['staff-routing']), note: 'a routing need hiding here' },
    { id: 'phone', label: 'Phone handling', value: share(['line-handling']), note: '"NO PATCH", "Away No Patch"' },
  ];
  return (
    <section className="card" aria-labelledby="jobs-h">
      <div className="card-head"><h2 id="jobs-h" className="grow">One field, four jobs</h2></div>
      <p className="small muted">
        What firms' call types hold today, as a share of every firm × name pair. Only about half say what the call was about, so the
        most important fact about a call is the least structured data we keep. Each job belongs in its own place.
      </p>
      <Bars rows={rows} max={1} format={(v) => `${Math.round(v * 100)}%`} label="What the call-type field holds" />
    </section>
  );
}
