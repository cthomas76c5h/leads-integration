import { useEffect, useState } from 'react';

/** `key` changes on real navigation only (links, back/forward, a pasted URL), never on replaceQuery. */
export interface Route { path: string[]; query: URLSearchParams; key: string }

function parse(): Route {
  const raw = location.hash.replace(/^#/, '') || '/';
  const [p = '/', q = ''] = raw.split('?');
  return { path: p.split('/').filter(Boolean).map(decodeURIComponent), query: new URLSearchParams(q), key: raw };
}

export function useRoute(): Route {
  const [route, setRoute] = useState(parse);
  useEffect(() => {
    const on = () => setRoute(parse());
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  return route;
}

export function href(path: string, query: Record<string, string | number | boolean | null | undefined> = {}): string {
  const sp = new URLSearchParams();
  for (const [k, v] of Object.entries(query)) if (v !== undefined && v !== null && v !== '' && v !== false) sp.set(k, String(v));
  const s = sp.toString();
  return `#${path}${s ? `?${s}` : ''}`;
}

export function go(path: string, query: Record<string, string | number | boolean | null | undefined> = {}): void {
  location.hash = href(path, query);
}

/** Update the query in place without a new history entry (filters as you type). */
export function replaceQuery(path: string, query: Record<string, string | number | boolean | null | undefined>): void {
  history.replaceState(null, '', href(path, query));
}
