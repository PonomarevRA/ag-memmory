const VISITS_KEY = 'agmemory.ui.visit-log.v1';
const THREAD_PREFIX = 'agmemory.ui.thread.v1.';
const PREFERENCES_KEY = 'agmemory.ui.preferences.v1';
const MAX_VISITS = 100;
const MAX_MESSAGES = 200;
const MAX_MESSAGE_LENGTH = 16000;

let currentRoute = '/';
let scrollListener;
let popStateListener;
const scrollPositions = new Map();

function safeStorage(operation, fallback) {
    try {
        return operation();
    } catch {
        return fallback;
    }
}

function sanitizeRoute(route) {
    if (typeof route !== 'string') return '/';
    const path = route.split(/[?#]/, 1)[0].trim();
    return path.startsWith('/') ? path : `/${path}`;
}

function sanitizeThreadId(threadId) {
    return typeof threadId === 'string' && /^[a-f0-9]{32}$/i.test(threadId) ? threadId.toLowerCase() : null;
}

function readJson(key, fallback) {
    return safeStorage(() => {
        const raw = localStorage.getItem(key);
        return raw ? JSON.parse(raw) : fallback;
    }, fallback);
}

function writeJson(key, value) {
    return safeStorage(() => {
        localStorage.setItem(key, JSON.stringify(value));
        return true;
    }, false);
}

function normalizedVisits() {
    const value = readJson(VISITS_KEY, []);
    if (!Array.isArray(value)) return [];

    return value
        .filter(item => item && typeof item.route === 'string' && typeof item.title === 'string' && typeof item.visitedAt === 'string')
        .map(item => ({ route: sanitizeRoute(item.route), title: item.title.slice(0, 120), visitedAt: item.visitedAt }))
        .slice(0, MAX_VISITS);
}

function saveScrollPosition() {
    scrollPositions.set(currentRoute, { x: window.scrollX, y: window.scrollY });
}

export function initialize(route) {
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

export function trackVisit(route, title) {
    const nextRoute = sanitizeRoute(route);
    saveScrollPosition();
    currentRoute = nextRoute;

    const previous = normalizedVisits().filter(item => item.route !== nextRoute);
    previous.unshift({ route: nextRoute, title: String(title).slice(0, 120), visitedAt: new Date().toISOString() });
    writeJson(VISITS_KEY, previous.slice(0, MAX_VISITS));

    // Blazor's router owns pushState for navigations. Replacing metadata here avoids duplicate Back entries.
    history.replaceState({ ...(history.state ?? {}), agMemoryRoute: nextRoute }, '', location.href);
}

export function loadVisits() {
    return normalizedVisits();
}

export function removeVisit(route) {
    const safeRoute = sanitizeRoute(route);
    writeJson(VISITS_KEY, normalizedVisits().filter(item => item.route !== safeRoute));
}

export function clearVisits() {
    safeStorage(() => localStorage.removeItem(VISITS_KEY), undefined);
}

export function loadThread(threadId) {
    const safeId = sanitizeThreadId(threadId);
    if (!safeId) return [];
    const value = readJson(`${THREAD_PREFIX}${safeId}`, []);
    if (!Array.isArray(value)) return [];

    return value
        .filter(item => item && typeof item.id === 'string' && (item.role === 'user' || item.role === 'assistant') && typeof item.content === 'string' && typeof item.createdAt === 'string')
        .map(item => ({ ...item, content: item.content.slice(0, MAX_MESSAGE_LENGTH) }))
        .slice(-MAX_MESSAGES);
}

export function saveThread(threadId, messages) {
    const safeId = sanitizeThreadId(threadId);
    if (!safeId || !Array.isArray(messages)) return;
    const safeMessages = messages
        .filter(item => item && typeof item.id === 'string' && (item.role === 'user' || item.role === 'assistant') && typeof item.content === 'string' && typeof item.createdAt === 'string')
        .slice(-MAX_MESSAGES)
        .map(item => ({ ...item, content: item.content.slice(0, MAX_MESSAGE_LENGTH) }));
    writeJson(`${THREAD_PREFIX}${safeId}`, safeMessages);
}

export function clearThread(threadId) {
    const safeId = sanitizeThreadId(threadId);
    if (safeId) safeStorage(() => localStorage.removeItem(`${THREAD_PREFIX}${safeId}`), undefined);
}

export function getPreferences() {
    const value = readJson(PREFERENCES_KEY, { theme: 'system', reduceMotion: false });
    const theme = ['system', 'light', 'dark'].includes(value?.theme) ? value.theme : 'system';
    return { theme, reduceMotion: value?.reduceMotion === true };
}

export function savePreferences(preferences) {
    const theme = ['system', 'light', 'dark'].includes(preferences?.theme) ? preferences.theme : 'system';
    const reduceMotion = preferences?.reduceMotion === true;
    document.documentElement.dataset.theme = theme;
    document.documentElement.dataset.reduceMotion = String(reduceMotion);
    writeJson(PREFERENCES_KEY, { theme, reduceMotion });
}

export function goBack() {
    if (history.length < 2) return false;
    history.back();
    return true;
}

export function isStorageAvailable() {
    return safeStorage(() => {
        const probe = 'agmemory.ui.storage.probe';
        localStorage.setItem(probe, '1');
        localStorage.removeItem(probe);
        return true;
    }, false);
}

export function dispose() {
    if (scrollListener) window.removeEventListener('scroll', scrollListener);
    if (popStateListener) window.removeEventListener('popstate', popStateListener);
    scrollListener = undefined;
    popStateListener = undefined;
}
