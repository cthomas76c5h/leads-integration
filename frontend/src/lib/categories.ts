import { useEffect, useState } from 'react';
import type { CallMapping, CrmDefault, ReferenceLead, Taxonomy, Tone } from '../api/types';
import { api } from '../api/client';

let taxonomyCache: Promise<Taxonomy> | null = null;
let referenceCache: Promise<ReferenceLead[]> | null = null;

/** Fetch once per page load, retry after a failure (same pattern as useCatalog). */
function useOnce<T>(get: () => Promise<T>, getCache: () => Promise<T> | null, setCache: (p: Promise<T> | null) => void): T | null {
  const [value, setValue] = useState<T | null>(null);
  useEffect(() => {
    let alive = true;
    let timer: number | undefined;
    const load = () => {
      let p = getCache();
      if (!p) { p = get(); setCache(p); }
      p.then((v) => { if (alive) setValue(v); }).catch(() => {
        setCache(null);
        if (alive) timer = window.setTimeout(load, 2000);
      });
    };
    load();
    return () => { alive = false; window.clearTimeout(timer); };
  }, [get, getCache, setCache]);
  return value;
}

const getTaxonomyCache = () => taxonomyCache;
const setTaxonomyCache = (p: Promise<Taxonomy> | null) => { taxonomyCache = p; };
const getReferenceCache = () => referenceCache;
const setReferenceCache = (p: Promise<ReferenceLead[]> | null) => { referenceCache = p; };

/** The call-category taxonomy (static: it is built into the backend). */
export function useTaxonomy(): Taxonomy | null {
  return useOnce(api.callCategories, getTaxonomyCache, setTaxonomyCache);
}

/** The reference leads (static). */
export function useReferenceLeads(): ReferenceLead[] | null {
  return useOnce(api.referenceLeads, getReferenceCache, setReferenceCache);
}

export const CRM_TONE: Record<CrmDefault, Tone> = { send: 'ok', review: 'warn', skip: 'muted', never: 'muted' };

export const CRM_TEXT: Record<CrmDefault, string> = {
  send: 'Send to CRM',
  review: 'Firm decides',
  skip: 'Message, not a lead',
  never: 'Not a call',
};

export interface Labels { category: (id: string) => string; area: (id: string | null) => string | null; modifier: (id: string) => string }

/** Display names from the taxonomy; ids until it has loaded. */
export function labels(t: Taxonomy | null): Labels {
  return {
    category: (id) => (id === 'unmapped' ? 'Unmapped' : t?.categories.find((c) => c.id === id)?.label ?? id),
    area: (id) => (id === null ? null : id === 'unspecified' ? 'No practice area named' : t?.practiceAreas.find((a) => a.id === id)?.label ?? id),
    modifier: (id) => t?.modifiers.find((m) => m.id === id)?.label ?? id,
  };
}

/** Same category and practice area: the firm's own name for this kind of call. */
export const sameKind = (a: CallMapping, b: CallMapping): boolean =>
  a.category === b.category && a.practiceArea === b.practiceArea;

export const pct = (share: number | null): string =>
  share === null ? '—' : share < 0.001 ? '<0.1%' : `${(share * 100).toFixed(1)}%`;
