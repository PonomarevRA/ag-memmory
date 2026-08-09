const ROUTE = '/api/memory-status';
const MAX_COUNT = 2_000_000_000;

function unavailable() {
    return {
        status: 'unavailable', totalMemoryCount: 0, activeMemoryCount: 0,
        expiredMemoryCount: 0, inactiveMemoryCount: 0, latestUpdateAt: null, activeByType: []
    };
}

function count(value) {
    return Number.isFinite(value) && value >= 0 ? Math.min(MAX_COUNT, Math.trunc(value)) : 0;
}

function timestamp(value) {
    return typeof value === 'string' && !Number.isNaN(Date.parse(value)) ? value : null;
}

function safeResponse(payload) {
    if (!payload || payload.status !== 'available' || !Array.isArray(payload.activeByType)) return unavailable();
    const activeByType = [];
    for (const item of payload.activeByType) {
        if (!item || typeof item.type !== 'string' || item.type.length === 0 || item.type.length > 80 || !Number.isFinite(item.count)) continue;
        activeByType.push({ type: item.type, count: count(item.count) });
        if (activeByType.length === 12) break;
    }
    return {
        status: 'available',
        totalMemoryCount: count(payload.totalMemoryCount),
        activeMemoryCount: count(payload.activeMemoryCount),
        expiredMemoryCount: count(payload.expiredMemoryCount),
        inactiveMemoryCount: count(payload.inactiveMemoryCount),
        latestUpdateAt: timestamp(payload.latestUpdateAt),
        activeByType
    };
}

export async function load() {
    try {
        const response = await fetch(ROUTE, { credentials: 'same-origin', cache: 'no-store' });
        if (!response.ok) return unavailable();
        return safeResponse(await response.json());
    } catch {
        return unavailable();
    }
}
