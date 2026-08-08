import { createMemoryGraphRenderer } from '/vendor/memory-graph-renderer.js';

const ROUTE = '/api/memory-graph';
let renderer;

function unavailable() {
    return { status: 'unavailable', nodes: [], edges: [] };
}

function safeNode(node) {
    if (!node || typeof node.id !== 'string' || typeof node.label !== 'string' || typeof node.type !== 'string') return null;
    if (node.id.length > 80 || node.label.length > 120 || node.type.length > 80) return null;
    if (![node.importanceBand, node.confidenceBand, node.degree].every(Number.isFinite)) return null;
    return {
        id: node.id,
        label: node.label,
        type: node.type,
        importanceBand: Math.max(1, Math.min(5, Math.trunc(node.importanceBand))),
        confidenceBand: Math.max(1, Math.min(5, Math.trunc(node.confidenceBand))),
        degree: Math.max(0, Math.trunc(node.degree))
    };
}

function safeResponse(payload) {
    if (!payload || payload.status !== 'available' || !Array.isArray(payload.nodes) || !Array.isArray(payload.edges)) return unavailable();
    const nodes = payload.nodes.map(safeNode).filter(Boolean);
    const ids = new Set(nodes.map(node => node.id));
    const edges = payload.edges
        .filter(edge => edge && typeof edge.sourceId === 'string' && typeof edge.targetId === 'string' && typeof edge.kind === 'string' &&
            ids.has(edge.sourceId) && ids.has(edge.targetId) && Number.isFinite(edge.weight))
        .map(edge => ({
            sourceId: edge.sourceId,
            targetId: edge.targetId,
            kind: edge.kind.slice(0, 80),
            weight: Math.max(1, Math.trunc(edge.weight))
        }));
    return { status: 'available', nodes, edges };
}

export function initialize(canvas) {
    renderer?.dispose();
    renderer = createMemoryGraphRenderer(canvas);
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

export function render(snapshot) {
    renderer?.render(safeResponse(snapshot));
}

export function zoomIn() {
    renderer?.zoomIn();
}

export function zoomOut() {
    renderer?.zoomOut();
}

export function reset() {
    renderer?.reset();
}

export function dispose() {
    renderer?.dispose();
    renderer = undefined;
}
