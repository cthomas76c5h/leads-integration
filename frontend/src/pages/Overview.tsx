import { api } from '../api/client';
import { SOURCE_SYSTEMS } from '../api/types';
import { Loading } from '../components/Shell';
import { useCatalog } from '../lib/catalog';
import { SOURCE_LABEL, when } from '../lib/format';
import { usePoll } from '../lib/poll';
import { href } from '../lib/route';

const CATEGORY: Record<string, string> = { platform: 'Platform', crm: 'CRM', automation: 'Automation', notify: 'Notify' };

/** The consensus view: which kinds exist where, how many, and what is wrong with them. */
export function Overview() {
  const { data: o, error } = usePoll(api.overview, 'overview');
  const catalog = useCatalog();

  if (!o) return <><h1>Integrations</h1><Loading error={error} /></>;

  const kinds = [...o.kinds].sort((a, b) => b.total - a.total);
  // Catalog kinds nobody uses yet still belong in the consensus table.
  const unused = (catalog ?? []).filter((k) => !o.kinds.some((x) => x.kind === k.kind));

  return (
    <>
      <h1>Integrations</h1>
      <p className="muted">Firms and their delivery configurations imported from the legacy systems. Use this to agree which integrations we support, and how.</p>
      {error && <p className="small bad" role="status">Live update failed: {error}</p>}

      <div className="tiles">
        <a className="tile" href="#/firms"><span>Firms</span><strong>{o.firms}</strong><small className="muted">All sources</small></a>
        {SOURCE_SYSTEMS.map((s) => (
          <a key={s} className="tile" href={href('/firms', { source: s })}>
            <span>From {SOURCE_LABEL[s]}</span><strong>{o.firmsBySource[s] ?? 0}</strong><small className="muted">firms</small>
          </a>
        ))}
        <div className="tile"><span>Matched across sources</span><strong>{o.firmsInSeveralSources}</strong><small className="muted">same key fingerprint</small></div>
      </div>

      <section className="card" aria-labelledby="kinds-h">
        <div className="card-head">
          <h2 id="kinds-h" className="grow">Destination kinds × sources</h2>
          <span className="small muted">Select a kind to see its firms.</span>
        </div>
        <div className="table-wrap">
          <table className="data">
            <thead>
              <tr>
                <th>Kind</th>
                <th>Auth · credentials</th>
                <th>Required payload fields</th>
                {SOURCE_SYSTEMS.map((s) => <th key={s} className="num">{SOURCE_LABEL[s]}</th>)}
                <th className="num">Total</th>
                <th className="num">Enabled</th>
                <th className="num">With issues</th>
              </tr>
            </thead>
            <tbody>
              {kinds.map((k) => {
                const c = catalog?.find((x) => x.kind === k.kind);
                return (
                  <tr key={k.kind}>
                    <td>
                      <a href={href('/firms', { kind: k.kind })}><strong>{k.label}</strong></a>
                      <span className="sub">{CATEGORY[k.category] ?? k.category} · <code>{k.kind}</code></span>
                      {c?.notes && <span className="sub">{c.notes}</span>}
                      {c && c.sourceCodes.length > 0 && (
                        <span className="sub">Codes: {c.sourceCodes.map((sc) => `${SOURCE_LABEL[sc.system]} “${sc.code}”`).join(', ')}</span>
                      )}
                    </td>
                    <td>
                      {c ? <>{c.auth}{c.mappingEnforced && <span className="sub">our mapping is the body</span>}
                        <div className="chips">{c.credentialFields.map((f) => <span key={f} className="chip mono">{f}</span>)}</div></> : <span className="muted">—</span>}
                    </td>
                    <td>
                      {c && c.payloadFields.length
                        ? <div className="chips">{c.payloadFields.map((f) => <span key={f} className="chip mono">{f}</span>)}</div>
                        : <span className="muted">—</span>}
                    </td>
                    {SOURCE_SYSTEMS.map((s) => <td key={s} className="num">{k.bySource[s] ?? <span className="muted">0</span>}</td>)}
                    <td className="num"><strong>{k.total}</strong></td>
                    <td className="num">{k.enabled}</td>
                    <td className="num">{k.withIssues ? <a className="bad" href={href('/firms', { kind: k.kind, issues: 1 })}>{k.withIssues}</a> : <span className="muted">0</span>}</td>
                  </tr>
                );
              })}
              {unused.map((c) => (
                <tr key={c.kind}>
                  <td><strong>{c.label}</strong><span className="sub">{CATEGORY[c.category] ?? c.category} · <code>{c.kind}</code> · in the catalog, no firm uses it</span>{c.notes && <span className="sub">{c.notes}</span>}</td>
                  <td>{c.auth}<div className="chips">{c.credentialFields.map((f) => <span key={f} className="chip mono">{f}</span>)}</div></td>
                  <td><div className="chips">{c.payloadFields.map((f) => <span key={f} className="chip mono">{f}</span>)}</div></td>
                  {SOURCE_SYSTEMS.map((s) => <td key={s} className="num muted">0</td>)}
                  <td className="num muted">0</td><td className="num muted">0</td><td className="num muted">0</td>
                </tr>
              ))}
              {kinds.length === 0 && unused.length === 0 && <tr><td colSpan={10} className="muted empty-note">Nothing imported yet.</td></tr>}
            </tbody>
          </table>
        </div>
      </section>

      <div className="ops-grid with-record">
        <section className="card" aria-labelledby="issues-h">
          <div className="card-head"><h2 id="issues-h" className="grow">Issues found in the imports</h2></div>
          {o.issues.length === 0 ? <p className="muted">No issues.</p> : (
            <div className="table-wrap">
              <table className="data narrow">
                <thead><tr><th>Severity</th><th>Issue</th><th className="num">Count</th></tr></thead>
                <tbody>
                  {[...o.issues].sort((a, b) => b.count - a.count).map((i) => (
                    <tr key={i.code}>
                      <td><span className={`badge ${i.severity === 'info' ? 'muted' : i.severity}`}>{i.severity}</span></td>
                      <td>{i.message}<span className="sub mono">{i.code}</span></td>
                      <td className="num"><strong>{i.count}</strong></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <p className="small"><a href={href('/firms', { issues: 1 })}>Firms with issues →</a></p>
        </section>

        <section className="card" aria-labelledby="imports-h">
          <div className="card-head"><h2 id="imports-h" className="grow">Import runs</h2></div>
          {o.imports.length === 0 ? <p className="muted">No import has run.</p> : (
            <div className="stack">
              {o.imports.map((r) => (
                <div key={r.id} className="scen-like">
                  <div className="row between">
                    <strong>{SOURCE_LABEL[r.system]}</strong>
                    <span className="muted small">{when(r.at)}</span>
                  </div>
                  <div className="small">{r.firms} firms · {r.connections} connections · {r.skipped ? <span className="warn">{r.skipped} skipped</span> : '0 skipped'}</div>
                  {r.notes.length > 0 && <ul className="small muted" style={{ margin: '0.3rem 0 0', paddingLeft: '1.1rem' }}>{r.notes.map((n) => <li key={n}>{n}</li>)}</ul>}
                </div>
              ))}
            </div>
          )}
        </section>
      </div>
    </>
  );
}
