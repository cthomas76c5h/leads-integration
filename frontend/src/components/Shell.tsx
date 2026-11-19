import type { ReactNode } from 'react';

export type PageKey = 'vision' | 'overview' | 'firms' | 'leads' | 'destinations' | 'report' | 'portal' | 'billing' | 'terms' | 'categories' | 'flow' | 'deliveries' | 'onboarding';

const PAGES: { key: PageKey; label: string; href: string }[] = [
  { key: 'vision', label: 'Vision', href: '#/vision' },
  { key: 'overview', label: 'Integrations', href: '#/' },
  { key: 'firms', label: 'Firms', href: '#/firms' },
  { key: 'leads', label: 'Leads', href: '#/leads' },
  { key: 'categories', label: 'Call categories', href: '#/categories' },
  { key: 'report', label: 'Firm report', href: '#/report' },
  { key: 'portal', label: 'Firm portal', href: '#/portal' },
  { key: 'billing', label: 'Billing', href: '#/billing' },
  { key: 'terms', label: 'Terms', href: '#/terms' },
  { key: 'flow', label: 'Delivery graph', href: '#/flow' },
  { key: 'deliveries', label: 'Delivery Center', href: '#/deliveries' },
  { key: 'destinations', label: 'Destinations', href: '#/destinations' },
  { key: 'onboarding', label: 'Onboarding', href: '#/onboarding' },
];

export function Shell({ page, children }: { page: PageKey; children: ReactNode }) {
  return (
    <div className="shell">
      <header className="shell-top">
        <div className="brand">
          <span className="logo" aria-hidden="true">◉</span> Answering AI <span className="sep" aria-hidden="true" />{' '}
          <span className="muted">Lead integrations</span>
        </div>
        <span className="tag">MVP</span>
        <div className="spacer" />
        <nav className="views" aria-label="Pages">
          {PAGES.map((p) => (
            <a key={p.key} href={p.href} aria-current={p.key === page ? 'page' : undefined}>{p.label}</a>
          ))}
        </nav>
      </header>
      <nav className="shell-nav" aria-label="Sections">
        {PAGES.map((p) => (
          <a key={p.key} href={p.href} className={`navlink ${p.key === page ? 'on' : ''}`} aria-current={p.key === page ? 'page' : undefined}>
            {p.label}
          </a>
        ))}
      </nav>
      <main className="shell-main" id="main">{children}</main>
    </div>
  );
}

export function Badge({ tone, children }: { tone: 'ok' | 'warn' | 'bad' | 'muted'; children: ReactNode }) {
  return <span className={`badge ${tone}`}>{children}</span>;
}

export function Loading({ error, what = 'Loading…' }: { error?: string | null; what?: string }) {
  if (error) {
    return (
      <div className="errbox page-error" role="alert">
        <strong>Could not load: {error}</strong>
        <span>Is the backend running on port 5080? This page retries every 2 seconds.</span>
      </div>
    );
  }
  return <p className="boot">{what}</p>;
}
