import { useCallback, useEffect, useRef, useState } from 'react';
import { errorText } from './format';

export interface Polled<T> {
  data: T | null;
  error: string | null;
  reload: () => void;
}

/**
 * Fetch now, then every `ms` while the page is visible (no SSE in the contract).
 * `key` restarts the loop when the query changes; the last data stays until new data arrives.
 * Pass `key = null` to stop.
 */
export function usePoll<T>(load: () => Promise<T>, key: string | null, ms = 2000): Polled<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const loadRef = useRef(load);
  loadRef.current = load;
  const runRef = useRef<() => void>(() => {});

  useEffect(() => {
    if (key === null) return;
    let alive = true;
    let busy = false;
    const run = async () => {
      if (busy || document.visibilityState !== 'visible') return;
      busy = true;
      try {
        const d = await loadRef.current();
        if (alive) { setData(d); setError(null); }
      } catch (e) {
        if (alive) setError(errorText(e));
      } finally {
        busy = false;
      }
    };
    runRef.current = () => { void run(); };
    void run();
    const t = window.setInterval(() => void run(), ms);
    const onVis = () => { if (document.visibilityState === 'visible') void run(); };
    document.addEventListener('visibilitychange', onVis);
    return () => {
      alive = false;
      window.clearInterval(t);
      document.removeEventListener('visibilitychange', onVis);
    };
  }, [key, ms]);

  const reload = useCallback(() => runRef.current(), []);
  return { data, error, reload };
}

/** Re-render every second for countdowns and "ago" labels. */
export function useTick(ms = 1000): number {
  const [n, set] = useState(0);
  useEffect(() => {
    const t = window.setInterval(() => set((n) => n + 1), ms);
    return () => window.clearInterval(t);
  }, [ms]);
  return n;
}
