import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
const { flat, universe, createFlat, createUniverse } = vi.hoisted(() => {
  const renderer = () => ({ render: vi.fn(), dispose: vi.fn(), zoomIn: vi.fn(), zoomOut: vi.fn(), reset: vi.fn(), selectNode: vi.fn() });
  const flat = renderer(); const universe = renderer();
  return { flat, universe, createFlat: vi.fn((_canvas: HTMLCanvasElement, _changed: (node?: import('./sanitize.js').GraphNode) => void) => flat), createUniverse: vi.fn(() => universe) };
});
vi.mock('@vendor/memory-graph-renderer.js', () => ({ createMemoryGraphRenderer: createFlat }));
vi.mock('@vendor/memory-graph-universe.js', () => ({ createMemoryUniverse: createUniverse }));
import { dispose, initialize, render, selectNode, setView } from './page-controller.js';
import { safeResponse } from './sanitize.js';

const snapshot = safeResponse({ status: 'available', nodes: [{ id: 'n1', href: '/memory-reader/card', type: 'Fact', importanceBand: 2, confidenceBand: 3, degree: 0 }], edges: [], nextToken: null });

describe('graph renderer controller', () => {
  beforeEach(() => { vi.clearAllMocks(); universe.render.mockImplementation(() => undefined); });
  afterEach(dispose);

  it('defaults to readable 2D and does not construct WebGL until explicitly requested', async () => {
    const canvas = document.createElement('canvas'); const compatibility = document.createElement('canvas');
    initialize(canvas, compatibility, document.createElement('div'));
    render(snapshot);
    expect(canvas.hidden).toBe(true); expect(compatibility.hidden).toBe(false);
    expect(createUniverse).not.toHaveBeenCalled();
    expect(flat.render).toHaveBeenCalledWith(snapshot, false);
    await setView('3d');
    expect(createUniverse).toHaveBeenCalledOnce();
    expect(canvas.hidden).toBe(false); expect(compatibility.hidden).toBe(true);
  });

  it('falls back safely after failed 3D rendering and keeps canonical aliases', async () => {
    universe.render.mockImplementation(() => { throw new Error('private driver details'); });
    const canvas = document.createElement('canvas'); const compatibility = document.createElement('canvas'); const notify = vi.fn();
    initialize(canvas, compatibility, document.createElement('div'), undefined, notify);
    render(snapshot); await setView('3d');
    expect(canvas.hidden).toBe(true); expect(compatibility.hidden).toBe(false);
    expect(notify).toHaveBeenLastCalledWith('2d', true);
    expect(flat.render).toHaveBeenLastCalledWith(snapshot, true);
    expect(snapshot.nodes[0].label).toBe('Факт №1');
  });

  it('does not construct a late imported 3D renderer after unmount', async () => {
    initialize(document.createElement('canvas'), document.createElement('canvas'), document.createElement('div'));
    const loading = setView('3d'); dispose(); await loading;
    expect(createUniverse).not.toHaveBeenCalled();
  });

  it('restores the latest canvas selection made while the 3D module is pending', async () => {
    initialize(document.createElement('canvas'), document.createElement('canvas'), document.createElement('div'));
    const newer = { ...snapshot.nodes[0], id: 'n2', label: 'Факт №2' };
    render({ ...snapshot, nodes: [...snapshot.nodes, newer] });
    const changed = createFlat.mock.calls[0][1];
    changed(snapshot.nodes[0]);
    const loading = setView('3d');
    changed(newer);
    await loading;
    expect(universe.selectNode).toHaveBeenLastCalledWith('n2');
  });

  it('passes aliases unchanged through a filtered view and uses native clear-selection API', () => {
    initialize(document.createElement('canvas'), document.createElement('canvas'), document.createElement('div'));
    const filtered = { ...snapshot, nodes: [{ ...snapshot.nodes[0], label: 'Факт №12' }] };
    render(filtered, true); selectNode('');
    expect(flat.render).toHaveBeenCalledWith(filtered, true);
    expect(flat.selectNode).toHaveBeenCalledWith('');
  });
});
