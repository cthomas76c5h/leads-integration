import { useEffect, useState } from 'react';
import { api } from '../api/client';
import { SOURCE_SYSTEMS, type SourceSystem } from '../api/types';
import { Loading } from '../components/Shell';
import { useCatalog } from '../lib/catalog';
import { SOURCE_LABEL } from '../lib/format';
import { usePoll } from '../lib/poll';
import { href, replaceQuery } from '../lib/route';
import { kindLabel } from '../lib/setup';

const LIMIT = 50;
const isSource = (s: string | null): s is SourceSystem => SOURCE_SYSTEMS.some((x) => x === s);

export function FirmList({ query }: { query: URLSearchParams }) {
  const catalog = useCatalog();
  const [text, setText] = useState(query.get('q') ?? '');
  const [q, setQ] = useState(text);
  const [kind, setKind] = useState(query.get('kind') ?? '');
  const initialSource = query.get('source');
  const [source, setSource] = useState<SourceSystem | ''>(isSource(initialSource) ? initialSource : '');
  const [issues, setIssues] = useState(query.get('issues') === '1');
  const [offset, setOffset] = useState(Number(query.get('offset') ?? 0) || 0);

  // Debounce the search box; reset to the first page when a filter changes.
  useEffect(() => {
    const t = window.setTimeout(() => { setQ(text.trim()); }, 250);
    return () => window.clearTimeout(t);
  }, [text]);
  useEffect(() => {
    replaceQuery('/firms', { q, kind, source, issues: issues ? 1 : null, offset: offset || null });
  }, [q, kind, source, issues, offset]);

  const key = JSON.stringify({ q, kind, source, issues, offset });
  const { data, error } = usePoll(() => api.firms({ q, kind, source, issues, limit: LIMIT, offset }), key);
  const filter = <T,>(set: (v: T) => void) => (v: T) => { set(v); setOffset(0); };

  return (
    <>
      <h1>Firms</h1>
      <p className="muted">Every firm known to at least one source, with its delivery connections.</p>
      <section className="card">
        <div className="filters" role="search">
          <label className="field">Search
            <input type="search" value={text} placeholder="Firm name…" onChange={(e) => { setText(e.target.value); setOffset(0); }} />
          </label>
          <label className="field">Kind
            <select value={kind} onChange={(e) => filter(setKind)(e.target.value)}>
              <option value="">All kinds</option>
              {(catalog ?? []).map((k) => <option key={k.kind} value={k.kind}>{k.label}</option>)}
              {kind && catalog && !catalog.some((k) => k.kind === kind) && <option value={kind}>{kind}</option>}
            </select>
          </label>
          <label className="field">Source
            <select value={source} onChange={(e) => filter(setSource)(isSource(e.target.value) ? e.target.value : '')}>
              <option value="">All sources</option>
              {SOURCE_SYSTEMS.map((s) => <option key={s} value={s}>{SOURCE_LABEL[s]}</option>)}
            </select>
          </label>
          <label className="check-inline">
            <input type="checkbox" checked={issues} onChange={(e) => filter(setIssues)(e.target.checked)} /> Has issues
          </label>
        </div>
      </section>

      <section className="card" aria-label="Firms" aria-busy={!data}>
        {!data ? <Loading error={error} /> : (
          <>
            <p className="small muted" aria-live="polite">{data.total} firms{q || kind || source || issues ? ' match' : ''}</p>
            <div className="table-wrap">
              <table className="data">
                <thead>
                  <tr><th>Firm</th><th>Sources</th><th>Connections</th><th>Key</th><th className="num">Issues</th></tr>
                </thead>
                <tbody>
                  {data.items.map((f) => (
                    <tr key={f.id}>
                      <td>
                        <a href={`#/firms/${encodeURIComponent(f.id)}`}><strong>{f.name}</strong></a>
                        {!f.active && <> <span className="badge muted">inactive</span></>}
                      </td>
                      <td><div className="chips">{f.sources.map((s) => <span key={s} className="chip">{SOURCE_LABEL[s]}</span>)}</div></td>
                      <td>
                        <div className="chips">
                          {f.connectionKinds.length
                            ? f.connectionKinds.map((k, i) => <span key={`${k}-${i}`} className="chip">{kindLabel(catalog, k)}</span>)
                            : <span className="muted small">none</span>}
                        </div>
                      </td>
                      <td className="mono small">{f.keyFingerprint ?? <span className="muted">—</span>}</td>
                      <td className="num">{f.issueCount ? <span className="bad">{f.issueCount}</span> : <span className="muted">0</span>}</td>
                    </tr>
                  ))}
                  {data.items.length === 0 && <tr><td colSpan={5} className="muted empty-note">No firm matches.</td></tr>}
                </tbody>
              </table>
            </div>
            <nav className="pager" aria-label="Pages">
              <span className="small muted">
                {data.total === 0 ? '0' : `${offset + 1}–${Math.min(offset + LIMIT, data.total)}`} of {data.total}
              </span>
              <button type="button" className="btn" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - LIMIT))}>← Previous</button>
              <button type="button" className="btn" disabled={offset + LIMIT >= data.total} onClick={() => setOffset(offset + LIMIT)}>Next →</button>
            </nav>
          </>
        )}
      </section>
      <p className="small"><a href={href('/')}>← Integrations overview</a></p>
    </>
  );
}
