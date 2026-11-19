import { useEffect, useRef, useState, type ReactNode } from 'react';
import type { Delivery, Preview, Setup } from '../api/types';
import { api } from '../api/client';
import { MappingEditor } from '../pages/Onboarding';
import { errorText, pretty } from '../lib/format';
import { href } from '../lib/route';
import { useToast } from '../lib/toast';

/** A modal dialog (native <dialog>): Escape and the close button both close it. */
export function FixDialog({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
    const close = () => onClose();
    d?.addEventListener('close', close);
    return () => d?.removeEventListener('close', close);
  }, [onClose]);
  return (
    <dialog ref={ref} className="map-dialog fix-dialog" aria-label={title}>
      <div className="card-head"><h2 className="grow">{title}</h2>
        <button type="button" className="btn icon" aria-label="Close" onClick={() => ref.current?.close()}>✕</button></div>
      {children}
    </dialog>
  );
}

/**
 * Edit the mapping of a destination whose body we build (a webhook with a setup).
 * Saving makes a draft revision: live keeps its approved version until the firm
 * approves the draft, it is tested and activated (RFC-001 sections 5 and 6:
 * adding or removing a caller field needs the firm's approval).
 */
export function MappingFix({ d, setup, onClose, onChanged }: { d: Delivery; setup: Setup; onClose: () => void; onChanged: () => void }) {
  const { act } = useToast();
  const drafting = setup.revising || (!setup.live && setup.version > 1);
  return (
    <FixDialog title={`Mapping: ${d.connectionName}`} onClose={onClose}>
      <p>The receiver said: <strong className="bad">{d.diagnosis?.cause}</strong></p>
      <p className="small muted">
        Each row is one field the receiver gets. Saving makes a <strong>draft version</strong>
        {setup.live ? `; live leads keep version ${setup.activeVersion} until the firm approves the draft, you test it and activate it` : ''}.
        Adding or removing a caller field needs the firm's approval.
      </p>
      <MappingEditor setup={setup} kind={setup.kindLabel} onSaved={onChanged} />
      {drafting && (
        <div className="problem warn">
          <strong>Draft version {setup.version}: {setup.state}</strong>
          <span>{setup.next}.</span>
          <div className="row-actions">
            {setup.state === 'Draft' && (
              <button type="button" className="btn primary" onClick={() => void act(() => api.setupAction(setup.id, 'send'), 'Sent to the firm for approval').then(onChanged)}>Send for approval</button>
            )}
            <a className="btn" href={href('/onboarding', { setup: setup.id })}>Open the setup</a>
          </div>
        </div>
      )}
    </FixDialog>
  );
}

/** What we send to a destination whose body its kind defines (not ours to map), with what its receiver would refuse. */
export function PreviewFix({ d, onClose }: { d: Delivery; onClose: () => void }) {
  const [p, setP] = useState<Preview | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    api.preview(d.connectionId).then(setP).catch((e: unknown) => setError(errorText(e)));
  }, [d.connectionId]);
  return (
    <FixDialog title={`What we send: ${d.connectionName}`} onClose={onClose}>
      <p>The receiver said: <strong className="bad">{d.diagnosis?.cause}</strong></p>
      <p className="small muted">
        This body is built for {d.kind} from the lead; its shape is not ours to map. A missing value comes from the
        destination's settings in its source system (an ID, a recipient, a sign-in), so fix it there, send a test lead, then resend.
      </p>
      {error && <p className="bad small">Could not build the preview: {error}</p>}
      {p && (
        <>
          {p.errors.length > 0 && <div className="problem bad"><strong>Its receiver would refuse this</strong><span>{p.errors.join('; ')}</span></div>}
          <div className="url muted">POST {p.url}</div>
          <pre className="out">{pretty(p.body)}</pre>
        </>
      )}
    </FixDialog>
  );
}
