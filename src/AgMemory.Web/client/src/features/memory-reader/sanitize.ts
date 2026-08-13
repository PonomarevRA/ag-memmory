import { safeText } from '@shared/fetch.js';

export const HOME_ROUTE = '/api/memory-reader';
export const TREE_ROUTE = '/api/memory-reader/tree';

export const DOCUMENT_STATUS = new Set(['available', 'not-found', 'changed', 'stale', 'unavailable']);
export const CATALOG_STATUS = new Set(['available', 'not-found', 'changed', 'stale', 'unavailable', 'catalog-not-ready']);
export const TREE_STATUS = new Set(['available', 'unavailable', 'catalog-not-ready']);

export const MAX_BLOCKS = 8;
export const MAX_RUNS_PER_BLOCK = 128;

export type MemoryReaderDocument = {
  status: string;
  routeKey: string | null;
  title: string | null;
  namespace: string | null;
  tags: string[];
  children: MemoryReaderRelation[];
  related: MemoryReaderRelation[];
  backlinks: MemoryReaderRelation[];
  blocks: MemoryReaderBlock[];
  nextBlockToken: string | null;
};

export type MemoryReaderRelation = {
  href: string;
  kind: string;
  label: string;
  title: string;
  namespace: string;
  sharedEntityCount: number;
};

export type MemoryReaderBlock = {
  id: string;
  heading: string | null;
  content: MemoryReaderInline[];
};

export type MemoryReaderInline = {
  kind: 'text' | 'link';
  text: string;
  routeKey: string | null;
  blockToken: string | null;
};

export function unavailableDocument(): MemoryReaderDocument {
  return {
    status: 'unavailable',
    routeKey: null,
    title: null,
    namespace: null,
    tags: [],
    children: [],
    related: [],
    backlinks: [],
    blocks: [],
    nextBlockToken: null
  };
}

function safeInline(value: unknown): MemoryReaderInline | null {
  const row = value as Record<string, unknown>;
  const text = safeText(row?.text, 8000);
  if (text === null) return null;
  if (row.kind === 'link') {
    const routeKey = safeText(row.routeKey, 128);
    const blockToken = safeText(row.blockToken, 4096);
    return routeKey && blockToken
      ? { kind: 'link', text, routeKey, blockToken }
      : { kind: 'text', text, routeKey: null, blockToken: null };
  }
  return row?.kind === 'text' ? { kind: 'text', text, routeKey: null, blockToken: null } : null;
}

function safeRelation(value: unknown): MemoryReaderRelation | null {
  const row = value as Record<string, unknown>;
  const href = safeText(row?.href, 256);
  const kind = safeText(row?.kind, 16);
  const label = safeText(row?.label, 160);
  const title = safeText(row?.title, 160);
  const namespace = safeText(row?.namespace, 192);
  const sharedEntityCount =
    Number.isSafeInteger(row?.sharedEntityCount) && (row.sharedEntityCount as number) >= 0 && (row.sharedEntityCount as number) <= 256
      ? (row.sharedEntityCount as number)
      : 0;
  const allowedKind = kind === 'child' || kind === 'related' || kind === 'backlink' ? kind : 'related';
  return href && href.startsWith('/memory-reader/') && label && title && namespace
    ? { href, kind: allowedKind, label, title, namespace, sharedEntityCount }
    : null;
}

function relationList(value: unknown, maximum: number): MemoryReaderRelation[] {
  return Array.isArray(value) ? value.map(safeRelation).filter((item): item is MemoryReaderRelation => item !== null).slice(0, maximum) : [];
}

/** Firebreak: only bounded, opaque reader DTO fields reach Blazor state. */
export function safeDocumentResponse(payload: unknown): MemoryReaderDocument {
  const body = payload as Record<string, unknown> | null;
  if (!body || !DOCUMENT_STATUS.has(String(body.status))) return unavailableDocument();
  if (body.status !== 'available') {
    return { ...unavailableDocument(), status: String(body.status) };
  }

  const routeKey = safeText(body.routeKey, 128);
  const title = safeText(body.title, 160);
  const namespace = safeText(body.namespace, 192);
  if (routeKey === null || title === null || namespace === null || !Array.isArray(body.blocks)) return unavailableDocument();

  const blocks: MemoryReaderBlock[] = [];
  for (const value of body.blocks) {
    const row = value as Record<string, unknown>;
    const id = safeText(row?.id, 4096);
    if (id === null || (row.heading !== null && safeText(row.heading, 512) === null) || !Array.isArray(row.content)) continue;
    const content: MemoryReaderInline[] = [];
    for (const candidate of row.content) {
      const inline = safeInline(candidate);
      if (inline) content.push(inline);
      if (content.length === MAX_RUNS_PER_BLOCK) break;
    }
    blocks.push({ id, heading: typeof row.heading === 'string' ? row.heading : null, content });
    if (blocks.length === MAX_BLOCKS) break;
  }

  const nextBlockToken = body.nextBlockToken === null ? null : safeText(body.nextBlockToken, 4096);
  const tags = Array.isArray(body.tags) ? body.tags.filter(tag => safeText(tag, 48)).slice(0, 16) as string[] : [];

  return {
    status: 'available',
    routeKey,
    title,
    namespace,
    tags,
    children: relationList(body.children, 64),
    related: relationList(body.related, 64),
    backlinks: relationList(body.backlinks, 50),
    blocks,
    nextBlockToken
  };
}

export function isSafeNamespace(value: string): boolean {
  const segments = value.split('/');
  return segments.length >= 1 && segments.length <= 6 && segments.every(segment => /^[a-z0-9][a-z0-9-]{0,31}$/.test(segment));
}

export function isSafeTagLocator(value: string): boolean {
  return /^tag\/[0-9a-f]{64}$/.test(value);
}
