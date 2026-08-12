const HOME_ROUTE = '/api/memory-reader';
const STATUS = new Set(['available', 'not-found', 'changed', 'stale', 'unavailable']);
const CATALOG_STATUS = new Set(['available', 'not-found', 'changed', 'stale', 'unavailable', 'catalog-not-ready']);
const MAX_BLOCKS = 8;
const MAX_RUNS_PER_BLOCK = 128;

function unavailable() {
    return { status: 'unavailable', routeKey: null, title: null, namespace: null, tags: [], children: [], related: [], backlinks: [], blocks: [], nextBlockToken: null };
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
    if (payload.status !== 'available') return { status: payload.status, routeKey: null, title: null, namespace: null, tags: [], children: [], related: [], backlinks: [], blocks: [], nextBlockToken: null };
    const routeKey = safeText(payload.routeKey, 128);
    const title = safeText(payload.title, 160);
    const namespaceValue = safeText(payload.namespace, 192);
    if (routeKey === null || title === null || namespaceValue === null || !Array.isArray(payload.blocks)) return unavailable();
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
    const tags = Array.isArray(payload.tags) ? payload.tags.filter(tag => safeText(tag, 48)).slice(0, 16) : [];
    const safeRelation = value => {
        const href = safeText(value?.href, 256); const label = safeText(value?.label, 160); const relationTitle = safeText(value?.title, 160); const relationNamespace = safeText(value?.namespace, 192);
        const sharedEntityCount = Number.isSafeInteger(value?.sharedEntityCount) && value.sharedEntityCount >= 0 && value.sharedEntityCount <= 256 ? value.sharedEntityCount : 0;
        return href && href.startsWith('/memory-reader/') && label && relationTitle && relationNamespace ? { href, label, title: relationTitle, namespace: relationNamespace, sharedEntityCount } : null;
    };
    const relationList = (value, maximum) => Array.isArray(value) ? value.map(safeRelation).filter(Boolean).slice(0, maximum) : [];
    return { status: 'available', routeKey, title, namespace: namespaceValue, tags, children: relationList(payload.children, 64), related: relationList(payload.related, 64), backlinks: relationList(payload.backlinks, 50), blocks, nextBlockToken };
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

function catalogUnavailable(status = 'unavailable') {
    return { status, documents: [], namespaces: [], tags: [], nextToken: null };
}

function safeFacet(value, isValidLocator) {
    const locator = safeText(value?.locator, 72);
    const label = safeText(value?.label, 80);
    const count = Number.isSafeInteger(value?.count) && value.count > 0 ? value.count : null;
    return locator && isValidLocator(locator) && label && count !== null ? { locator, label, count } : null;
}

function isSafeNamespace(value) {
    const segments = value.split('/');
    return segments.length >= 1 && segments.length <= 6 && segments.every(segment => /^[a-z0-9][a-z0-9-]{0,31}$/.test(segment));
}

function isSafeTagLocator(value) {
    return /^tag\/[0-9a-f]{64}$/.test(value);
}

export async function loadCatalog(token, namespaceValue, tag) {
    const parameters = new URLSearchParams();
    if (typeof token === 'string' && token.length > 0) parameters.set('continuation', token);
    if (typeof namespaceValue === 'string' && namespaceValue.length > 0) parameters.set('namespace', namespaceValue);
    if (typeof tag === 'string' && tag.length > 0) parameters.set('tag', tag);
    const route = parameters.size > 0 ? `${HOME_ROUTE}?${parameters.toString()}` : HOME_ROUTE;
    try {
        const response = await fetch(route, { credentials: 'same-origin', cache: 'no-store' });
        const payload = response.ok ? await response.json() : null;
        if (!payload || !CATALOG_STATUS.has(payload.status)) return catalogUnavailable();
        if (payload.status !== 'available' || !Array.isArray(payload.documents)) return catalogUnavailable(payload.status);
        const documents = payload.documents.flatMap(value => {
            const href = safeText(value?.href, 256);
            const type = safeText(value?.type, 80);
            const title = safeText(value?.title, 160);
            const namespaceValue = safeText(value?.namespace, 192);
            const preview = safeText(value?.preview, 320);
            const tags = Array.isArray(value?.tags) ? value.tags.filter(tag => safeText(tag, 48)).slice(0, 3) : [];
            return href && href.startsWith('/memory-reader/') && type && title && namespaceValue && preview ? [{ href, type, title, namespace: namespaceValue, tags, preview, updatedAt: value.updatedAt }] : [];
        }).slice(0, 20);
        const nextToken = payload.nextToken === null ? null : safeText(payload.nextToken, 4096);
        const namespaces = Array.isArray(payload.namespaces) ? payload.namespaces.map(value => safeFacet(value, isSafeNamespace)).filter(Boolean).slice(0, 50) : [];
        const tags = Array.isArray(payload.tags) ? payload.tags.map(value => safeFacet(value, isSafeTagLocator)).filter(Boolean).slice(0, 50) : [];
        return { status: 'available', documents, namespaces, tags, nextToken };
    } catch {
        return catalogUnavailable();
    }
}
