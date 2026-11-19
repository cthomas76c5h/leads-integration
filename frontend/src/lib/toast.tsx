import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { errorText } from './format';

export type Act = <T>(fn: () => Promise<T>, ok?: string | ((r: T) => string)) => Promise<T | null>;

interface ToastApi { act: Act; say: (message: string, bad?: boolean) => void }

const ToastContext = createContext<ToastApi | null>(null);

/** Toast plus the page's aria-live #status region (like the prototype). */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toast, setToast] = useState<{ message: string; bad: boolean; at: number } | null>(null);
  useEffect(() => {
    if (!toast) return;
    const t = window.setTimeout(() => setToast(null), 3200);
    return () => window.clearTimeout(t);
  }, [toast]);

  const say = useCallback((message: string, bad = false) => {
    setToast({ message, bad, at: Date.now() });
    const live = document.getElementById('status');
    if (live) live.textContent = message;
  }, []);

  const act = useCallback<Act>(async (fn, ok) => {
    try {
      const r = await fn();
      if (ok) say(typeof ok === 'function' ? ok(r) : ok);
      return r;
    } catch (e) {
      say(errorText(e), true);
      return null;
    }
  }, [say]);

  const value = useMemo(() => ({ act, say }), [act, say]);
  return (
    <ToastContext.Provider value={value}>
      {children}
      {/* Announced through #status; the visual toast itself is not a second live region. */}
      {toast && <div key={toast.at} className={`toast ${toast.bad ? 'bad' : ''}`} aria-hidden="true">{toast.message}</div>}
    </ToastContext.Provider>
  );
}

export function useToast(): ToastApi {
  const v = useContext(ToastContext);
  if (!v) throw new Error('useToast outside ToastProvider');
  return v;
}
