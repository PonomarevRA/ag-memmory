import { describe, expect, it, vi } from 'vitest';
const universe = { render: vi.fn(() => { throw new Error('WebGL unavailable'); }), dispose: vi.fn(), zoomIn: vi.fn(), zoomOut: vi.fn(), reset: vi.fn(), selectNode: vi.fn() };
const fallback = { render: vi.fn(), dispose: vi.fn(), zoomIn: vi.fn(), zoomOut: vi.fn(), reset: vi.fn(), selectNode: vi.fn() };
vi.mock('@vendor/memory-graph-universe.js', () => ({ createMemoryUniverse: vi.fn(() => universe) }));
vi.mock('@vendor/memory-graph-renderer.js', () => ({ createMemoryGraphRenderer: vi.fn(() => fallback) }));
import { initialize, render } from './page-controller.js';

describe('graph controller fallback', () => {
  it('switches from failed 3D render to the local 2D renderer', () => {
    const canvas = document.createElement('canvas'); const compatibility = document.createElement('canvas'); const selection = document.createElement('p');
    initialize(canvas, compatibility, selection);
    render({ status: 'available', nodes: [], edges: [], nextToken: null });
    expect(canvas.hidden).toBe(true); expect(compatibility.hidden).toBe(false); expect(fallback.render).toHaveBeenCalledOnce();
    expect(selection.textContent).toContain('двумерная');
  });
});
