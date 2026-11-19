import { useEffect, useId, useState } from 'react';
import type { ChangeKind, Require, TermsVersion } from '../api/types';
import { api } from '../api/client';
import { Loading } from '../components/Shell';
import { day, when } from '../lib/format';
import { usePoll } from '../lib/poll';
import { href } from '../lib/route';
import { useToast } from '../lib/toast';

const KIND: Record<ChangeKind, { label: string; tone: 'muted' | 'warn' | 'bad'; means: string }> = {
  editorial: { label: 'Editorial', tone: 'muted', means: 'Wording, grammar, formatting: the meaning does not change' },
  pricing: { label: 'Pricing', tone: 'warn', means: 'Prices or billing terms change: the people who pay must agree' },
  material: { label: 'Material', tone: 'bad', means: 'What firms or users may do, or what we do, changes' },
};
const REQUIRE: Record<Require, string> = { none: 'Nobody: a notice only', admins: 'Firm admins', all: 'Every user' };
const defaultRequire: Record<ChangeKind, Require> = { editorial: 'none', pricing: 'admins', material: 'all' };

/**
 * Terms of service, as the business runs them: one global, versioned document. Every
 * user accepts at first sign-in; a new version asks again only when it says so, and
 * only of the users it concerns; every acceptance is recorded (docs/terms/strategy.md).
 */
export function Terms() {
  const terms = usePoll(() => api.terms(), 'terms', 15_000);
  const [selected, setSelected] = useState<number | null>(null);
  if (!terms.data) return <><h1>Terms of service</h1><Loading error={terms.error} /></>;
  const { versions, coverage } = terms.data;
  const current = versions[0];
  const sel = selected ?? current?.version ?? null;

  return (
    <>
      <h1>Terms of service</h1>
      <p className="muted">One set of terms for every firm. Users accept at first sign-in; a new version asks again only when it says so, and only the people it concerns. Minor edits never lock anyone out.</p>

      {current && (
        <section className="card" aria-labelledby="cur-h">
          <div className="card-head"><h2 id="cur-h" className="grow">In force: version {current.version}</h2><Kind v={current} /></div>
          <p>{current.summary}</p>
          <p className="small muted">Published {when(current.publishedAt)} · {current.effectiveAt > Date.now() ? `takes effect ${day(current.effectiveAt)}` : `in effect since ${day(current.effectiveAt)}`} · from a {current.source === 'docx' ? 'Word document' : 'text'} · asks: {REQUIRE[current.require]}</p>
        </section>
      )}

      <section className="card" aria-labelledby="hist-h">
        <h2 id="hist-h">Versions</h2>
        <div className="table-wrap">
          <table className="data">
            <thead><tr><th>Version</th><th>What changed</th><th>Kind</th><th>Asks</th><th>Takes effect</th><th className="num">Accepted</th></tr></thead>
            <tbody>
              {versions.map((v) => {
                const c = coverage.find((x) => x.version === v.version);
                const pct = c && c.asked ? Math.round((c.accepted / c.asked) * 100) : null;
                return (
                  <tr key={v.version} className={v.version === sel ? 'on' : ''}>
                    <td><button type="button" className="link-btn" onClick={() => setSelected(v.version)}>v{v.version}</button><span className="sub">{day(v.publishedAt)}</span></td>
                    <td>{v.summary}</td>
                    <td><Kind v={v} /></td>
                    <td>{REQUIRE[v.require]}</td>
                    <td>{day(v.effectiveAt)}</td>
                    <td className="num">{pct === null ? <span className="muted">notice only</span> : <><strong>{pct}%</strong><span className="sub">{c?.accepted} of {c?.asked}</span></>}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>

      <div className="report-grid">
        <Publish current={current} onPublished={terms.reload} />
        {sel !== null && <Acceptances version={sel} />}
      </div>
    </>
  );
}

function Kind({ v }: { v: TermsVersion }) {
  return <span className={`badge ${KIND[v.changeKind].tone}`}>{KIND[v.changeKind].label}</span>;
}

function Publish({ current, onPublished }: { current: TermsVersion | undefined; onPublished: () => void }) {
  const { act } = useToast();
  const id = useId();
  const [kind, setKind] = useState<ChangeKind>('editorial');
  const [require, setRequire] = useState<Require>('none');
  const [summary, setSummary] = useState('');
  const [body, setBody] = useState(current?.body ?? '');
  const [effective, setEffective] = useState('');
  useEffect(() => { if (!body && current) setBody(current.body); }, [current, body]);
  const changeKind = (k: ChangeKind) => { setKind(k); setRequire(defaultRequire[k]); };
  const effectiveAt = effective ? new Date(`${effective}T00:00:00`).getTime() : 0;
  const preview =
    require === 'none' ? 'Every user sees a notice of what changed. Nobody is asked to accept; nobody is locked out.'
      : `${require === 'admins' ? 'Firm admins' : 'Every user'} ${effectiveAt > Date.now() ? `see a notice asking them to accept before ${day(effectiveAt)}; from then the portal waits for their acceptance` : 'are asked to accept when they next sign in; the portal waits until they do'}. ${require === 'admins' ? 'Other users see a notice only.' : ''}`;

  return (
    <section className="card" aria-labelledby="pub-h">
      <h2 id="pub-h">Publish a new version</h2>
      <p className="small muted">In production the text comes from a Word document (or a Google Doc exported as Word), previewed before publishing. Here, edit the text directly.</p>
      <div className="stack">
        <label className="field" htmlFor={`${id}-k`}>Kind of change
          <select id={`${id}-k`} value={kind} onChange={(e) => changeKind(e.target.value as ChangeKind)}>
            {(Object.keys(KIND) as ChangeKind[]).map((k) => <option key={k} value={k}>{KIND[k].label}: {KIND[k].means}</option>)}
          </select>
        </label>
        <label className="field" htmlFor={`${id}-r`}>Who must accept again
          <select id={`${id}-r`} value={require} onChange={(e) => setRequire(e.target.value as Require)}>
            {(Object.keys(REQUIRE) as Require[]).map((r) => <option key={r} value={r}>{REQUIRE[r]}</option>)}
          </select>
        </label>
        <label className="field" htmlFor={`${id}-e`}>Takes effect
          <input id={`${id}-e`} type="date" value={effective} onChange={(e) => setEffective(e.target.value)} />
        </label>
        <label className="field" htmlFor={`${id}-s`}>What changed (shown to users)
          <input id={`${id}-s`} value={summary} placeholder="e.g. A new section on promotions" onChange={(e) => setSummary(e.target.value)} />
        </label>
        <label className="field" htmlFor={`${id}-b`}>Text
          <textarea id={`${id}-b`} rows={8} value={body} onChange={(e) => setBody(e.target.value)} />
        </label>
        <div className="problem warn"><strong>What users will see</strong><span>{preview}</span></div>
        <button type="button" className="btn primary" disabled={!summary.trim() || !body.trim()}
          onClick={() => void act(() => api.publishTerms({ summary, body, changeKind: kind, require, effectiveAt }), (r) => `Published version ${r.version}`).then((r) => { if (r) { setSummary(''); onPublished(); } })}>
          Publish version {current ? current.version + 1 : 1}
        </button>
      </div>
    </section>
  );
}

function Acceptances({ version }: { version: number }) {
  const rows = usePoll(() => api.termsAcceptances(version, 12), `acceptances:${version}`, 15_000);
  return (
    <section className="card" aria-labelledby="acc-h">
      <h2 id="acc-h">Acceptance records: version {version}</h2>
      <p className="small muted">Who accepted, for which firm, as which role, and when. Append-only: never edited.</p>
      {!rows.data ? <Loading error={rows.error} /> : rows.data.length === 0 ? <p className="muted small">No acceptances recorded for this version.</p> : (
        <table className="data narrow">
          <thead><tr><th>Firm</th><th>User</th><th>When</th></tr></thead>
          <tbody>
            {rows.data.map((a) => (
              <tr key={`${a.firmId}-${a.userId}`}>
                <td><a href={href('/portal', { firm: a.firmId, role: a.role })}>{a.firmName}</a></td>
                <td>{a.role === 'admin' ? 'Firm admin' : 'Team member'}</td>
                <td>{when(a.at)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
