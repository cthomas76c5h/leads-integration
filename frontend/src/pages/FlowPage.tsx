// Delivery graph for one firm: firm → each connection → where it delivers.
// Ported in spirit from the webhook-lab canvas (React Flow 12 + dagre).

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  Background, ControlButton, Controls, Handle, MarkerType, MiniMap, Position, ReactFlow, ReactFlowProvider,
  useNodesState, useReactFlow, type Edge, type Node, type NodeProps, type NodeTypes,
} from '@xyflow/react';
import dagre from 'dagre';
import { api } from '../api/client';
import type { Connection, Delivery, FirmDetail, Kind } from '../api/types';
import { ConnectionCard, connectionTone } from '../components/Connection';
import { DeliveryList } from '../components/Deliveries';
import { FirmPicker, SendLeadForm } from '../components/FirmTools';
import { Loading } from '../components/Shell';
import { useCatalog } from '../lib/catalog';
import { leadNo, store } from '../lib/format';
import { usePoll, useTick } from '../lib/poll';
import { go } from '../lib/route';

// ---------- graph ----------

interface Stats { list: Delivery[]; accepted: number; failed: number; pending: number; last: Delivery | null }
type FirmNodeT = Node<{ firm: FirmDetail }, 'firm'>;
type ConnNodeT = Node<{ c: Connection; kindLabel: string; stats: Stats }, 'connection'>;
type RxNodeT = Node<{ url: string; local: boolean; count: number }, 'receiver'>;
type AppNode = FirmNodeT | ConnNodeT | RxNodeT;

const SIZE: Record<AppNode['type'], [number, number]> = { firm: [280, 130], connection: [320, 220], receiver: [260, 110] };

function stats(deliveries: Delivery[], id: string): Stats {
  const list = deliveries.filter((d) => d.connectionId === id && d.status !== 'skipped');
  const count = (...s: Delivery['status'][]) => list.filter((d) => s.includes(d.status)).length;
  return { list, accepted: count('delivered'), failed: count('failed', 'cancelled'), pending: count('pending', 'delivering'), last: list[0] ?? null };
}

function edgeLook(last: Delivery | null, c: Connection): { color: string; label: string; animated: boolean } {
  if (c.status !== 'active') return { color: 'var(--bad)', label: c.status === 'needs-reconnection' ? 'needs reconnection' : 'disabled', animated: false };
  if (!c.enabled) return { color: 'var(--line-strong)', label: 'off', animated: false };
  if (!last) return { color: 'var(--line-strong)', label: 'waiting for a lead', animated: false };
  const a = last.attempts.at(-1);
  if (last.status === 'delivering') return { color: 'var(--warn)', label: 'sending…', animated: true };
  if (last.status === 'pending') {
    const s = last.nextAttemptAt ? Math.max(0, Math.ceil((last.nextAttemptAt - Date.now()) / 1000)) : 0;
    return { color: 'var(--warn)', label: a ? `${a.status ?? 'no answer'} · retry in ${s}s` : 'queued', animated: true };
  }
  if (last.status === 'delivered') return { color: 'var(--ok)', label: `accepted · HTTP ${a?.status ?? 200}`, animated: false };
  return { color: 'var(--bad)', label: a?.status ? `refused · HTTP ${a.status}` : (last.reason ?? 'failed'), animated: false };
}

const isLocal = (url: string) => /\/receiver\//.test(url) || /^https?:\/\/localhost[:/]/.test(url);
const shortUrl = (url: string) => {
  try { const u = new URL(url, location.origin); return isLocal(url) ? u.pathname : u.host; } catch { return url; }
};

function buildGraph(firm: FirmDetail, deliveries: Delivery[], catalog: Kind[] | null): { nodes: AppNode[]; edges: Edge[] } {
  const nodes: AppNode[] = [{ id: 'firm', type: 'firm', position: { x: 0, y: 0 }, data: { firm } }];
  const edges: Edge[] = [];
  const receivers = new Map<string, number>();
  for (const c of firm.connections) {
    const st = stats(deliveries, c.id);
    nodes.push({ id: `c:${c.id}`, type: 'connection', position: { x: 0, y: 0 }, data: { c, kindLabel: catalog?.find((k) => k.kind === c.kind)?.label ?? c.kind, stats: st } });
    edges.push({
      id: `in:${c.id}`, source: 'firm', target: `c:${c.id}`, label: c.enabled ? 'lead' : 'off', className: 'quiet-edge',
      style: c.enabled ? undefined : { strokeDasharray: '5 4' },
    });
    const rx = `rx:${c.deliveryUrl}`;
    receivers.set(rx, (receivers.get(rx) ?? 0) + 1);
    const look = edgeLook(st.last, c);
    edges.push({
      id: `out:${c.id}`, source: `c:${c.id}`, target: rx, label: look.label, animated: look.animated,
      style: { stroke: look.color, strokeWidth: 2 },
      markerEnd: { type: MarkerType.ArrowClosed, color: look.color },
      labelStyle: { fill: look.color, fontWeight: 600 },
    });
  }
  for (const [id, count] of receivers) {
    const url = id.slice(3);
    nodes.push({ id, type: 'receiver', position: { x: 0, y: 0 }, data: { url, local: isLocal(url), count } });
  }
  return { nodes, edges };
}

function layout(nodes: AppNode[], edges: Edge[]): Record<string, { x: number; y: number }> {
  const g = new dagre.graphlib.Graph();
  g.setDefaultEdgeLabel(() => ({}));
  g.setGraph({ rankdir: 'LR', nodesep: 36, ranksep: 110, marginx: 20, marginy: 20 });
  const size = (n: AppNode): [number, number] => [n.measured?.width ?? SIZE[n.type][0], n.measured?.height ?? SIZE[n.type][1]];
  for (const n of nodes) { const [width, height] = size(n); g.setNode(n.id, { width, height }); }
  for (const e of edges) if (g.hasNode(e.source) && g.hasNode(e.target)) g.setEdge(e.source, e.target);
  dagre.layout(g);
  const out: Record<string, { x: number; y: number }> = {};
  for (const n of nodes) {
    const p = g.node(n.id);
    const [w, h] = size(n);
    if (p) out[n.id] = { x: p.x - w / 2, y: p.y - h / 2 };
  }
  return out;
}

// ---------- nodes ----------

const FlowCtx = createContext<{ open: (id: string) => void }>({ open: () => {} });

function NodeShell({ kind, chip, title, selected, warn, paused, children }: {
  kind: string; chip: string; title: string; selected: boolean; warn?: boolean; paused?: boolean; children: ReactNode;
}) {
  return (
    <div className={`node node-${kind} ${selected ? 'selected' : ''} ${warn ? 'warn' : ''} ${paused ? 'paused' : ''}`}>
      <div className="node-head"><span className="node-chip">{chip}</span><span className="node-title" title={title}>{title}</span></div>
      <div className="node-body">{children}</div>
    </div>
  );
}

function FirmNode({ data, selected }: NodeProps<FirmNodeT>) {
  const { open } = useContext(FlowCtx);
  const f = data.firm;
  return (
    <>
      <NodeShell kind="firm" chip="Firm" title={f.name} selected={selected}>
        <div className="muted small">Each finished call publishes one lead. It fans out to every enabled connection that routing allows.</div>
        <div className="row nodrag"><button type="button" className="btn primary" onClick={() => open('firm')}>Send synthetic lead…</button></div>
      </NodeShell>
      <Handle type="source" position={Position.Right} />
    </>
  );
}

function ConnectionNode({ id, data, selected }: NodeProps<ConnNodeT>) {
  const { open } = useContext(FlowCtx);
  const { c, stats: s, kindLabel } = data;
  const tone = connectionTone(c);
  return (
    <>
      <Handle type="target" position={Position.Left} />
      <NodeShell kind="connection" chip={c.status === 'needs-reconnection' ? 'Needs reconnection' : c.status !== 'active' ? 'Disabled' : c.enabled ? kindLabel : `${kindLabel} · off`} title={c.name}
        selected={selected} warn={c.status !== 'active'} paused={c.status === 'active' && !c.enabled}>
        <div className="chips">
          <span className={`chip ${tone.tone === 'ok' ? '' : 'warn'}`}>{tone.label}</span>
          <span className="chip">{c.source}</span>
          {c.issues.length > 0 && <span className="chip warn">{c.issues.length} issues</span>}
        </div>
        {c.disabledReason && <div className="banner bad">{c.disabledReason}</div>}
        <div className="stats">
          <span className="ok">✓ {s.accepted}</span><span className="warn">⟳ {s.pending}</span><span className="bad">✗ {s.failed}</span>
        </div>
        <ol className="mini-log nowheel">
          {s.list.slice(0, 6).map((d) => (
            <li key={d.id} className={d.status}>
              <span className="dot" />
              <span className="ellipsis mono">{leadNo(d.eventId)}{d.test ? ' · test' : ''}</span>
              <span className="muted">{d.attempts.at(-1)?.status ?? '…'}</span>
            </li>
          ))}
        </ol>
        <div className="row nodrag"><button type="button" className="btn" onClick={() => open(id)}>Details…</button></div>
      </NodeShell>
      <Handle type="source" position={Position.Right} />
    </>
  );
}

function ReceiverNode({ data, selected }: NodeProps<RxNodeT>) {
  return (
    <>
      <Handle type="target" position={Position.Left} />
      <NodeShell kind={data.local ? 'receiver' : 'external'} chip={data.local ? 'Fake receiver' : 'External'} title={shortUrl(data.url)} selected={selected}>
        <div className="muted small url">{data.url}</div>
        <div className="muted small">{data.local ? 'Local fake CRM: validates the payload like the real one.' : 'A real address; we cannot see what it receives.'}</div>
      </NodeShell>
    </>
  );
}

const nodeTypes: NodeTypes = { firm: FirmNode, connection: ConnectionNode, receiver: ReceiverNode };

// ---------- page ----------

function Graph({ firm, deliveries, reload }: { firm: FirmDetail; deliveries: Delivery[]; reload: () => void }) {
  const tick = useTick(); // countdown labels on retrying edges
  const catalog = useCatalog();
  const flow = useReactFlow();
  const [nodes, setNodes, onNodesChange] = useNodesState<AppNode>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const graph = useMemo(() => buildGraph(firm, deliveries, catalog), [firm, deliveries, catalog, tick]);

  // Merge fresh data into React Flow's nodes, keeping positions; lay out when the set of nodes changes.
  const shape = graph.nodes.map((n) => n.id).join('|');
  useEffect(() => {
    setNodes((prev) => {
      const byId = new Map(prev.map((n) => [n.id, n]));
      const changed = graph.nodes.some((n) => !byId.has(n.id)) || prev.length !== graph.nodes.length;
      const pos = changed ? layout(graph.nodes.map((n) => byId.get(n.id) ?? n) as AppNode[], graph.edges) : {};
      return graph.nodes.map((n) => {
        const old = byId.get(n.id);
        const position = old && !changed ? old.position : (pos[n.id] ?? old?.position ?? n.position);
        return { ...(old ?? n), data: n.data, type: n.type, position, selected: n.id === selectedId } as AppNode;
      });
    });
  }, [graph, setNodes]); // selection is applied by open(); not a reason to rebuild
  useEffect(() => {
    const t = requestAnimationFrame(() => void flow.fitView({ padding: 0.15, duration: 200 }));
    return () => cancelAnimationFrame(t);
  }, [shape, flow]);

  const autoLayout = useCallback(() => {
    setNodes((cur) => { const p = layout(cur, graph.edges); return cur.map((n) => ({ ...n, position: p[n.id] ?? n.position })); });
    requestAnimationFrame(() => void flow.fitView({ padding: 0.15, duration: 300 }));
  }, [graph.edges, setNodes, flow]);

  const open = useCallback((id: string) => {
    setSelectedId(id);
    setNodes((cur) => cur.map((n) => ({ ...n, selected: n.id === id })));
  }, [setNodes]);
  const ctx = useMemo(() => ({ open }), [open]);

  const conn = selectedId?.startsWith('c:') ? firm.connections.find((c) => `c:${c.id}` === selectedId) ?? null : null;
  const rx = selectedId?.startsWith('rx:') ? selectedId.slice(3) : null;

  return (
    <FlowCtx.Provider value={ctx}>
      <div className="flow-layout">
        <section className="canvas" aria-label={`Delivery graph for ${firm.name}`}>
          <ReactFlow
            nodes={nodes} edges={graph.edges} nodeTypes={nodeTypes}
            onNodesChange={onNodesChange}
            onNodeClick={(_, n) => setSelectedId(n.id)}
            onPaneClick={() => setSelectedId(null)}
            nodesConnectable={false} deleteKeyCode={null}
            colorMode="system" minZoom={0.2} fitView
            proOptions={{ hideAttribution: true }}>
            <Background gap={18} size={1.2} />
            <Controls showInteractive={false}>
              <ControlButton onClick={autoLayout} title="Auto layout" aria-label="Auto layout">⤧</ControlButton>
            </Controls>
            <MiniMap pannable zoomable nodeStrokeWidth={3} style={{ width: 150, height: 100 }}
              nodeColor={(n) => (n.type === 'connection' ? 'var(--kind-endpoint)' : `var(--kind-${n.type ?? 'receiver'})`)} />
          </ReactFlow>
        </section>
        <aside className="side" aria-label="Details">
          {conn ? (
            <div className="stack">
              <div className="panel-head"><h2>{conn.name}</h2><button type="button" className="btn icon" aria-label="Close" onClick={() => setSelectedId(null)}>✕</button></div>
              <ConnectionCard c={conn} kind={catalog?.find((k) => k.kind === conn.kind)} onChanged={reload} />
              <h3>Deliveries</h3>
              <DeliveryList rows={deliveries.filter((d) => d.connectionId === conn.id)} onChanged={reload} />
            </div>
          ) : rx ? (
            <div className="stack">
              <div className="panel-head"><h2>{shortUrl(rx)}</h2><button type="button" className="btn icon" aria-label="Close" onClick={() => setSelectedId(null)}>✕</button></div>
              <p className="url">{rx}</p>
              <p className="muted small">See what the fake receivers saw under Delivery Center → Received.</p>
              <a className="btn" href="#/deliveries?tab=received">Open Received</a>
            </div>
          ) : (
            <div className="stack">
              <div className="panel-head"><h2>{firm.name}</h2><a className="btn" href={`#/firms/${encodeURIComponent(firm.id)}`}>Firm page</a></div>
              <SendLeadForm firm={firm} onSent={reload} />
              <div className="panel-empty">
                <ol className="small">
                  <li>Send a synthetic lead and watch the lines: amber while sending or retrying, green when the receiver accepts, red when it refuses.</li>
                  <li>Select a connection card for its payload preview, test, enable or reactivate.</li>
                  <li><strong>Auto layout</strong> (⤧) tidies the canvas.</li>
                </ol>
              </div>
              <h3>All deliveries</h3>
              <DeliveryList rows={deliveries} showConnection onChanged={reload} />
            </div>
          )}
        </aside>
      </div>
    </FlowCtx.Provider>
  );
}

export default function FlowPage({ firmId }: { firmId: string | null }) {
  const remembered = store.get<string>('lead-integrations:flow-firm');
  const id = firmId ?? remembered;
  useEffect(() => { if (firmId) store.set('lead-integrations:flow-firm', firmId); }, [firmId]);
  useEffect(() => { if (!firmId && remembered) go('/flow', { firm: remembered }); }, [firmId, remembered]);

  const firm = usePoll(() => api.firm(id ?? ''), id ? `firm:${id}` : null);
  const deliveries = usePoll(() => api.deliveries({ firmId: id ?? '', limit: 100 }), id ? `flow-deliveries:${id}` : null);
  const f = firm.data && firm.data.id === id ? firm.data : null;
  const reload = () => { firm.reload(); deliveries.reload(); };

  return (
    <>
      <div className="row between">
        <div>
          <h1>Delivery graph</h1>
          <p className="muted">One lead fans out to each connection. Edges animate while a delivery is pending.</p>
        </div>
        <FirmPicker value={id} onChange={(fid) => go('/flow', { firm: fid })} />
      </div>
      {!id ? <p className="muted">Pick a firm to see its graph.</p>
        : !f || !deliveries.data ? <Loading error={firm.error ?? deliveries.error} />
        : (
          <ReactFlowProvider>
            <Graph key={f.id} firm={f} deliveries={deliveries.data.filter((d) => d.firmId === f.id)} reload={reload} />
          </ReactFlowProvider>
        )}
    </>
  );
}
