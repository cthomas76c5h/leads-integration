import type { CallMapping } from '../api/types';
import { CRM_TEXT, CRM_TONE, type Labels } from '../lib/categories';

/** Category · practice area · modifiers · CRM default, as one line of chips. */
export function MappingChips({ m, l, crm = true }: { m: CallMapping; l: Labels; crm?: boolean }) {
  const area = l.area(m.practiceArea);
  return (
    <span className="mapping">
      <span className={`badge ${m.category === 'unmapped' ? 'bad' : m.category === 'new-matter' ? 'ok' : 'muted'}`}>{l.category(m.category)}</span>
      {area && <span className="chip">{area}</span>}
      {m.modifiers.map((x) => <span key={x} className="chip">{l.modifier(x)}</span>)}
      {crm && m.crm && <span className={`small ${CRM_TONE[m.crm]}`}>{CRM_TEXT[m.crm]}</span>}
    </span>
  );
}
