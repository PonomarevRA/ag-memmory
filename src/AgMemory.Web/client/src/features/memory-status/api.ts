import { fetchJson, safeCount, safeTimestamp } from '@shared/fetch.js';

export const ROUTE = '/api/memory-status';
export const MAX_TYPE_ROWS = 12;

export type MemoryStatusResponse = {
  status: string;
  totalMemoryCount: number;
  activeMemoryCount: number;
  expiredMemoryCount: number;
  inactiveMemoryCount: number;
  latestUpdateAt: string | null;
  activeByType: Array<{ type: string; count: number }>;
  lastInjectHitCount: number;
  readerAligned: boolean;
};

export function unavailable(): MemoryStatusResponse {
  return {
    status: 'unavailable',
    totalMemoryCount: 0,
    activeMemoryCount: 0,
    expiredMemoryCount: 0,
    inactiveMemoryCount: 0,
    latestUpdateAt: null,
    activeByType: [],
    lastInjectHitCount: 0,
    readerAligned: false
  };
}

/** Projects the server DTO into a bounded, browser-safe shape. */
export function safeResponse(payload: unknown): MemoryStatusResponse {
  const body = payload as Record<string, unknown> | null;
  if (!body || body.status !== 'available' || !Array.isArray(body.activeByType)) return unavailable();

  const activeByType: MemoryStatusResponse['activeByType'] = [];
  for (const item of body.activeByType) {
    const row = item as Record<string, unknown>;
    const type = typeof row.type === 'string' && row.type.length > 0 && row.type.length <= 80 ? row.type : null;
    if (!type || !Number.isFinite(row.count)) continue;
    activeByType.push({ type, count: safeCount(row.count) });
    if (activeByType.length === MAX_TYPE_ROWS) break;
  }

  return {
    status: 'available',
    totalMemoryCount: safeCount(body.totalMemoryCount),
    activeMemoryCount: safeCount(body.activeMemoryCount),
    expiredMemoryCount: safeCount(body.expiredMemoryCount),
    inactiveMemoryCount: safeCount(body.inactiveMemoryCount),
    latestUpdateAt: safeTimestamp(body.latestUpdateAt),
    activeByType,
    lastInjectHitCount: safeCount(body.lastInjectHitCount),
    readerAligned: body.readerAligned === true
  };
}

export async function load(): Promise<MemoryStatusResponse> {
  const payload = await fetchJson<unknown>(ROUTE);
  return payload ? safeResponse(payload) : unavailable();
}
