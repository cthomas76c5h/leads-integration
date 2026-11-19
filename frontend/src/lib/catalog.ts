import { useEffect, useState } from 'react';
import type { Kind } from '../api/types';
import { api } from '../api/client';

let cache: Promise<Kind[]> | null = null;

/** The static catalog, fetched once per page load (retried after a failure). */
export function useCatalog(): Kind[] | null {
  const [kinds, setKinds] = useState<Kind[] | null>(null);
  useEffect(() => {
    let alive = true;
    let timer: number | undefined;
    const load = () => {
      cache ??= api.catalog();
      cache.then((k) => { if (alive) setKinds(k); }).catch(() => {
        cache = null;
        if (alive) timer = window.setTimeout(load, 2000);
      });
    };
    load();
    return () => { alive = false; window.clearTimeout(timer); };
  }, []);
  return kinds;
}
