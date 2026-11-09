import { fetchJson, safeText } from '@shared/fetch.js';

export const ROUTE = '/api/memory-graph';
export const MAX_NODES = 150;
export const MAX_EDGES = 150;

const TYPE_LABELS: Record<string, string> = {
  Fact: 'Факт', Decision: 'Решение', Preference: 'Предпочтение', Task: 'Задача', Event: 'Событие',
  Procedure: 'Процедура', Constraint: 'Ограничение', Incident: 'Инцидент', LessonLearned: 'Урок',
  Observation: 'Наблюдение', Outcome: 'Результат', Summary: 'Сводка', Unknown: 'Запись'
};

const TYPE_COLORS: Record<string, string> = {
  Fact: '#6172f3', Decision: '#b66be5', Preference: '#d97706', Task: '#0d9488', Event: '#db2777',
  Procedure: '#2563eb', Constraint: '#b45309', Incident: '#dc2626', LessonLearned: '#7c3aed',
  Observation: '#0891b2', Outcome: '#059669', Summary: '#475569', Unknown: '#64748b'
};

/** Uses the same fixed local type colors in the renderer and its text legend. */
export function typeColor(type: string): string { return TYPE_COLORS[type] ?? TYPE_COLORS.Unknown; }

/** Names only approved record types; arbitrary server strings never become user-visible labels. */
export function typeLabel(type: string): string { return TYPE_LABELS[type] ?? TYPE_LABELS.Unknown; }

export type GraphNode = {
  id: string; href: string; label: string; type: string; importanceBand: number; confidenceBand: number; degree: number; layoutKey?: string; color?: string;
  alias?: string; subtitle?: string;
};
export type GraphEdge = { sourceId: string; targetId: string; kind: 'SharedEntity'; weight: number };
export type GraphSnapshot = {
  status: string; nodes: GraphNode[]; edges: GraphEdge[]; nextToken: string | null; areaId?: string | null; areaLabel?: string | null; focusId?: string;
};

export function unavailable(status = 'unavailable'): GraphSnapshot {
  return { status, nodes: [], edges: [], nextToken: null, areaId: null, areaLabel: null };
}

/** Produces one privacy-safe node; titles, labels, entity names and other unapproved payload fields are ignored. */
function safeNode(value: unknown): GraphNode | null {
  const row = value as Record<string, unknown> | null;
  if (!row || typeof row.id !== 'string' || !/^[a-zA-Z0-9_-]{1,80}$/.test(row.id)) return null;
  const href = row.href === null ? '' : typeof row.href === 'string' ? row.href : null;
  if (href === null || (href && (href.length > 256 || !/^\/memory-reader\/[a-zA-Z0-9_-]+(?:\?area=[a-z][a-z0-9-]{0,63})?$/.test(href)))) return null;
  if (![row.importanceBand, row.confidenceBand, row.degree].every(Number.isFinite)) return null;
  const type = typeof row.type === 'string' && Object.hasOwn(TYPE_LABELS, row.type) ? row.type : 'Unknown';
  return { id: row.id, href, label: '', type,
    importanceBand: Math.max(1, Math.min(5, Math.trunc(row.importanceBand as number))),
    confidenceBand: Math.max(1, Math.min(5, Math.trunc(row.confidenceBand as number))),
    degree: Math.max(0, Math.min(10000, Math.trunc(row.degree as number))), layoutKey: row.id, color: typeColor(type) };
}

/** Deduplicates bounded undirected edges; only edges with both visible endpoints are usable. */
function boundedEdges(values: unknown[], nodes: GraphNode[]): GraphEdge[] {
  const ids = new Set(nodes.map(node => node.id));
  const edges: GraphEdge[] = [];
  const seen = new Set<string>();
  for (const value of values) {
    const row = value as Record<string, unknown> | null;
    if (!row || typeof row.sourceId !== 'string' || typeof row.targetId !== 'string' || row.kind !== 'SharedEntity' || !Number.isFinite(row.weight)) continue;
    if (!ids.has(row.sourceId) || !ids.has(row.targetId) || row.sourceId === row.targetId) continue;
    const key = [row.sourceId, row.targetId].sort().join(':');
    if (seen.has(key)) continue;
    seen.add(key);
    edges.push({ sourceId: row.sourceId, targetId: row.targetId, kind: 'SharedEntity', weight: Math.max(1, Math.min(200, Math.trunc(row.weight as number))) });
    if (edges.length === MAX_EDGES) break;
  }
  return edges;
}

/** Validates the safe server projection and generates unique aliases local to this response. */
export function safeResponse(payload: unknown): GraphSnapshot {
  const body = payload as Record<string, unknown> | null;
  if (!body || body.status !== 'available' || !Array.isArray(body.nodes) || !Array.isArray(body.edges)) {
    return unavailable(body?.status === 'changed' || body?.status === 'stale' ? body.status : 'unavailable');
  }
  const nodes: GraphNode[] = [];
  const ids = new Set<string>();
  for (const candidate of body.nodes) {
    const node = safeNode(candidate);
    if (!node || ids.has(node.id)) continue;
    ids.add(node.id);
    node.label = `${typeLabel(node.type)} №${nodes.length + 1}`;
    nodes.push(node);
    if (nodes.length === MAX_NODES) break;
  }
  const area = safeText(body.areaId, 64);
  const areaId = area && /^[a-z][a-z0-9-]{0,63}$/.test(area) ? area : null;
  const areaLabel = areaId ? safeText(body.areaLabel, 120) : null;
  const nextToken = typeof body.nextToken === 'string' && body.nextToken.length > 0 && body.nextToken.length <= 4096 ? body.nextToken : null;
  return { status: 'available', nodes, edges: boundedEdges(body.edges, nodes), nextToken, areaId, areaLabel };
}

/** Appends sanitized portions with stable aliases; changed status or area identity never mixes two graphs. */
export function mergePortion(current: GraphSnapshot, page: GraphSnapshot): GraphSnapshot {
  if (page.status !== 'available') return page;
  if ((current.areaId ?? null) !== (page.areaId ?? null)) return unavailable('changed');
  const nodes = [...current.nodes];
  const ids = new Set(nodes.map(node => node.id));
  for (const node of page.nodes) {
    if (ids.has(node.id)) continue;
    if (nodes.length === MAX_NODES) break;
    ids.add(node.id);
    nodes.push({ ...node, label: `${typeLabel(node.type)} №${nodes.length + 1}` });
  }
  return { ...current, nodes, edges: boundedEdges([...current.edges, ...page.edges], nodes), nextToken: page.nextToken };
}

/** Loads one fixed graph portion; the process configuration alone selects its exact scope. */
export async function load(token?: string | null, area?: string | null): Promise<GraphSnapshot> {
  if (area !== null && area !== undefined && !/^[a-z][a-z0-9-]{0,63}$/.test(area)) return unavailable();
  const parameters = new URLSearchParams();
  if (typeof token === 'string' && token.length > 0) parameters.set('continuation', token);
  if (area) parameters.set('area', area);
  const route = parameters.size ? `${ROUTE}?${parameters}` : ROUTE;
  const payload = await fetchJson<unknown>(route);
  return payload ? safeResponse(payload) : unavailable();
}
