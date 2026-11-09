/** Bounded same-origin JSON GET used by diagnostic pages. */
export async function fetchJson<T>(route: string): Promise<T | null> {
  try {
    const response = await fetch(route, { credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok) return null;
    return (await response.json()) as T;
  } catch {
    return null;
  }
}

/** Returns a trimmed string when within the limit; otherwise null (reject unknown shapes). */
export function safeText(value: unknown, maximum: number): string | null {
  return typeof value === 'string' && value.length <= maximum ? value : null;
}

/** Clamps server counts to non-negative integers with an upper safety bound. */
export function safeCount(value: unknown, maximum = 2_000_000_000): number {
  return Number.isFinite(value) && (value as number) >= 0
    ? Math.min(maximum, Math.trunc(value as number))
    : 0;
}

/** Accepts ISO timestamps only; rejects malformed strings. */
export function safeTimestamp(value: unknown): string | null {
  return typeof value === 'string' && !Number.isNaN(Date.parse(value)) ? value : null;
}
