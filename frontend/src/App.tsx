import { lazy, Suspense, useEffect } from 'react';
import { Loading, Shell, type PageKey } from './components/Shell';
import { useRoute } from './lib/route';
import { Overview } from './pages/Overview';
import { FirmList } from './pages/FirmList';
import { FirmPage } from './pages/FirmPage';
import { DeliveryCenter } from './pages/DeliveryCenter';
import { Onboarding } from './pages/Onboarding';
import { Categories } from './pages/Categories';
import { Leads } from './pages/Leads';
import { Destinations } from './pages/Destinations';
import { Report } from './pages/Report';
import { Vision } from './pages/Vision';
import { Portal } from './pages/Portal';
import { BillingOps } from './pages/BillingOps';
import { Terms } from './pages/Terms';

// React Flow and dagre load only when the graph is opened.
const FlowPage = lazy(() => import('./pages/FlowPage'));

const TITLES: Record<PageKey, string> = {
  vision: 'Vision',
  report: 'Your week in calls',
  portal: 'Firm portal',
  billing: 'Billing operations',
  terms: 'Terms of service',
  overview: 'Integrations',
  firms: 'Firms',
  leads: 'Leads',
  destinations: 'Destinations',
  categories: 'Call categories',
  flow: 'Delivery graph',
  deliveries: 'Delivery Center',
  onboarding: 'Onboarding',
};

export function App() {
  const route = useRoute();
  const [head, id] = route.path;
  const page: PageKey =
    head === 'firms' ? 'firms' : head === 'leads' ? 'leads' : head === 'destinations' ? 'destinations' : head === 'vision' ? 'vision' : head === 'report' ? 'report' : head === 'portal' ? 'portal' : head === 'billing' ? 'billing' : head === 'terms' ? 'terms' : head === 'categories' ? 'categories' : head === 'flow' ? 'flow' : head === 'deliveries' ? 'deliveries' : head === 'onboarding' ? 'onboarding' : 'overview';

  useEffect(() => { document.title = `${TITLES[page]} · Answering AI`; }, [page]);

  // Pages copy the query into state once; remount them when navigation changes it.
  return (
    <Shell page={page}>
      {page === 'vision' && <Vision />}
      {page === 'overview' && <Overview />}
      {page === 'report' && <Report key={route.key} query={route.query} />}
      {page === 'portal' && <Portal key={route.key} query={route.query} />}
      {page === 'billing' && <BillingOps key={route.key} query={route.query} />}
      {page === 'terms' && <Terms />}
      {page === 'firms' && (id ? <FirmPage key={id} id={id} /> : <FirmList key={route.key} query={route.query} />)}
      {page === 'leads' && <Leads key={route.key} query={route.query} />}
      {page === 'destinations' && <Destinations key={route.key} query={route.query} />}
      {page === 'categories' && <Categories key={route.key} query={route.query} />}
      {page === 'flow' && (
        <Suspense fallback={<Loading what="Loading the graph…" />}>
          <FlowPage firmId={route.query.get('firm')} />
        </Suspense>
      )}
      {page === 'deliveries' && <DeliveryCenter key={route.key} query={route.query} />}
      {page === 'onboarding' && <Onboarding key={route.key} query={route.query} />}
    </Shell>
  );
}
