import {
  applyPreferences,
  getPreferences,
  isStorageAvailable,
  loadThread,
  normalizedVisits,
  sanitizeRoute,
  saveThread,
  trackVisit as persistVisit
} from './browser-state.js';

export type VisitEntry = { route: string; title: string; visitedAt: string };

let currentRoute = '/';
let scrollListener: (() => void) | undefined;
let popStateListener: (() => void) | undefined;
const scrollPositions = new Map<string, { x: number; y: number }>();

function saveScrollPosition(): void {
  scrollPositions.set(currentRoute, { x: window.scrollX, y: window.scrollY });
}

/** Restores scroll positions and wires popstate for Blazor router coexistence. */
export function initialize(route: unknown): void {
  currentRoute = sanitizeRoute(route);
  const state = history.state ?? {};
  history.replaceState({ ...state, agMemoryRoute: currentRoute }, '', location.href);

  scrollListener = () => saveScrollPosition();
  popStateListener = () => {
    currentRoute = sanitizeRoute(location.pathname);
    const position = scrollPositions.get(currentRoute);
    if (position) requestAnimationFrame(() => window.scrollTo(position.x, position.y));
  };

  window.addEventListener('scroll', scrollListener, { passive: true });
  window.addEventListener('popstate', popStateListener);
}

export function trackVisit(route: unknown, title: unknown): void {
  currentRoute = persistVisit(route, title, scrollPositions, currentRoute);
}

export function loadVisits(): VisitEntry[] {
  return normalizedVisits();
}

export function removeVisit(route: unknown): void {
  const safeRoute = sanitizeRoute(route);
  localStorage.setItem('agmemory.ui.visit-log.v1', JSON.stringify(normalizedVisits().filter(item => item.route !== safeRoute)));
}

export function clearVisits(): void {
  try {
    localStorage.removeItem('agmemory.ui.visit-log.v1');
  } catch {
    // Private mode or disabled storage.
  }
}

export { loadThread, saveThread, getPreferences };

export function savePreferences(preferences: Partial<{ theme: string; reduceMotion: boolean }>): ReturnType<typeof applyPreferences> {
  return applyPreferences(preferences);
}

export function goBack(): boolean {
  if (history.length < 2) return false;
  history.back();
  return true;
}

export { isStorageAvailable };

export function dispose(): void {
  if (scrollListener) window.removeEventListener('scroll', scrollListener);
  if (popStateListener) window.removeEventListener('popstate', popStateListener);
  scrollListener = undefined;
  popStateListener = undefined;
}
