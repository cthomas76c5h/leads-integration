import type { Kind, MapRow, SourceField } from '../api/types';

export const RECIPES: Record<string, { label: string; help: string }> = {
  'create-intake': { label: 'Create new intake', help: 'A new intake or matter in the CRM for each qualified call.' },
  'create-contact': { label: 'Create or update contact', help: 'Add a contact, or update one with the same phone.' },
  'notify-staff': { label: 'Notify staff', help: 'Send a message to the team; nothing is created in the CRM.' },
};
export const recipeLabel = (r: string): string => RECIPES[r]?.label ?? r;

// Fallback labels for lead field keys (docs/api.md SOURCE_FIELDS). The operator's mapping editor uses
// GET /api/setups/{id}/fields instead; these only label rows where that list is not available (client page).
export const SOURCE_FIELDS: Record<string, string> = {
  full_name: 'Caller name',
  first_name: 'First name',
  last_name: 'Last name',
  phone: 'Phone',
  email: 'Email',
  call_type: 'Practice area',
  message: 'Call summary',
  full_message: 'Call summary + intake answers',
  source: 'Lead source',
  event_id: 'Lead ID (dedupe key)',
};

/** Label for a mapping key: from the setup's field list when given, else the fallback. */
export function fieldLabel(key: string, fields?: SourceField[] | null): string {
  const f = fields?.find((x) => x.key === key);
  if (f) return f.label;
  if (SOURCE_FIELDS[key]) return SOURCE_FIELDS[key];
  if (key.startsWith('custom.')) {
    const slug = key.slice('custom.'.length).replace(/_/g, ' ');
    return `Intake: ${slug.charAt(0).toUpperCase()}${slug.slice(1)}`;
  }
  return key;
}

export const sameRows = (a: MapRow[], b: MapRow[]): boolean => JSON.stringify(a) === JSON.stringify(b);

/** Kind label from the catalog; falls back to the id. */
export function kindLabel(catalog: Kind[] | null, kind: string): string {
  return catalog?.find((k) => k.kind === kind)?.label ?? kind;
}

