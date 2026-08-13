import { createMemoryGraphRenderer } from '@vendor/memory-graph-renderer.js';
import { createMemoryUniverse } from '@vendor/memory-graph-universe.js';
import { load, safeResponse, type GraphSnapshot } from './sanitize.js';

type Renderer = {
  render(snapshot: GraphSnapshot, preserveView?: boolean): void;
  zoomIn(): void;
  zoomOut(): void;
  reset(): void;
  selectNode(id: string): void;
  dispose(): void;
};

type Universe = Renderer & { selectNode(id: string): void };

let universe: Universe | undefined;
let fallbackRenderer: Renderer | undefined;
let primaryCanvas: HTMLCanvasElement | undefined;
let fallbackCanvas: HTMLCanvasElement | undefined;
let selectionSummary: HTMLElement | undefined;
let currentSnapshot: GraphSnapshot | undefined;

function announceFallback(): void {
  if (selectionSummary) {
    selectionSummary.textContent =
      'Трёхмерный режим недоступен в этом браузере. Показана совместимая двумерная карта; полное текстовое описание находится ниже.';
  }
}

function announceFallbackSelection(node: GraphSnapshot['nodes'][number]): void {
  if (!selectionSummary || !currentSnapshot) return;
  const neighborCount = currentSnapshot.edges.filter(edge => edge.sourceId === node.id || edge.targetId === node.id).length;
  selectionSummary.replaceChildren(
    document.createTextNode(
      `Выбран ${node.label}: тип ${node.type}, степень ${node.degree}, важность ${node.importanceBand} из 5, уверенность ${node.confidenceBand} из 5. Ближайших соседей: ${neighborCount}. `
    )
  );
  const link = document.createElement('a');
  link.href = node.href;
  link.textContent = 'Открыть запись';
  selectionSummary.append(link);
}

function showPrimary(): void {
  if (!primaryCanvas || !fallbackCanvas) return;
  primaryCanvas.hidden = false;
  primaryCanvas.setAttribute('aria-hidden', 'false');
  fallbackCanvas.hidden = true;
  fallbackCanvas.setAttribute('aria-hidden', 'true');
}

function showFallback(): void {
  if (!primaryCanvas || !fallbackCanvas) return;
  primaryCanvas.hidden = true;
  primaryCanvas.setAttribute('aria-hidden', 'true');
  fallbackCanvas.hidden = false;
  fallbackCanvas.setAttribute('aria-hidden', 'false');
}

function activateFallback(): void {
  universe?.dispose();
  universe = undefined;
  showFallback();
  if (fallbackCanvas) {
    fallbackRenderer ??= createMemoryGraphRenderer(fallbackCanvas, announceFallbackSelection) as Renderer;
  }
  announceFallback();
  if (currentSnapshot?.status === 'available') fallbackRenderer?.render(currentSnapshot);
}

export function initialize(canvas: HTMLCanvasElement, compatibilityCanvas: HTMLCanvasElement, selectedSummary: HTMLElement): void {
  dispose();
  primaryCanvas = canvas;
  fallbackCanvas = compatibilityCanvas;
  selectionSummary = selectedSummary;
  showPrimary();
  try {
    universe = createMemoryUniverse(primaryCanvas, selectionSummary, activateFallback) as Universe;
  } catch {
    activateFallback();
  }
}

export { load };

export function render(snapshot: unknown, preserveView = false): void {
  currentSnapshot = safeResponse(snapshot);
  if (universe) {
    try {
      universe.render(currentSnapshot, preserveView);
      return;
    } catch {
      activateFallback();
      return;
    }
  }
  fallbackRenderer?.render(currentSnapshot, preserveView);
}

export function zoomIn(): void {
  if (universe) universe.zoomIn();
  else fallbackRenderer?.zoomIn();
}

export function zoomOut(): void {
  if (universe) universe.zoomOut();
  else fallbackRenderer?.zoomOut();
}

export function reset(): void {
  if (universe) universe.reset();
  else fallbackRenderer?.reset();
}

export function selectNode(responseLocalOpaqueId: string): void {
  const node = currentSnapshot?.nodes.find(candidate => candidate.id === responseLocalOpaqueId);
  if (!node) return;
  if (universe) universe.selectNode(node.id);
  else fallbackRenderer?.selectNode(node.id);
}

export function dispose(): void {
  universe?.dispose();
  fallbackRenderer?.dispose();
  universe = undefined;
  fallbackRenderer = undefined;
  currentSnapshot = undefined;
  primaryCanvas = undefined;
  fallbackCanvas = undefined;
  selectionSummary = undefined;
}
