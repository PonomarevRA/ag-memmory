import { fetchJson } from '@shared/fetch.js';
import {
  CATALOG_STATUS,
  HOME_ROUTE,
  safeDocumentResponse,
  TREE_ROUTE,
  TREE_STATUS,
  unavailableDocument,
  isSafeNamespace,
  isSafeTagLocator,
  type MemoryReaderDocument
} from './sanitize.js';
import { safeText } from '@shared/fetch.js';

export async function load(routeKey?: string | null, token?: string | null, area?: string | null): Promise<MemoryReaderDocument> {
  const route =
    typeof routeKey === 'string' && routeKey.length > 0
      ? `/api/memory-reader/${encodeURIComponent(routeKey)}${typeof token === 'string' && token.length > 0 ? `?block=${encodeURIComponent(token)}` : ''}`
      : HOME_ROUTE;
  const payload = await fetchJson<unknown>(withArea(route, area));
  return payload ? safeDocumentResponse(payload) : unavailableDocument();
}

export type ReaderArea = { id: string; label: string; isDefault: boolean };
export async function loadAreas(): Promise<ReaderArea[]> {
  const payload = await fetchJson<Record<string, unknown>>('/api/memory-reader/areas');
  if (!payload || payload.status !== 'available' || !Array.isArray(payload.areas)) return [];
  return payload.areas.flatMap(value => {
    const row = value as Record<string, unknown>;
    const id = safeText(row.id, 64); const label = safeText(row.label, 120);
    return id && /^[a-z][a-z0-9-]{0,63}$/.test(id) && label && typeof row.isDefault === 'boolean' ? [{ id, label, isDefault: row.isDefault }] : [];
  }).slice(0, 50);
}

function withArea(route: string, area?: string | null): string {
  if (!area) return route;
  const separator = route.includes('?') ? '&' : '?';
  return `${route}${separator}area=${encodeURIComponent(area)}`;
}

export type CatalogResponse = {
  status: string;
  documents: Array<Record<string, unknown>>;
  namespaces: Array<{ locator: string; label: string; count: number }>;
  tags: Array<{ locator: string; label: string; count: number }>;
  nextToken: string | null;
};

function catalogUnavailable(status = 'unavailable'): CatalogResponse {
  return { status, documents: [], namespaces: [], tags: [], nextToken: null };
}

function safeFacet(value: unknown, isValidLocator: (locator: string) => boolean) {
  const row = value as Record<string, unknown>;
  const locator = safeText(row?.locator, 72);
  const label = safeText(row?.label, 80);
  const count = Number.isSafeInteger(row?.count) && (row.count as number) > 0 ? (row.count as number) : null;
  return locator && isValidLocator(locator) && label && count !== null ? { locator, label, count } : null;
}

export async function loadCatalog(token?: string | null, namespaceValue?: string | null, tag?: string | null, search?: string | null, area?: string | null): Promise<CatalogResponse> {
  const parameters = new URLSearchParams();
  if (typeof token === 'string' && token.length > 0) parameters.set('continuation', token);
  if (typeof namespaceValue === 'string' && namespaceValue.length > 0) parameters.set('namespace', namespaceValue);
  if (typeof tag === 'string' && tag.length > 0) parameters.set('tag', tag);
  if (typeof search === 'string' && search.length > 0) parameters.set('search', search);
  if (typeof area === 'string' && area.length > 0) parameters.set('area', area);
  const route = parameters.size > 0 ? `${HOME_ROUTE}?${parameters.toString()}` : HOME_ROUTE;
  const payload = await fetchJson<Record<string, unknown>>(route);
  if (!payload || !CATALOG_STATUS.has(String(payload.status))) return catalogUnavailable();
  if (payload.status !== 'available' || !Array.isArray(payload.documents)) return catalogUnavailable(String(payload.status));

  const documents = payload.documents.flatMap(value => {
    const row = value as Record<string, unknown>;
    const href = safeText(row?.href, 256);
    const type = safeText(row?.type, 80);
    const title = safeText(row?.title, 160);
    const namespace = safeText(row?.namespace, 192);
    const preview = safeText(row?.preview, 320);
    const tags = Array.isArray(row?.tags) ? row.tags.filter(item => safeText(item, 48)).slice(0, 3) : [];
    return href && href.startsWith('/memory-reader/') && type && title && namespace && preview
      ? [{ href, type, title, namespace, tags, preview, updatedAt: row.updatedAt }]
      : [];
  }).slice(0, 20);

  const nextToken = payload.nextToken === null ? null : safeText(payload.nextToken, 4096);
  const namespaces = Array.isArray(payload.namespaces)
    ? payload.namespaces.map(item => safeFacet(item, isSafeNamespace)).filter(Boolean).slice(0, 50) as CatalogResponse['namespaces']
    : [];
  const tags = Array.isArray(payload.tags)
    ? payload.tags.map(item => safeFacet(item, isSafeTagLocator)).filter(Boolean).slice(0, 50) as CatalogResponse['tags']
    : [];

  return { status: 'available', documents, namespaces, tags, nextToken };
}

export type TreeNode = {
  kind: string;
  label: string;
  locator: string | null;
  href: string | null;
  linkWeight: number | null;
  itemCount: number | null;
  children: TreeNode[];
  nodeKey: string | null;
  parentKey: string | null;
  depth: number;
};

function treeUnavailable(status = 'unavailable'): { status: string; roots: TreeNode[]; nextToken: string | null } {
  return { status, roots: [], nextToken: null };
}

function safeTreeNode(value: unknown, depth: number): TreeNode | null {
  if (depth > 8) return null;
  const row = value as Record<string, unknown>;
  const kind = row?.kind === 'namespace' || row?.kind === 'type' || row?.kind === 'document' ? row.kind : null;
  const label = safeText(row?.label, 160);
  if (!kind || !label) return null;
  const locator = kind !== 'document' ? safeText(row?.locator, 192) : null;
  const href = kind === 'document' ? safeText(row?.href, 256) : null;
  if (kind === 'namespace' && (!locator || !isSafeNamespace(locator))) return null;
  if (kind === 'document' && (!href || !href.startsWith('/memory-reader/'))) return null;
  const linkWeight =
    Number.isSafeInteger(row?.linkWeight) && (row.linkWeight as number) >= 0 && (row.linkWeight as number) <= 256
      ? (row.linkWeight as number)
      : null;
  const itemCount =
    Number.isSafeInteger(row?.itemCount) && (row.itemCount as number) >= 0 && (row.itemCount as number) <= 10000
      ? (row.itemCount as number)
      : null;
  const children = Array.isArray(row?.children)
    ? row.children.map(child => safeTreeNode(child, depth + 1)).filter((child): child is TreeNode => child !== null).slice(0, 256)
    : [];
  const nodeKey = safeText(row?.nodeKey, 512);
  const parentKey = row?.parentKey === null ? null : safeText(row?.parentKey, 512);
  const nodeDepth = Number.isSafeInteger(row?.depth) && (row.depth as number) >= 0 && (row.depth as number) <= 8 ? row.depth as number : depth;
  return { kind, label, locator, href, linkWeight, itemCount, children, nodeKey, parentKey, depth: nodeDepth };
}

export async function loadTree(token?: string | null, area?: string | null): Promise<{ status: string; roots: TreeNode[]; nextToken: string | null }> {
  const payload = await fetchJson<Record<string, unknown>>(withArea(token ? `${TREE_ROUTE}?continuation=${encodeURIComponent(token)}` : TREE_ROUTE, area));
  if (!payload || !TREE_STATUS.has(String(payload.status))) return treeUnavailable();
  if (payload.status !== 'available' || !Array.isArray(payload.roots)) return treeUnavailable(String(payload.status));
  const roots = payload.roots.map(node => safeTreeNode(node, 0)).filter((node): node is TreeNode => node !== null).slice(0, 256);
  return { status: 'available', roots, nextToken: payload.nextToken === null ? null : safeText(payload.nextToken, 4096) };
}

export async function loadTags(token?: string | null, area?: string | null): Promise<{ status: string; tags: CatalogResponse['tags']; nextToken: string | null }> {
  const payload = await fetchJson<Record<string, unknown>>(withArea(token ? `/api/memory-reader/tags?continuation=${encodeURIComponent(token)}` : '/api/memory-reader/tags', area));
  if (!payload || payload.status !== 'available' || !Array.isArray(payload.tags)) return { status: 'unavailable', tags: [], nextToken: null };
  return { status: 'available', tags: payload.tags.map(item => safeFacet(item, isSafeTagLocator)).filter(Boolean).slice(0, 12) as CatalogResponse['tags'], nextToken: payload.nextToken === null ? null : safeText(payload.nextToken, 4096) };
}
