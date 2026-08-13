import { describe, expect, it, vi } from 'vitest';

vi.mock('./sanitize.js', () => ({
  load: vi.fn().mockResolvedValue({ status: 'available', nodes: [], edges: [], nextToken: null })
}));
vi.mock('./page-controller.js', () => ({
  initialize: vi.fn(), render: vi.fn(), zoomIn: vi.fn(), zoomOut: vi.fn(), reset: vi.fn(), dispose: vi.fn()
}));

import { memoryGraphPage, mountMemoryGraphPage } from './page.js';
import { initialize, render, zoomIn } from './page-controller.js';

describe('memory graph SPA page', () => {
  it('keeps the WebGL canvas, compatible fallback, textual alternative and controls', async () => {
    document.body.innerHTML = await memoryGraphPage();
    expect(document.querySelector('#memory-graph-canvas')).not.toBeNull();
    expect(document.querySelector('#memory-graph-fallback')?.hasAttribute('hidden')).toBe(true);
    expect(document.querySelector('#memory-graph-selection')?.getAttribute('aria-live')).toBe('polite');
    expect(document.querySelector('.memory-graph-summary')).not.toBeNull();
    mountMemoryGraphPage();
    expect(initialize).toHaveBeenCalledOnce();
    expect(render).toHaveBeenCalledOnce();
    document.querySelector<HTMLButtonElement>('#graph-zoom-in')?.click();
    expect(zoomIn).toHaveBeenCalledOnce();
  });
});
