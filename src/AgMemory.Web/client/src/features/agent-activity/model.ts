export type Cursor = { journalEpoch: string; sequence: string };
export type ActivityEvent = { schemaVersion: 'agent-activity/v1'; eventId: string; sequence: string; journalEpoch: string; occurredAt: string; sessionId: string;
  operationId: string; operation: string; phase: 'started' | 'finished'; clientKind: string; outcome?: string; resultCount?: number;
  durationMs?: number; query?: { preview?: string; mode: string; truncated: boolean }; request?: { tool: string; summary: string }; response?: { summary: string; truncated: boolean }; target?: { nodeKey?: string; generation?: string; status: string } };
export type Preferences = { chatEnabled: boolean; markersEnabled: boolean; expanded: boolean };
export const DEFAULTS: Preferences = { chatEnabled: false, markersEnabled: false, expanded: false };
const MAX_EVENTS = 500;

/** Accepts only bounded public metadata; authority, private content and unexpected text fields are discarded. */
export function safeEvent(value: unknown): ActivityEvent | null {
  if (!value || typeof value !== 'object') return null;
  const row = value as Record<string, unknown>;
  const bounded = (key: string, maximum = 128) => typeof row[key] === 'string' && (row[key] as string).length <= maximum ? row[key] as string : '';
  const eventId = bounded('eventId'); const sequence = bounded('sequence', 20); const journalEpoch = bounded('journalEpoch');
  if (row.schemaVersion !== 'agent-activity/v1' || !eventId || !journalEpoch || !/^(0|[1-9][0-9]*)$/.test(sequence) || !['started', 'finished'].includes(String(row.phase))) return null;
  const query = row.query as Record<string, unknown> | null;
  const request = row.request as Record<string, unknown> | null; const response = row.response as Record<string, unknown> | null;
  const safeSummary = (value: unknown) => typeof value === 'string' ? value.slice(0, 128) : '';
  const target = row.target as Record<string, unknown> | null;
  const nodeKey = target && typeof target.nodeKey === 'string' && /^[a-zA-Z0-9_-]{1,80}$/.test(target.nodeKey) ? target.nodeKey : undefined;
  const generation = target && typeof target.generation === 'string' && /^[a-zA-Z0-9_-]{1,80}$/.test(target.generation) ? target.generation : undefined;
  return { schemaVersion: 'agent-activity/v1', eventId, sequence, journalEpoch, occurredAt: bounded('occurredAt', 64), sessionId: bounded('sessionId'), operationId: bounded('operationId'),
    operation: bounded('operation'), query: query ? { mode: query.mode === 'preview' ? 'preview' : 'omitted', truncated: query.truncated === true, preview: typeof query.preview === 'string' ? query.preview.slice(0, 256) : undefined } : undefined, request: request ? { tool: safeSummary(request.tool), summary: safeSummary(request.summary) } : undefined, response: response ? { summary: safeSummary(response.summary), truncated: response.truncated === true } : undefined, phase: row.phase as ActivityEvent['phase'], clientKind: bounded('clientKind', 64), outcome: bounded('outcome', 32),
    resultCount: typeof row.resultCount === 'number' && Number.isSafeInteger(row.resultCount) && row.resultCount >= 0 ? row.resultCount : undefined,
    durationMs: typeof row.durationMs === 'number' && Number.isFinite(row.durationMs) && row.durationMs >= 0 ? row.durationMs : undefined,
    target: { nodeKey, generation, status: target?.status === 'available' && nodeKey && generation ? 'available' : 'unavailable' } };
}

/** Decimal-string ordering remains exact after JavaScript's safe integer range. */
export function compareSequence(first: string, second: string): number { return first.length - second.length || first.localeCompare(second); }

/** Deduplicates replay and bounds retained UI memory independently of the cyclic disk journal. */
export function reduceEvents(previous: ActivityEvent[], values: unknown[]): ActivityEvent[] {
  const byId = new Map(previous.map(event => [event.eventId, event]));
  for (const value of values.slice(-MAX_EVENTS)) { const event = safeEvent(value); if (event) byId.set(event.eventId, event); }
  return [...byId.values()].sort((first, second) => compareSequence(first.sequence, second.sequence)).slice(-MAX_EVENTS);
}

/** Persists preferences only; no events, raw query, scope or reconnect cursor enter browser storage. */
export function readPreferences(): Preferences {
  try { const value = JSON.parse(localStorage.getItem('agmemory.activity.v1') ?? '{}'); return {
    chatEnabled: value.chatEnabled === true, markersEnabled: value.markersEnabled === true, expanded: value.expanded === true }; }
  catch { return { ...DEFAULTS }; }
}
export function savePreferences(value: Preferences): void { try { localStorage.setItem('agmemory.activity.v1', JSON.stringify(value)); } catch { /* Private browsing may deny storage. */ } }
