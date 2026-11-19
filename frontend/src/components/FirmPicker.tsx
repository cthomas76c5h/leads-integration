import { useEffect, useId, useRef, useState } from 'react';
import type { DestinationHealth, FirmSummary } from '../api/types';
import { api } from '../api/client';
import { store } from '../lib/format';

interface Option { id: string; name: string; note: string; tone?: 'bad' | 'warn' | 'muted'; group: string }

const RECENT_KEY = 'lead-integrations:recent-firms';
const URGENT: Partial<Record<DestinationHealth, string>> = { stopped: 'a destination stopped', failing: 'a destination failing', late: 'deliveries late' };

const recent = (): { id: string; name: string }[] => store.get<{ id: string; name: string }[]>(RECENT_KEY) ?? [];
const remember = (id: string, name: string) => store.set(RECENT_KEY, [{ id, name }, ...recent().filter((r) => r.id !== id)].slice(0, 6));

const describe = (f: FirmSummary): string => {
  const live = f.liveDestinations ? `${f.liveDestinations} live destination${f.liveDestinations > 1 ? 's' : ''}` : f.connectionKinds.length ? 'none live' : 'no destinations';
  return [f.active ? null : 'inactive', live].filter(Boolean).join(' · ');
};

/**
 * One search box: type part of a firm's name and pick from the list (arrow keys and Enter work).
 * Empty, it suggests firms that need attention and the ones you opened recently.
 */
export function FirmPicker({ value, onChange, label = 'Firm' }: { value: string | null; onChange: (id: string, name: string) => void; label?: string }) {
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const [text, setText] = useState('');
  const [current, setCurrent] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [options, setOptions] = useState<Option[]>([]);
  const [active, setActive] = useState(0);
  const [error, setError] = useState<string | null>(null);

  // The chosen firm's name, so the box shows it rather than an id.
  useEffect(() => {
    if (!value) { setCurrent(null); return; }
    let alive = true;
    const known = recent().find((r) => r.id === value);
    if (known) setCurrent(known.name);
    api.firm(value).then((f) => { if (alive) { setCurrent(f.name); remember(f.id, f.name); } }).catch(() => undefined);
    return () => { alive = false; };
  }, [value]);

  // Suggestions: what you typed, or (empty) firms needing attention plus recent ones.
  useEffect(() => {
    if (!open) return;
    let alive = true;
    const q = text.trim();
    const timer = window.setTimeout(() => {
      const load = q
        ? api.firms({ q, limit: 8 }).then((r) => r.items.map((f): Option => ({ id: f.id, name: f.name, note: describe(f), tone: f.active ? undefined : 'muted', group: 'Matches' })))
        : api.destinations({ limit: 40 }).then((d) => {
            const seen = new Set<string>();
            const urgent: Option[] = [];
            for (const r of d.items) {
              const why = URGENT[r.health] ?? (r.missedLeads > 0 ? 'missed leads' : null);
              if (!why || seen.has(r.firmId)) continue;
              seen.add(r.firmId);
              urgent.push({ id: r.firmId, name: r.firmName, note: why, tone: r.health === 'late' || !URGENT[r.health] ? 'warn' : 'bad', group: 'Needs attention' });
            }
            const mine = recent().filter((r) => !seen.has(r.id)).map((r): Option => ({ ...r, note: '', group: 'Recent' }));
            return [...urgent.slice(0, 5), ...mine];
          });
      load
        .then((o) => { if (alive) { setOptions(o); setActive(0); setError(null); } })
        .catch((e: unknown) => { if (alive) setError(e instanceof Error ? e.message : String(e)); });
    }, q ? 150 : 0);
    return () => { alive = false; window.clearTimeout(timer); };
  }, [text, open]);

  const choose = (o: Option) => {
    remember(o.id, o.name);
    setCurrent(o.name);
    setText('');
    setOpen(false);
    input.current?.blur();
    onChange(o.id, o.name);
  };

  const onKey = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setActive((a) => Math.min(a + 1, options.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter' && open && options[active]) { e.preventDefault(); choose(options[active]); }
    else if (e.key === 'Escape') { setOpen(false); setText(''); }
  };

  const listId = `${id}-list`;
  return (
    <div className="firm-picker">
      <label className="field" htmlFor={`${id}-q`}>{label}
        <input
          id={`${id}-q`} ref={input} type="search" role="combobox" autoComplete="off"
          aria-expanded={open} aria-controls={listId} aria-autocomplete="list"
          aria-activedescendant={open && options[active] ? `${id}-o-${active}` : undefined}
          value={open ? text : (current ?? '')}
          placeholder={current ? 'Type to find another firm…' : 'Type part of a firm name…'}
          onFocus={() => { setText(''); setOpen(true); }}
          onBlur={() => window.setTimeout(() => setOpen(false), 150)}
          onChange={(e) => { setText(e.target.value); setOpen(true); }}
          onKeyDown={onKey}
        />
      </label>
      {open && (
        <ul id={listId} role="listbox" className="firm-options" aria-label="Firms">
          {error && <li className="bad small" role="presentation">Could not load firms: {error}</li>}
          {!error && options.length === 0 && <li className="muted small" role="presentation">{text.trim() ? 'No firm matches.' : 'Type part of a firm name.'}</li>}
          {options.map((o, i) => (
            <li key={`${o.group}-${o.id}`} role="presentation">
              {(i === 0 || options[i - 1]?.group !== o.group) && <span className="firm-group">{o.group}</span>}
              <div
                id={`${id}-o-${i}`} role="option" aria-selected={i === active} className={`firm-option ${i === active ? 'on' : ''}`}
                onMouseDown={(e) => { e.preventDefault(); choose(o); }} onMouseEnter={() => setActive(i)}>
                <span className="grow ellipsis">{o.name}</span>
                {o.note && <span className={`small ${o.tone ?? 'muted'}`}>{o.note}</span>}
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
