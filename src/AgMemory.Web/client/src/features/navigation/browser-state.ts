const VISITS_KEY = 'agmemory.ui.visit-log.v1';
const THREAD_PREFIX = 'agmemory.ui.thread.v1.';
const PREFERENCES_KEY = 'agmemory.ui.preferences.v1';
export const MAX_VISITS = 100;
export const MAX_MESSAGES = 200;
export const MAX_MESSAGE_LENGTH = 16000;

export type VisitEntry = { route: string; title: string; visitedAt: string };
export type ThreadMessage = { id: string; role: 'user' | 'assistant'; content: string; createdAt: string };
export type Preferences = { theme: 'system' | 'light' | 'dark'; reduceMotion: boolean };

function safeStorage<T>(operation: () => T, fallback: T): T {
  try {
    return operation();
  } catch {
    return fallback;
  }
}

export function sanitizeRoute(route: unknown): string {
  if (typeof route !== 'string') return '/';
  const path = route.split(/[?#]/, 1)[0].trim();
  return path.startsWith('/') ? path : `/${path}`;
}

export function sanitizeThreadId(threadId: unknown): string | null {
  return typeof threadId === 'string' && /^[a-f0-9]{32}$/i.test(threadId) ? threadId.toLowerCase() : null;
}

function readJson<T>(key: string, fallback: T): T {
  return safeStorage(() => {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : fallback;
  }, fallback);
}

function writeJson(key: string, value: unknown): boolean {
  return safeStorage(() => {
    localStorage.setItem(key, JSON.stringify(value));
    return true;
  }, false);
}

export function normalizedVisits(): VisitEntry[] {
  const value = readJson<unknown>(VISITS_KEY, []);
  if (!Array.isArray(value)) return [];
  return value
    .filter(item => {
      const row = item as Record<string, unknown>;
      return row && typeof row.route === 'string' && typeof row.title === 'string' && typeof row.visitedAt === 'string';
    })
    .map(item => {
      const row = item as VisitEntry;
      return { route: sanitizeRoute(row.route), title: row.title.slice(0, 120), visitedAt: row.visitedAt };
    })
    .slice(0, MAX_VISITS);
}

export function trackVisit(route: unknown, title: unknown, scrollPositions: Map<string, { x: number; y: number }>, currentRoute: string): string {
  const nextRoute = sanitizeRoute(route);
  scrollPositions.set(currentRoute, { x: window.scrollX, y: window.scrollY });
  const previous = normalizedVisits().filter(item => item.route !== nextRoute);
  previous.unshift({ route: nextRoute, title: String(title).slice(0, 120), visitedAt: new Date().toISOString() });
  writeJson(VISITS_KEY, previous.slice(0, MAX_VISITS));
  history.replaceState({ ...(history.state ?? {}), agMemoryRoute: nextRoute }, '', location.href);
  return nextRoute;
}

export function loadThread(threadId: unknown): ThreadMessage[] {
  const safeId = sanitizeThreadId(threadId);
  if (!safeId) return [];
  const value = readJson<unknown>(`${THREAD_PREFIX}${safeId}`, []);
  if (!Array.isArray(value)) return [];
  return value
    .filter(item => {
      const row = item as Record<string, unknown>;
      return row && typeof row.id === 'string' && (row.role === 'user' || row.role === 'assistant') && typeof row.content === 'string' && typeof row.createdAt === 'string';
    })
    .map(item => {
      const row = item as ThreadMessage;
      return { ...row, content: row.content.slice(0, MAX_MESSAGE_LENGTH) };
    })
    .slice(-MAX_MESSAGES);
}

export function saveThread(threadId: unknown, messages: unknown): void {
  const safeId = sanitizeThreadId(threadId);
  if (!safeId || !Array.isArray(messages)) return;
  const safeMessages = messages
    .filter(item => {
      const row = item as Record<string, unknown>;
      return row && typeof row.id === 'string' && (row.role === 'user' || row.role === 'assistant') && typeof row.content === 'string' && typeof row.createdAt === 'string';
    })
    .slice(-MAX_MESSAGES)
    .map(item => {
      const row = item as ThreadMessage;
      return { ...row, content: row.content.slice(0, MAX_MESSAGE_LENGTH) };
    });
  writeJson(`${THREAD_PREFIX}${safeId}`, safeMessages);
}

export function getPreferences(): Preferences {
  const value = readJson<Partial<Preferences>>(PREFERENCES_KEY, { theme: 'system', reduceMotion: false });
  const theme = value?.theme && ['system', 'light', 'dark'].includes(value.theme) ? value.theme : 'system';
  return { theme, reduceMotion: value?.reduceMotion === true };
}

export function applyPreferences(preferences: Partial<Preferences>): Preferences {
  const theme = preferences?.theme && ['system', 'light', 'dark'].includes(preferences.theme) ? preferences.theme : 'system';
  const reduceMotion = preferences?.reduceMotion === true;
  if (typeof document !== 'undefined') {
    document.documentElement.dataset.theme = theme;
    document.documentElement.dataset.reduceMotion = String(reduceMotion);
  }
  const normalized = { theme, reduceMotion };
  writeJson(PREFERENCES_KEY, normalized);
  return normalized;
}

export function isStorageAvailable(): boolean {
  return safeStorage(() => {
    const probe = 'agmemory.ui.storage.probe';
    localStorage.setItem(probe, '1');
    localStorage.removeItem(probe);
    return true;
  }, false);
}
