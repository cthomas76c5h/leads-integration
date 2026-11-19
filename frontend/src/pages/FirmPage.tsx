import { api } from '../api/client';
import type { FirmDetail } from '../api/types';
import { labels, useTaxonomy } from '../lib/categories';
import { ConnectionCard } from '../components/Connection';
import { DeliveryList } from '../components/Deliveries';
import { SendLeadForm } from '../components/FirmTools';
import { Loading } from '../components/Shell';
import { useCatalog } from '../lib/catalog';
import { SOURCE_LABEL } from '../lib/format';
import { usePoll } from '../lib/poll';
import { href } from '../lib/route';
import { kindLabel } from '../lib/setup';

/** A firm's contact types under their category (new matters by practice area), in taxonomy order. */
function ContactTypeGroups({ types }: { types: FirmDetail['contactTypes'] }) {
  const t = useTaxonomy();
  const l = labels(t);
  const order = t?.categories.map((c) => c.id) ?? [];
  const rank = (id: string) => (order.includes(id) ? order.indexOf(id) : order.length);
  const groups = new Map<string, { title: string; rank: number; items: typeof types }>();
  for (const c of types) {
    const m = c.mapping;
    const key = m.category === 'new-matter' ? `new-matter:${m.practiceArea ?? ''}` : m.category;
    const title = m.category === 'new-matter' ? `${l.category(m.category)} · ${l.area(m.practiceArea) ?? ''}` : l.category(m.category);
    const g = groups.get(key) ?? { title, rank: rank(m.category), items: [] };
    g.items.push(c);
    groups.set(key, g);
  }
  const sorted = [...groups.values()].sort((a, b) => a.rank - b.rank || a.title.localeCompare(b.title));
  return (
    <dl className="ct-groups">
      {sorted.map((g) => (
        <div key={g.title}>
          <dt>{g.title}</dt>
          <dd className="chips">
            {g.items.map((c) => <span key={c.name} className={`chip ${c.active ? '' : 'warn'}`}>{c.name}{c.active ? '' : ' · inactive'}</span>)}
          </dd>
        </div>
      ))}
    </dl>
  );
}

export function FirmPage({ id }: { id: string }) {
  const catalog = useCatalog();
  const firm = usePoll(() => api.firm(id), `firm:${id}`);
  const deliveries = usePoll(() => api.deliveries({ firmId: id, limit: 50 }), `firm-deliveries:${id}`);
  const f = firm.data;

  if (!f) return <><p className="small"><a href="#/firms">← Firms</a></p><Loading error={firm.error} /></>;
  const reload = () => { firm.reload(); deliveries.reload(); };
  const issues = f.connections.reduce((n, c) => n + c.issues.length, 0);

  return (
    <>
      <p className="small"><a href="#/firms">← Firms</a></p>
      <div className="row between">
        <div>
          <h1>{f.name}</h1>
          <p className="muted" style={{ margin: '0.2rem 0 0' }}>
            {f.connections.length} connections · {issues ? <span className="bad">{issues} issues</span> : 'no issues'}
            {f.keyFingerprint && <> · key <span className="mono">{f.keyFingerprint}</span></>}
            {!f.active && <> · <span className="badge muted">inactive</span></>}
          </p>
        </div>
        <div className="row">
          <a className="btn" href={href('/flow', { firm: f.id })}>Delivery graph</a>
          <a className="btn" href={href('/deliveries', { firm: f.id })}>Delivery Center</a>
        </div>
      </div>
      {firm.error && <p className="small bad">Live update failed: {firm.error}</p>}

      <section className="card" aria-labelledby="send-h">
        <div className="card-head">
          <h2 id="send-h" className="grow">Send synthetic lead</h2>
          <span className="small muted">One delivery per enabled, active connection; routing may skip some.</span>
        </div>
        <SendLeadForm firm={f} onSent={reload} />
      </section>

      <div className="tiles">
        <section className="card" aria-labelledby="src-h">
          <h2 id="src-h">Sources</h2>
          <ul className="issues">
            {f.sourcesDetail.map((s) => (
              <li key={`${s.system}:${s.sourceId}`}>
                <span className="chip">{SOURCE_LABEL[s.system]}</span>
                <span>{s.name} <span className="muted mono small">#{s.sourceId}</span>{!s.active && <span className="muted"> · inactive</span>}</span>
              </li>
            ))}
          </ul>
        </section>
        <section className="card" aria-labelledby="ct-h">
          <h2 id="ct-h">Contact types (routing)</h2>
          <p className="small muted" style={{ marginTop: 0 }}>
            A lead goes out to a CRM only if its call type matches an active one. Grouped by <a href="#/categories">call category</a>.
          </p>
          {f.contactTypes.length
            ? <ContactTypeGroups types={f.contactTypes} />
            : <p className="muted small">None: every call type goes out.</p>}
        </section>
        <section className="card" aria-labelledby="cl-h">
          <h2 id="cl-h">Custom labels</h2>
          {f.customLabels.length
            ? <div className="chips">{f.customLabels.map((l) => <span key={l} className="chip">{l}</span>)}</div>
            : <p className="muted small">None seen.</p>}
        </section>
      </div>

      <h2 className="section-title">Connections</h2>
      {f.connections.length === 0 ? <p className="muted">No connections.</p> : (
        <div className="conn-grid">
          {f.connections.map((c) => (
            <ConnectionCard key={c.id} c={c} kind={catalog?.find((k) => k.kind === c.kind)} onChanged={reload} />
          ))}
        </div>
      )}

      {f.setups.length > 0 && (
        <section className="card" aria-labelledby="setups-h">
          <h2 id="setups-h">Onboarding setups</h2>
          <ul className="issues">
            {f.setups.map((s) => (
              <li key={s.id}>
                <span className={`badge ${s.tone}`}>{s.state}</span>
                <a href={href('/onboarding', { setup: s.id })}>{kindLabel(catalog, s.kind)} · version {s.version}</a>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="card" aria-labelledby="recent-h">
        <div className="card-head"><h2 id="recent-h" className="grow">Recent deliveries</h2><span className="small muted">Tests included, newest first</span></div>
        {deliveries.data
          ? <DeliveryList rows={deliveries.data} showConnection onChanged={reload} />
          : <Loading error={deliveries.error} />}
      </section>
    </>
  );
}
