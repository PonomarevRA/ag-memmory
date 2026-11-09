import { createMemoryGraphRenderer } from '@vendor/memory-graph-renderer.js';
import type { GraphNode, GraphSnapshot } from './sanitize.js';
export { load } from './sanitize.js';

export type GraphView = '2d' | '3d';
type Renderer = { activityPositions?(ids: string[]): Array<{ id: string; x: number; y: number }>; render(snapshot: GraphSnapshot, preserveView?: boolean): void; zoomIn(): void; zoomOut(): void; reset(): void; selectNode(id: string): void; dispose(): void };
let flat: Renderer | undefined;
let universe: Renderer | undefined;
let primaryCanvas: HTMLCanvasElement | undefined;
let flatCanvas: HTMLCanvasElement | undefined;
let selectionSummary: HTMLElement | undefined;
let onSelection: ((node?: GraphNode) => void) | undefined;
let onView: ((view: GraphView, unavailable?: boolean) => void) | undefined;
let snapshot: GraphSnapshot | undefined;
let view: GraphView = '2d';
let selectedId: string | undefined;
let epoch = 0;

/** Initializes the readable 2D view; the optional WebGL module is downloaded only on an explicit switch. */
export function initialize(canvas: HTMLCanvasElement, compatibilityCanvas: HTMLCanvasElement, summary: HTMLElement,
  selectionChanged?: (node?: GraphNode) => void, viewChanged?: (view: GraphView, unavailable?: boolean) => void): void {
  dispose();
  primaryCanvas = canvas; flatCanvas = compatibilityCanvas; selectionSummary = summary;
  onSelection = selectionChanged; onView = viewChanged;
  flat = createMemoryGraphRenderer(compatibilityCanvas, notifySelection);
  show('2d');
}

/** Keeps response-local selection shared between renderers and the accessible inspector. */
function notifySelection(node?: GraphNode): void { selectedId = node?.id; onSelection?.(node); }

/** Updates renderer visibility and reports the actual active view to its accessible controls. */
function show(next: GraphView, unavailable = false): void {
  view = next;
  if (primaryCanvas) { primaryCanvas.hidden = next !== '3d'; primaryCanvas.setAttribute('aria-hidden', String(next !== '3d')); }
  if (flatCanvas) { flatCanvas.hidden = next !== '2d'; flatCanvas.setAttribute('aria-hidden', String(next !== '2d')); }
  onView?.(next, unavailable);
}

/** Restores readable 2D after WebGL errors without losing the safe snapshot or text selection controls. */
function fallback(): void {
  universe?.dispose(); universe = undefined;
  const selected = selectedId;
  show('2d', true);
  if (snapshot) flat?.render(snapshot, true);
  if (selected) flat?.selectNode(selected);
}

/** Switches views only within the current mount; a late import cannot resurrect a disposed graph. */
export async function setView(next: GraphView): Promise<void> {
  if (!primaryCanvas || !flatCanvas || !selectionSummary) return;
  const version = ++epoch;
  if (next === '2d') {
    const selected = selectedId;
    show(next); if (snapshot) flat?.render(snapshot, true); if (selected) flat?.selectNode(selected); return;
  }
  try {
    if (!universe) {
      const module = await import('@vendor/memory-graph-universe.js');
      if (version !== epoch || !primaryCanvas || !selectionSummary) return;
      universe = module.createMemoryUniverse(primaryCanvas, selectionSummary, fallback, notifySelection);
    }
    if (version !== epoch) return;
    const selected = selectedId;
    show('3d');
    if (snapshot) universe.render(snapshot, true);
    if (selected) universe.selectNode(selected);
  } catch {
    if (version === epoch) fallback();
  }
}

/** Accepts an already sanitized graph or its local filtered view, preserving the generated aliases. */
export function render(next: GraphSnapshot, preserveView = false): void {
  snapshot = next;
  try { activeRenderer()?.render(next, preserveView); }
  catch { if (view === '3d') fallback(); }
}

function activeRenderer(): Renderer | undefined { return view === '3d' ? universe : flat; }
export function zoomIn(): void { activeRenderer()?.zoomIn(); }
export function zoomOut(): void { activeRenderer()?.zoomOut(); }
export function reset(): void { activeRenderer()?.reset(); }
export function selectNode(id: string): void { activeRenderer()?.selectNode(id); }

/** Releases both optional renderers and invalidates outstanding module imports. */
export function dispose(): void {
  epoch++;
  flat?.dispose(); universe?.dispose();
  flat = undefined; universe = undefined; primaryCanvas = undefined; flatCanvas = undefined;
  selectionSummary = undefined; snapshot = undefined; onSelection = undefined; onView = undefined; view = '2d'; selectedId = undefined;
}

/** Returns only the current renderer's actual on-screen points; stale or hidden keys have no position. */
window.addEventListener('agent-activity:positions', event => {
  const request = (event as CustomEvent<{ ids: string[]; positions?: Array<{ id: string; x: number; y: number }> }>).detail;
  if (!request || !Array.isArray(request.ids)) return;
  request.positions = activeRenderer()?.activityPositions?.(request.ids.slice(0, 32)) ?? [];
});
