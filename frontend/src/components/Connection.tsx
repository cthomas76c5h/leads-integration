import { useState } from 'react';
import type { Connection, CredentialField, Issue, Kind, Preview } from '../api/types';
import { api } from '../api/client';
import { SOURCE_LABEL, ago, pretty } from '../lib/format';
import { useToast } from '../lib/toast';

export function connectionTone(c: Connection): { label: string; tone: 'ok' | 'warn' | 'bad' | 'muted' } {
  if (c.status === 'needs-reconnection') return { label: 'Needs reconnection', tone: 'bad' };
  if (c.status === 'disabled') return { label: 'Disabled', tone: 'bad' };
  if (!c.enabled) return { label: 'Turned off', tone: 'muted' };
  if (c.consecutiveFailures > 0) return { label: `${c.consecutiveFailures} failed in a row`, tone: 'warn' };
  return { label: 'Active', tone: 'ok' };
}

/** Never a value: present + fingerprint + what the field means for this kind. */
export function CredentialChip({ f }: { f: CredentialField }) {
  const text = f.present ? `${f.name} ${f.fingerprint ?? 'present'}` : `${f.name} missing`;
  return (
    <span className={`chip cred ${f.present ? 'ok' : 'missing'}`} title={f.meaning ? `${f.name}: ${f.meaning}` : f.name}>
      {f.present ? '✓ ' : '✗ '}{text}{f.meaning && <span className="muted"> · {f.meaning}</span>}
    </span>
  );
}

export function IssueList({ issues }: { issues: Issue[] }) {
  if (!issues.length) return null;
  return (
    <ul className="issues" aria-label="Issues">
      {issues.map((i) => (
        <li key={i.code + i.message}>
          <span className={`badge ${i.severity === 'info' ? 'muted' : i.severity}`}>{i.severity}</span>
          <span>{i.message} <code className="muted">{i.code}</code></span>
        </li>
      ))}
    </ul>
  );
}

/** One connection: credentials (fingerprints only), settings, issues, status, URL and its actions. */
export function ConnectionCard({ c, kind, onChanged, headingLevel = 3 }: {
  c: Connection; kind?: Kind; onChanged?: () => void; headingLevel?: 2 | 3;
}) {
  const { act } = useToast();
  const [preview, setPreview] = useState<Preview | null>(null);
  const [loadingPreview, setLoadingPreview] = useState(false);
  const status = connectionTone(c);
  const H = headingLevel === 2 ? 'h2' : 'h3';
  const settings = Object.entries(c.settings);
  const done = () => onChanged?.();

  const showPreview = async () => {
    if (preview) { setPreview(null); return; }
    setLoadingPreview(true);
    const p = await act(() => api.preview(c.id, 'full'));
    setLoadingPreview(false);
    if (p) setPreview(p);
  };

  return (
    <article className={`card conn-card ${c.status !== 'active' ? 'off' : !c.enabled ? 'paused' : ''}`} aria-label={c.name}>
      <div className="card-head">
        <div className="grow">
          <H style={{ margin: 0, fontSize: '1rem' }}>{c.name}</H>
          <p className="muted small">
            {kind?.label ?? c.kind} · from {SOURCE_LABEL[c.source]}{c.sourceRef ? ` #${c.sourceRef}` : ''} · added {ago(c.createdAt)}
          </p>
        </div>
        <span className={`badge ${status.tone}`}>{status.label}</span>
      </div>

      {c.disabledReason && <div className="banner bad">{c.disabledReason}</div>}

      <dl className="facts">
        <dt>Delivers to</dt><dd className="url">{c.deliveryUrl}</dd>
        {kind && <><dt>Auth</dt><dd>{kind.auth}{kind.mappingEnforced ? ' · our mapping is the body' : ''}</dd></>}
        <dt>Credentials</dt>
        <dd>
          {c.credentials.length
            ? <div className="chips">{c.credentials.map((f) => <CredentialChip key={f.name} f={f} />)}</div>
            : <span className="muted">none</span>}
        </dd>
        {settings.length > 0 && (
          <>
            <dt>Settings</dt>
            <dd><div className="chips">{settings.map(([k, v]) => <span key={k} className="chip mono">{k}={v}</span>)}</div></dd>
          </>
        )}
      </dl>

      <IssueList issues={c.issues} />

      <div className="row">
        <button type="button" className="btn" aria-expanded={!!preview} disabled={loadingPreview} onClick={() => void showPreview()}>
          {preview ? 'Hide payload' : loadingPreview ? 'Loading…' : 'Preview payload'}
        </button>
        <button type="button" className="btn" disabled={c.status !== 'active' || !c.enabled}
          onClick={() => void act(() => api.testConnection(c.id), 'Test lead sent to this connection').then(done)}>
          Send test
        </button>
        {c.enabled
          ? <button type="button" className="btn" onClick={() => void act(() => api.patchConnection(c.id, { enabled: false }), 'Connection turned off').then(done)}>Disable</button>
          : <button type="button" className="btn" onClick={() => void act(() => api.patchConnection(c.id, { enabled: true }), 'Connection turned on').then(done)}>Enable</button>}
        {c.status === 'disabled' && (
          <button type="button" className="btn primary" onClick={() => void act(() => api.patchConnection(c.id, { reactivate: true }), 'Connection reactivated').then(done)}>
            Reactivate
          </button>
        )}
        {c.status === 'needs-reconnection' && (
          // The lab has no sign-in flow: this stands in for the firm reconnecting. Held leads are sent at once.
          <button type="button" className="btn primary" onClick={() => void act(() => api.patchConnection(c.id, { reactivate: true }), 'Reconnected: held leads are being sent').then(done)}>
            Mark reconnected
          </button>
        )}
      </div>

      {preview && (
        <div className="stack">
          <div className="url muted">POST {preview.url}</div>
          {Object.keys(preview.headers).length > 0 && (
            <pre className="out">{Object.entries(preview.headers).map(([k, v]) => `${k}: ${v}`).join('\n')}</pre>
          )}
          <pre className="out">{pretty(preview.body)}</pre>
          {preview.errors.length > 0 && (
            <ul className="bad small">{preview.errors.map((e) => <li key={e}>{e}</li>)}</ul>
          )}
        </div>
      )}
    </article>
  );
}
