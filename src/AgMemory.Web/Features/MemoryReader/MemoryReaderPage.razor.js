const HOME_ROUTE = '/api/memory-reader';
const STATUS = new Set(['available', 'not-found', 'changed', 'stale', 'unavailable']);
const CATALOG_STATUS = new Set(['available', 'not-found', 'changed', 'stale', 'unavailable', 'catalog-not-ready']);
const MAX_BLOCKS = 8;
const MAX_RUNS_PER_BLOCK = 128;

function unavailable() {
    return { status: 'unavailable', routeKey: null, blocks: [], nextBlockToken: null };
}

function safeText(value, maximum) {
    return typeof value === 'string' && value.length <= maximum ? value : null;
}

function safeInline(value) {
    const text = safeText(value?.text, 8000);
    if (text === null) return null;
    if (value.kind === 'link') {
        const routeKey = safeText(value.routeKey, 128);
        const blockToken = safeText(value.blockToken, 4096);
        return routeKey && blockToken ? { kind: 'link', text, routeKey, blockToken } : { kind: 'text', text, routeKey: null, blockToken: null };
    }
    return value?.kind === 'text' ? { kind: 'text', text, routeKey: null, blockToken: null } : null;
}

function safeResponse(payload) {
    if (!payload || !STATUS.has(payload.status)) return unavailable();
    if (payload.status !== 'available') return { status: payload.status, routeKey: null, blocks: [], nextBlockToken: null };
    const routeKey = safeText(payload.routeKey, 128);
    if (routeKey === null || !Array.isArray(payload.blocks)) return unavailable();
    const blocks = [];
    for (const value of payload.blocks) {
        const id = safeText(value?.id, 4096);
        if (id === null || (value.heading !== null && safeText(value.heading, 512) === null) || !Array.isArray(value.content)) continue;
        const content = [];
        for (const candidate of value.content) {
            const inline = safeInline(candidate);
            if (inline) content.push(inline);
            if (content.length === MAX_RUNS_PER_BLOCK) break;
        }
        blocks.push({ id, heading: value.heading, content });
        if (blocks.length === MAX_BLOCKS) break;
    }
    const nextBlockToken = payload.nextBlockToken === null ? null : safeText(payload.nextBlockToken, 4096);
    return { status: 'available', routeKey, blocks, nextBlockToken };
}

export async function load(routeKey, token) {
    const route = typeof routeKey === 'string' && routeKey.length > 0
        ? `/api/memory-reader/${encodeURIComponent(routeKey)}${typeof token === 'string' && token.length > 0 ? `?block=${encodeURIComponent(token)}` : ''}`
        : HOME_ROUTE;
    try {
        const response = await fetch(route, { credentials: 'same-origin', cache: 'no-store' });
        return response.ok ? safeResponse(await response.json()) : unavailable();
    } catch {
        return unavailable();
    }
}

export async function loadCatalog(token) {
    const route = typeof token === 'string' && token.length > 0
        ? `${HOME_ROUTE}?continuation=${encodeURIComponent(token)}`
        : HOME_ROUTE;
    try {
        const response = await fetch(route, { credentials: 'same-origin', cache: 'no-store' });
        const payload = response.ok ? await response.json() : null;
        if (!payload || !CATALOG_STATUS.has(payload.status)) return { status: 'unavailable', documents: [], nextToken: null };
        if (payload.status !== 'available' || !Array.isArray(payload.documents)) return { status: payload.status, documents: [], nextToken: null };
        const documents = payload.documents.flatMap(value => {
            const href = safeText(value?.href, 256);
            const type = safeText(value?.type, 80);
            const preview = safeText(value?.preview, 320);
            return href && href.startsWith('/memory-reader/') && type && preview ? [{ href, type, preview, updatedAt: value.updatedAt }] : [];
        }).slice(0, 20);
        const nextToken = payload.nextToken === null ? null : safeText(payload.nextToken, 4096);
        return { status: 'available', documents, nextToken };
    } catch {
        return { status: 'unavailable', documents: [], nextToken: null };
    }
}
