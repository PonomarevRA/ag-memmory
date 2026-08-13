import { fetchJson } from '@shared/fetch.js';

export const ROUTE = '/api/memory-graph';
export const MAX_NODES = 150;
export const MAX_EDGES = 150;

export type GraphNode = {
  id: string;
  href: string;
  label: string;
  type: string;
  importanceBand: number;
  confidenceBand: number;
  degree: number;
  layoutKey?: string;
};

export type GraphEdge = {
  sourceId: string;
  targetId: string;
  kind: 'SharedEntity';
  weight: number;
};

export type GraphSnapshot = {
  status: string;
  nodes: GraphNode[];
  edges: GraphEdge[];
  nextToken: string | null;
};

export function unavailable(): GraphSnapshot {
  return { status: 'unavailable', nodes: [], edges: [], nextToken: null };
}

function safeNode(node: unknown): GraphNode | null {
  const row = node as Record<string, unknown>;
  if (!row || typeof row.id !== 'string' || typeof row.href !== 'string' || typeof row.type !== 'string') return null;
  if (row.id.length > 80 || row.href.length > 256 || row.type.length > 80 || !row.href.startsWith('/memory-reader/')) return null;
  if (![row.importanceBand, row.confidenceBand, row.degree].every(Number.isFinite)) return null;
  return {
    id: row.id,
    href: row.href,
    label: row.type as string,
    type: row.type as string,
    importanceBand: Math.max(1, Math.min(5, Math.trunc(row.importanceBand as number))),
    confidenceBand: Math.max(1, Math.min(5, Math.trunc(row.confidenceBand as number))),
    degree: Math.max(0, Math.trunc(row.degree as number))
  };
}

/** Caps and deduplicates graph payloads before any renderer sees them. */
export function safeResponse(payload: unknown): GraphSnapshot {
  const body = payload as Record<string, unknown> | null;
  if (!body || body.status !== 'available' || !Array.isArray(body.nodes) || !Array.isArray(body.edges)) return unavailable();

  const nodes: GraphNode[] = [];
  const seenIds = new Set<string>();
  for (const candidate of body.nodes) {
    const node = safeNode(candidate);
    if (!node || seenIds.has(node.id)) continue;
    seenIds.add(node.id);
    nodes.push(node);
    if (nodes.length === MAX_NODES) break;
  }
  for (const node of nodes) node.layoutKey = node.id;

  const ids = new Set(nodes.map(node => node.id));
  const edges: GraphEdge[] = [];
  const edgeKeys = new Set<string>();
  for (const edge of body.edges) {
    const row = edge as Record<string, unknown>;
    if (!row || typeof row.sourceId !== 'string' || typeof row.targetId !== 'string' || row.kind !== 'SharedEntity') continue;
    if (!ids.has(row.sourceId) || !ids.has(row.targetId) || row.sourceId === row.targetId || !Number.isFinite(row.weight)) continue;
    const key = [row.sourceId, row.targetId].sort().join(':');
    if (edgeKeys.has(key)) continue;
    edgeKeys.add(key);
    edges.push({
      sourceId: row.sourceId,
      targetId: row.targetId,
      kind: 'SharedEntity',
      weight: Math.max(1, Math.min(200, Math.trunc(row.weight as number)))
    });
    if (edges.length === MAX_EDGES) break;
  }

  const nextToken = typeof body.nextToken === 'string' && body.nextToken.length <= 4096 ? body.nextToken : null;
  return { status: 'available', nodes, edges, nextToken };
}

export async function load(token?: string | null): Promise<GraphSnapshot> {
  const route = typeof token === 'string' && token.length > 0 ? `${ROUTE}?continuation=${encodeURIComponent(token)}` : ROUTE;
  const payload = await fetchJson<unknown>(route);
  return payload ? safeResponse(payload) : unavailable();
}
