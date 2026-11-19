/**
 * Horizontal bars for one series (a magnitude per row): one hue, thin bars with a
 * rounded data end, the value at the tip in text ink, and the row's label and value
 * as text, so the rows double as the table view. Each row has a hover tooltip.
 */
export interface BarRow { id: string; label: string; value: number; note?: string; strong?: boolean }

export function Bars({ rows, format = (v) => String(v), max, label, empty = 'None this week.' }: {
  rows: BarRow[]; format?: (v: number) => string; max?: number; label: string; empty?: string;
}) {
  const top = max ?? Math.max(1, ...rows.map((r) => r.value));
  if (rows.length === 0) return <p className="muted small">{empty}</p>;
  return (
    <ul className="bars" aria-label={label}>
      {rows.map((r) => (
        <li key={r.id} className={r.strong ? 'strong' : ''} title={`${r.label}: ${format(r.value)}${r.note ? ` · ${r.note}` : ''}`}>
          <span className="bar-label">{r.label}{r.note && <small className="muted"> {r.note}</small>}</span>
          <span className="bar-track" aria-hidden="true">
            <span className="bar-fill" style={{ inlineSize: `${Math.max(r.value > 0 ? 1.5 : 0, (r.value / top) * 100)}%` }} />
          </span>
          <span className="bar-value">{format(r.value)}</span>
        </li>
      ))}
    </ul>
  );
}
