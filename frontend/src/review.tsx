// The client's secure-link page: connect, review what will be shared, approve or ask for changes.
// Sees only its own setup (client routes only).

import { StrictMode, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { api } from './api/client';
import type { ClientSetup, MapRow } from './api/types';
import { ago } from './lib/format';
import { usePoll } from './lib/poll';
import { RECIPES, recipeLabel } from './lib/setup';
import { ToastProvider, useToast } from './lib/toast';
import './styles/base.css';
import './styles/console.css';
import './styles/app.css';

const token = new URLSearchParams(location.search).get('t') ?? '';

/** Field labels come from the setup's sample rows (by source key); the key itself is the fallback. */
const labelOf = (s: ClientSetup, from: string) => s.sample.find((x) => x.from === from)?.label ?? from;
const shared = (s: ClientSetup, rows: MapRow[]) => rows.map((r) => `${labelOf(s, r.from)} → ${r.to}`);

/** Live setup vs the proposed revision, in business terms (no payloads). */
function ChangeSummary({ s }: { s: ClientSetup }) {
  const was = s.active;
  if (!was) return null;
  const before = shared(s, was.rows);
  const after = shared(s, s.rows);
  // ActiveRevision carries only the kind id; it has the same label unless the kind changed.
  const wasDest = was.kind === s.kind ? s.kindLabel : was.kind;
  const added = after.filter((x) => !before.includes(x));
  const removed = before.filter((x) => !after.includes(x));
  const cell = (a: string, b: string) => (a === b ? <td className="muted">Unchanged</td> : <td><strong>{b}</strong></td>);
  return (
    <section className="step-box" aria-labelledby="change-h">
      <h2 id="change-h">What would change</h2>
      <div className="table-wrap">
        <table className="share">
          <thead><tr><th>Setting</th><th>Current (live, version {was.version})</th><th>Proposed (version {s.version})</th></tr></thead>
          <tbody>
            <tr><td>Destination</td><td>{wasDest}</td>{cell(wasDest, s.kindLabel)}</tr>
            <tr><td>Action</td><td>{recipeLabel(was.recipe)}</td>{cell(recipeLabel(was.recipe), recipeLabel(s.recipe))}</tr>
            <tr>
              <td>Information shared</td><td>{before.length} fields</td>
              {added.length || removed.length
                ? <td>{added.map((x) => <div key={`a${x}`} className="ok">+ {x}</div>)}{removed.map((x) => <div key={`r${x}`} className="bad">− {x}</div>)}</td>
                : <td className="muted">Unchanged</td>}
            </tr>
          </tbody>
        </table>
      </div>
      <p className="muted small">Your current setup keeps running until you approve this change and it is tested.</p>
    </section>
  );
}

function Review() {
  const { act } = useToast();
  const { data: s, error, reload } = usePoll(() => api.clientSetup(token), token ? `client:${token}` : null);
  const [url, setUrl] = useState('');
  const [by, setBy] = useState('');
  const [comment, setComment] = useState('');
  const [tried, setTried] = useState(false);

  if (!token || (error && !s)) {
    return (
      <main className="client">
        <div className="card">
          <h1>This link does not work</h1>
          <p className="muted">{token ? `${error}.` : 'The link is missing its code.'} Ask your Answering AI contact for a new link.</p>
        </div>
      </main>
    );
  }
  if (!s) return <p className="boot">Loading…</p>;

  const dest = s.kindLabel;
  const mine = s.approvals.find((a) => a.version === s.version);
  const onActive = s.live && !s.revising; // nothing awaiting the client's decision
  const urlKind = s.auth === 'url'; // otherwise the client authorizes
  const decide = async (decision: 'approved' | 'changes') => {
    setTried(true);
    if (!by.trim() || (decision === 'changes' && !comment.trim())) return;
    const ok = await act(() => api.clientDecision(token, { decision, by: by.trim(), comment: comment.trim(), version: s.version }),
      decision === 'approved' ? 'Thank you: setup approved' : 'Sent to your Answering AI contact');
    if (ok) { setComment(''); setTried(false); reload(); }
  };
  const nameMissing = tried && !by.trim();
  const commentMissing = tried && !comment.trim();

  return (
    <main className="client">
      <header className="client-head">
        <div className="brand"><span className="logo" aria-hidden="true">◉</span> Answering AI</div>
        <span className="tag">MVP</span>
      </header>

      <div className="card">
        <div className="card-head">
          <div className="grow">
            <h1>{RECIPES[s.recipe]?.label ?? 'New intakes'} → {dest}</h1>
            <p className="muted">
              {s.firmName} · {onActive ? 'Live' : s.revising ? `Live on version ${s.activeVersion} · proposed change, version ${s.version}` : `Proposed setup, version ${s.version}`}
            </p>
          </div>
          <span className={`badge ${onActive ? 'ok' : mine?.decision === 'approved' ? 'ok' : mine ? 'bad' : 'muted'}`}>
            {onActive ? 'Live' : mine?.decision === 'approved' ? 'Approved' : mine ? 'Changes requested' : s.connected ? 'Awaiting your approval' : 'Connect first'}
          </span>
        </div>

        {!s.connected && (
          <section className="step-box" aria-labelledby="connect-h">
            <h2 id="connect-h">1. Connect {dest}</h2>
            {urlKind ? (
              <>
                <p>Copy the address {dest} receives intakes on and paste it here. No need to email it.</p>
                <form className="row" onSubmit={(e) => { e.preventDefault(); void act(() => api.clientConnect(token, url), 'Connected').then(reload); }}>
                  <input type="url" required className="grow" placeholder="https://…" value={url} onChange={(e) => setUrl(e.target.value)} aria-label="Address" />
                  <button className="btn primary" type="submit">Connect</button>
                </form>
                <p className="muted small">Lab: use {location.origin}/receiver/ok</p>
              </>
            ) : (
              <>
                <p>Sign in to {dest} and allow Answering AI to create records. Your contact sees the connection at once.</p>
                <div className="row"><button type="button" className="btn primary" onClick={() => void act(() => api.clientConnect(token), 'Connected').then(reload)}>Connect {dest}</button></div>
                <p className="muted small">Lab: this simulates the authorization.</p>
              </>
            )}
          </section>
        )}

        {s.revising && <ChangeSummary s={s} />}

        <section className="step-box" aria-labelledby="review-h">
          <h2 id="review-h">{s.connected ? '' : '2. '}Review what happens{s.revising ? ' after the change' : ''}</h2>
          <dl className="facts wide-facts">
            <dt>Destination</dt><dd>{dest}</dd>
            <dt>When</dt><dd>After each qualified call</dd>
            <dt>Action</dt><dd>{recipeLabel(s.recipe)}{RECIPES[s.recipe] ? ` — ${RECIPES[s.recipe]?.help}` : ''}</dd>
          </dl>
          <h3>What information is shared</h3>
          <div className="table-wrap">
            <table className="share">
              <thead><tr><th>From the call</th><th>Goes into {dest}</th><th>Example</th></tr></thead>
              <tbody>
                {s.sample.map((r, i) => (
                  <tr key={i}><td>{r.label}</td><td><strong>{r.to}</strong></td><td className="muted ellipsis">{r.value}</td></tr>
                ))}
              </tbody>
            </table>
          </div>
          {!s.enforced && <p className="muted small">Your Answering AI contact sets these fields up with you. We confirm the receiver accepts each intake; please check the first ones arrive in {dest}.</p>}
        </section>

        {!onActive && s.connected && (
          <section className="step-box" aria-labelledby="decide-h">
            <h2 id="decide-h">Approve or ask for changes</h2>
            {mine && (
              <p className={mine.decision === 'approved' ? 'ok' : 'bad'}>
                {mine.decision === 'approved' ? `Approved by ${mine.by} ${ago(mine.at)}.` : `You asked: “${mine.comment}”. Your contact will update the setup.`}
              </p>
            )}
            {!mine && (
              <div className="stack">
                <label className="field">Your name
                  <input value={by} onChange={(e) => setBy(e.target.value)} autoComplete="name" required aria-invalid={nameMissing}
                    aria-describedby={nameMissing ? 'by-err' : undefined} />
                </label>
                {nameMissing && <p id="by-err" className="bad small">Enter your name.</p>}
                <label className="field">Comment (needed to ask for changes)
                  <textarea className="note-input" value={comment} onChange={(e) => setComment(e.target.value)} />
                </label>
                {commentMissing && <p className="muted small">A comment is needed to request changes.</p>}
                <div className="row">
                  <button type="button" className="btn" onClick={() => void decide('changes')}>Request changes</button>
                  <button type="button" className="btn primary" onClick={() => void decide('approved')}>Approve setup</button>
                </div>
                <p className="muted small">Your approval is recorded for version {s.version}. Any later change needs your approval again.</p>
              </div>
            )}
          </section>
        )}

        {s.approvals.length > 0 && (
          <details className="history">
            <summary>Approval history</summary>
            <ol>
              {s.approvals.map((a, i) => (
                <li key={i}>
                  Version {a.version}: {a.decision === 'approved' ? 'approved' : 'changes requested'} by {a.by}{a.comment ? ` — “${a.comment}”` : ''}{' '}
                  <span className="muted small">{ago(a.at)}</span>
                </li>
              ))}
            </ol>
          </details>
        )}
      </div>
    </main>
  );
}

const root = document.getElementById('root');
if (root) {
  createRoot(root).render(
    <StrictMode>
      <ToastProvider>
        <Review />
      </ToastProvider>
    </StrictMode>,
  );
}
