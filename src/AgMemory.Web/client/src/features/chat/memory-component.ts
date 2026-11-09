/** A bounded preview exposed by the gateway. It conveys evidence, never browser authority. */
export type MemoryComponent = { type: string; title: string; preview: string; href?: string; capturedAt?: string; revision?: number; sourceState?: 'available' | 'unavailable' };
export const MAX_MEMORY_COMPONENTS = 12;

/** Accepts only server-issued source revalidation links; no external hosts, fragments, arbitrary parameters or authority values. */
function safeSourceHref(value: unknown): string | undefined {
  if (typeof value !== 'string' || value.length > 4096) return undefined;
  const match = /^\/api\/chat-memory\/sources\/[A-Za-z0-9_-]+$/.exec(value);
  return match ? value : undefined;
}

/** Drops unknown/local-storage fields rather than carrying provider credentials or unsanitized links forward. */
export function safeMemoryComponent(value: unknown): MemoryComponent | null {
  if (!value || typeof value !== 'object') return null;
  const row = value as Record<string, unknown>;
  if (typeof row.type !== 'string' || typeof row.title !== 'string' || typeof row.preview !== 'string') return null;
  const type = row.type.trim().slice(0, 80);
  const title = row.title.trim().slice(0, 160);
  const preview = row.preview.slice(0, 320);
  if (!type || !title) return null;
  const result: MemoryComponent = { type, title, preview };
  const href = safeSourceHref(row.href);
  if (href) result.href = href;
  if (typeof row.capturedAt === 'string' && row.capturedAt.length <= 40 && /^\d{4}-\d{2}-\d{2}T/.test(row.capturedAt) && !Number.isNaN(Date.parse(row.capturedAt))) result.capturedAt = row.capturedAt;
  if (Number.isSafeInteger(row.revision) && (row.revision as number) > 0) result.revision = row.revision as number;
  if (row.sourceState === 'available' || row.sourceState === 'unavailable') result.sourceState = row.sourceState;
  return result;
}

/** Keeps a bounded set of safe per-response previews on both storage reads and writes. */
export function safeMemoryComponents(value: unknown): MemoryComponent[] {
  if (!Array.isArray(value)) return [];
  const result: MemoryComponent[] = [];
  for (const item of value) {
    const safe = safeMemoryComponent(item);
    if (safe) result.push(safe);
    if (result.length >= MAX_MEMORY_COMPONENTS) break;
  }
  return result;
}
