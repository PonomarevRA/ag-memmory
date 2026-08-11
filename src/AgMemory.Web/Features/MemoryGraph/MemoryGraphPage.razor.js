import { createMemoryGraphRenderer } from '/vendor/memory-graph-renderer.js';
import { createMemoryUniverse } from '/vendor/memory-graph-universe.js';

const ROUTE = '/api/memory-graph';
const MAX_NODES = 150;
const MAX_EDGES = 150;
let universe;
let fallbackRenderer;
let primaryCanvas;
let fallbackCanvas;
let selectionSummary;
let currentSnapshot;

function unavailable() {
    return { status: 'unavailable', nodes: [], edges: [], nextToken: null };
}

function safeNode(node) {
    if (!node || typeof node.id !== 'string' || typeof node.href !== 'string' || typeof node.type !== 'string') return null;
    if (node.id.length > 80 || node.href.length > 256 || node.type.length > 80 || !node.href.startsWith('/memory-reader/')) return null;
    if (![node.importanceBand, node.confidenceBand, node.degree].every(Number.isFinite)) return null;
    return {
        id: node.id,
        href: node.href,
        label: node.type,
        type: node.type,
        importanceBand: Math.max(1, Math.min(5, Math.trunc(node.importanceBand))),
        confidenceBand: Math.max(1, Math.min(5, Math.trunc(node.confidenceBand))),
        degree: Math.max(0, Math.trunc(node.degree))
    };
}

function safeResponse(payload) {
    if (!payload || payload.status !== 'available' || !Array.isArray(payload.nodes) || !Array.isArray(payload.edges)) return unavailable();
    const nodes = [];
    const seenIds = new Set();
    for (const candidate of payload.nodes) {
        const node = safeNode(candidate);
        if (!node || seenIds.has(node.id)) continue;
        seenIds.add(node.id);
        nodes.push(node);
        if (nodes.length === MAX_NODES) break;
    }
    for (const node of nodes) node.layoutKey = node.id;
    const ids = new Set(nodes.map(node => node.id));
    const edges = [];
    const edgeKeys = new Set();
    for (const edge of payload.edges) {
        if (!edge || typeof edge.sourceId !== 'string' || typeof edge.targetId !== 'string' || edge.kind !== 'SharedEntity' ||
            !ids.has(edge.sourceId) || !ids.has(edge.targetId) || edge.sourceId === edge.targetId || !Number.isFinite(edge.weight)) continue;
        const key = [edge.sourceId, edge.targetId].sort().join(':');
        if (edgeKeys.has(key)) continue;
        edgeKeys.add(key);
        edges.push({
            sourceId: edge.sourceId,
            targetId: edge.targetId,
            kind: 'SharedEntity',
            weight: Math.max(1, Math.min(200, Math.trunc(edge.weight)))
        });
        if (edges.length === MAX_EDGES) break;
    }
    return { status: 'available', nodes, edges, nextToken: typeof payload.nextToken === 'string' && payload.nextToken.length <= 4096 ? payload.nextToken : null };
}

function showPrimary() {
    primaryCanvas.hidden = false;
    primaryCanvas.setAttribute('aria-hidden', 'false');
    fallbackCanvas.hidden = true;
    fallbackCanvas.setAttribute('aria-hidden', 'true');
}

function showFallback() {
    primaryCanvas.hidden = true;
    primaryCanvas.setAttribute('aria-hidden', 'true');
    fallbackCanvas.hidden = false;
    fallbackCanvas.setAttribute('aria-hidden', 'false');
}

function announceFallback() {
    if (selectionSummary) {
        selectionSummary.textContent = 'Трёхмерный режим недоступен в этом браузере. Показана совместимая двумерная карта; полное текстовое описание находится ниже.';
    }
}

function announceFallbackSelection(node) {
    if (!selectionSummary || !currentSnapshot) return;
    const neighborCount = currentSnapshot.edges.filter(edge => edge.sourceId === node.id || edge.targetId === node.id).length;
    selectionSummary.replaceChildren(document.createTextNode(`Выбран ${node.label}: тип ${node.type}, степень ${node.degree}, важность ${node.importanceBand} из 5, уверенность ${node.confidenceBand} из 5. Ближайших соседей: ${neighborCount}. `));
    const link = document.createElement('a');
    link.href = node.href;
    link.textContent = 'Открыть запись';
    selectionSummary.append(link);
}

function activateFallback() {
    universe?.dispose();
    universe = undefined;
    showFallback();
    fallbackRenderer ??= createMemoryGraphRenderer(fallbackCanvas, announceFallbackSelection);
    announceFallback();
    if (currentSnapshot?.status === 'available') fallbackRenderer.render(currentSnapshot);
}

export function initialize(canvas, compatibilityCanvas, selectedSummary) {
    dispose();
    primaryCanvas = canvas;
    fallbackCanvas = compatibilityCanvas;
    selectionSummary = selectedSummary;
    showPrimary();
    try {
        universe = createMemoryUniverse(primaryCanvas, selectionSummary, activateFallback);
    } catch {
        // Unsupported WebGL and renderer setup failures use the compatibility canvas without exposing details.
        activateFallback();
    }
}

export async function load(token) {
    try {
        const route = typeof token === 'string' && token.length > 0 ? `${ROUTE}?continuation=${encodeURIComponent(token)}` : ROUTE;
        const response = await fetch(route, { credentials: 'same-origin', cache: 'no-store' });
        if (!response.ok) return unavailable();
        return safeResponse(await response.json());
    } catch {
        return unavailable();
    }
}

export function render(snapshot, preserveView = false) {
    currentSnapshot = safeResponse(snapshot);
    if (universe) {
        try {
            universe.render(currentSnapshot, preserveView);
            return;
        } catch {
            // A later renderer failure gets the same safe fallback as initialization or context loss.
            activateFallback();
            return;
        }
    }
    fallbackRenderer?.render(currentSnapshot, preserveView);
}

export function zoomIn() {
    if (universe) universe.zoomIn();
    else fallbackRenderer?.zoomIn();
}

export function zoomOut() {
    if (universe) universe.zoomOut();
    else fallbackRenderer?.zoomOut();
}

export function reset() {
    if (universe) universe.reset();
    else fallbackRenderer?.reset();
}

export function selectNode(responseLocalOpaqueId) {
    const node = currentSnapshot?.nodes.find(candidate => candidate.id === responseLocalOpaqueId);
    if (!node) return;
    if (universe) universe.selectNode(node.id);
    else fallbackRenderer?.selectNode(node.id);
}

export function dispose() {
    universe?.dispose();
    fallbackRenderer?.dispose();
    universe = undefined;
    fallbackRenderer = undefined;
    currentSnapshot = undefined;
    primaryCanvas = undefined;
    fallbackCanvas = undefined;
    selectionSummary = undefined;
}
