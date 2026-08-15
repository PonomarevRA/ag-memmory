import { fetchJson, safeText } from '@shared/fetch.js';

export const ROUTE = '/api/version';
const VERSION_PATTERN = /^[0-9A-Za-z][0-9A-Za-z.+_-]{0,31}$/;
const INFORMATIONAL_PATTERN = /^[0-9A-Za-z][0-9A-Za-z.+_-]{0,63}$/;

export type AppVersion = {
  version: string;
  informationalVersion: string;
};

let cached: AppVersion | null | undefined;

export function resetAppVersionCache(): void {
  cached = undefined;
}

export function safeAppVersion(payload: unknown): AppVersion | null {
  const body = payload as Record<string, unknown> | null;
  const version = safeText(body?.version, 32);
  if (!version || !VERSION_PATTERN.test(version)) return null;
  const informational = safeText(body?.informationalVersion, 64);
  const informationalVersion = informational && INFORMATIONAL_PATTERN.test(informational)
    ? informational
    : version;
  return { version, informationalVersion };
}

export async function loadAppVersion(): Promise<AppVersion | null> {
  if (cached !== undefined) return cached;
  const payload = await fetchJson<unknown>(ROUTE);
  cached = payload ? safeAppVersion(payload) : null;
  return cached;
}
