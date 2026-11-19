import { useId, useState } from 'react';
import type { FirmDetail } from '../api/types';
import { api } from '../api/client';
import { labels, sameKind, useReferenceLeads, useTaxonomy } from '../lib/categories';
import { plural } from '../lib/format';
import { useToast } from '../lib/toast';

export { FirmPicker } from './FirmPicker';

/** POST /api/firms/{id}/leads: a synthetic lead, or a reference lead, under a call type from the firm's contact types. */
export function SendLeadForm({ firm, onSent }: { firm: FirmDetail; onSent?: () => void }) {
  const { act } = useToast();
  const refs = useReferenceLeads();
  const l = labels(useTaxonomy());
  const active = firm.contactTypes.filter((c) => c.active);
  const [callType, setCallType] = useState('');
  const [variant, setVariant] = useState<'full' | 'sparse'>('full');
  const [webchat, setWebchat] = useState(false);
  const [refId, setRefId] = useState('');
  const id = useId();
  const ref = refs?.find((r) => r.id === refId) ?? null;

  const pickRef = (rid: string) => {
    setRefId(rid);
    const r = refs?.find((x) => x.id === rid);
    // Default to the firm's own active name for the same kind of call, so routing treats it like a real lead.
    setCallType(r ? firm.contactTypes.find((c) => c.active && sameKind(c.mapping, r.mapped))?.name ?? '' : '');
  };

  const send = async () => {
    const r = await act(
      () => api.sendLead(firm.id, ref ? { referenceLead: ref.id, callType: callType || undefined } : { variant, callType: callType || undefined, webchat }),
      (res) => {
        const skipped = res.deliveries.filter((d) => d.status === 'skipped').length;
        const sent = res.deliveries.length - skipped;
        return `Synthetic lead published: ${plural(sent, 'delivery', 'deliveries')}${skipped ? `, ${skipped} skipped by routing` : ''}`;
      },
    );
    if (r) onSent?.();
  };

  return (
    <div className="send-lead">
      <label className="field" htmlFor={`${id}-r`}>Lead
        <select id={`${id}-r`} value={ref ? ref.id : `synthetic:${variant}`} onChange={(e) => {
          const v = e.target.value;
          if (v.startsWith('synthetic:')) { pickRef(''); setVariant(v === 'synthetic:sparse' ? 'sparse' : 'full'); } else pickRef(v);
        }}>
          <optgroup label="Synthetic, with this firm's intake labels">
            <option value="synthetic:full">Full intake</option>
            <option value="synthetic:sparse">Hang-up (sparse)</option>
          </optgroup>
          {refs && (
            <optgroup label="Reference leads (see Call categories)">
              {refs.map((r) => <option key={r.id} value={r.id}>{r.lead.callType} · {l.category(r.mapped.category)}{r.mapped.practiceArea ? ` · ${l.area(r.mapped.practiceArea)}` : ''}</option>)}
            </optgroup>
          )}
        </select>
      </label>
      <label className="field" htmlFor={`${id}-ct`}>Call type
        <select id={`${id}-ct`} value={callType} onChange={(e) => setCallType(e.target.value)}>
          <option value="">{ref ? `As written: ${ref.lead.callType}` : 'Default'}</option>
          {firm.contactTypes.map((c) => (
            <option key={c.name} value={c.name}>{c.name} · {l.category(c.mapping.category)}{c.active ? '' : ' (inactive: routing skips it)'}</option>
          ))}
        </select>
      </label>
      {!ref && <label className="check-inline"><input type="checkbox" checked={webchat} onChange={(e) => setWebchat(e.target.checked)} /> Webchat</label>}
      <button type="button" className="btn primary" onClick={() => void send()}>{ref ? 'Send reference lead' : 'Send synthetic lead'}</button>
      {ref && <span className="small muted">{ref.why}</span>}
      {firm.contactTypes.length > 0 && active.length === 0 && <span className="small warn">No active contact type: routing may skip every connection.</span>}
    </div>
  );
}
