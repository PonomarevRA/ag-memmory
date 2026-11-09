/** Typed browser adapter contracts for the vendored renderers; implementation stays local JavaScript. */
declare module '@vendor/memory-graph-renderer.js' {
  type GraphSnapshot = import('../features/memory-graph/sanitize.js').GraphSnapshot;
  export function createMemoryGraphRenderer(canvas: HTMLCanvasElement, onSelect: (node?: GraphSnapshot['nodes'][number]) => void): {
    render(snapshot: GraphSnapshot, preserveView?: boolean): void;
    activityPositions(ids: string[]): Array<{ id: string; x: number; y: number }>;
    zoomIn(): void;
    zoomOut(): void;
    reset(): void;
    selectNode(id: string): void;
    dispose(): void;
  };
}

declare module '@vendor/memory-graph-universe.js' {
  type GraphSnapshot = import('../features/memory-graph/sanitize.js').GraphSnapshot;
  export function createMemoryUniverse(canvas: HTMLCanvasElement, summary: HTMLElement, onFailure: () => void, onSelect?: (node?: GraphSnapshot['nodes'][number]) => void): {
    render(snapshot: GraphSnapshot, preserveView?: boolean): void;
    activityPositions(ids: string[]): Array<{ id: string; x: number; y: number }>;
    zoomIn(): void;
    zoomOut(): void;
    reset(): void;
    selectNode(id?: string): void;
    dispose(): void;
  };
}
