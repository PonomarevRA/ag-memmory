import { createMemoryGraphRenderer } from '/vendor/memory-graph-renderer.js';
import { createMemoryUniverse } from '/vendor/memory-graph-universe.js';

const ROUTE = '/api/memory-graph';
const MAX_NODES = 75;
const MAX_EDGES = 150;
let universe;
let fallbackRenderer;
let primaryCanvas;
let fallbackCanvas;
let selectionSummary;
let currentSnapshot;

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
    const nodes = [];
    const seenIds = new Set();
    for (const candidate of payload.nodes) {
        const node = safeNode(candidate);
        if (!node || seenIds.has(node.id)) continue;
        seenIds.add(node.id);
        nodes.push(node);
        if (nodes.length === MAX_NODES) break;
    }
    // Opaque response IDs are deliberately renewed by the endpoint. A generic type/label ordinal
    // is stable for a structurally identical ordered response and never exposes a durable ID.
    const labelOrdinals = new Map();
    for (const node of nodes) {
        const stem = `${node.type}:${node.label}`;
        const ordinal = labelOrdinals.get(stem) ?? 0;
        labelOrdinals.set(stem, ordinal + 1);
        node.layoutKey = `${stem}:${ordinal}`;
    }
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
    return { status: 'available', nodes, edges };
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
    selectionSummary.textContent = `Выбран ${node.label}: тип ${node.type}, степень ${node.degree}, важность ${node.importanceBand} из 5, уверенность ${node.confidenceBand} из 5. Ближайших соседей: ${neighborCount}.`;
}

function activateFallback() {
    universe?.dispose();
    universe = undefined;
    showFallback();
    fallbackRenderer ??= createMemoryGraphRenderer(fallbackCanvas);
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
    currentSnapshot = safeResponse(snapshot);
    if (universe) {
        try {
            universe.render(currentSnapshot);
            return;
        } catch {
            // A later renderer failure gets the same safe fallback as initialization or context loss.
            activateFallback();
            return;
        }
    }
    fallbackRenderer?.render(currentSnapshot);
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
    else announceFallbackSelection(node);
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
