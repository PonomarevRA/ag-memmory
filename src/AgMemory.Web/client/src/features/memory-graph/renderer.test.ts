import { afterEach, describe, expect, it, vi } from 'vitest';
import { createMemoryGraphRenderer } from '@vendor/memory-graph-renderer.js';
import type { GraphSnapshot } from './sanitize.js';

/** Records canvas output instead of simulating GPU drawing, so geometry and contrast assertions stay deterministic. */
function fixture(width = 1000, height = 600) {
  let boxes: Array<{ x: number; y: number; width: number; height: number; color: string }> = [];
  let labels: Array<{ label: string; x: number; y: number; font: string }> = [];
  let strokes: Array<{ color: string; width: number; alpha: number }> = [];
  const context = {
    fillStyle: '', strokeStyle: '', lineWidth: 1, globalAlpha: 1, font: '', textAlign: '', textBaseline: '', lineCap: '',
    setTransform: vi.fn(), clearRect: () => { boxes = []; labels = []; strokes = []; }, fillRect: vi.fn(),
    strokeRect(x: number, y: number, width: number, height: number) { boxes.push({ x, y, width, height, color: this.strokeStyle }); },
    fillText(label: string, x: number, y: number) { labels.push({ label, x, y, font: this.font }); },
    measureText: (label: string) => ({ width: label.length * 7 }), beginPath: vi.fn(), moveTo: vi.fn(), lineTo: vi.fn(),
    stroke() { strokes.push({ color: this.strokeStyle, width: this.lineWidth, alpha: this.globalAlpha }); }
  };
  const canvas = document.createElement('canvas'); canvas.tabIndex = 0; document.body.append(canvas);
  Object.defineProperties(canvas, { clientWidth: { value: width }, clientHeight: { value: height } });
  canvas.getBoundingClientRect = () => ({ x: 0, y: 0, left: 0, top: 0, width, height, right: width, bottom: height, toJSON: () => ({}) });
  vi.spyOn(canvas, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D);
  const disconnect = vi.fn();
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect = disconnect; });
  const onSelect = vi.fn();
  const renderer = createMemoryGraphRenderer(canvas, onSelect);
  return { canvas, renderer, onSelect, disconnect, context, boxes: () => boxes, labels: () => labels, strokes: () => strokes };
}

/** Produces only local safe aliases; frozen inputs expose accidental mutation by the renderer. */
function snapshot(count = 3, reverse = false): GraphSnapshot {
  const nodes = Array.from({ length: count }, (_, index) => Object.freeze({ id: `id-${index.toString().padStart(3, '0')}`,
    href: `/memory-reader/r${index}`, type: 'Fact', label: `Факт №${index + 1}`, degree: 2,
    importanceBand: 3, confidenceBand: 4, color: '#6172f3' }));
  if (reverse) nodes.reverse();
  const edges = count > 2 ? [Object.freeze({ sourceId: 'id-000', targetId: 'id-001', kind: 'SharedEntity', weight: 1 })] : [];
  // Runtime freezing deliberately checks the mutable vendor boundary without weakening production types.
  return Object.freeze({ status: 'available', nodes: Object.freeze(nodes), edges: Object.freeze(edges), nextToken: null }) as unknown as GraphSnapshot;
}

function pointer(canvas: HTMLCanvasElement, type: string, x: number, y: number) {
  const event = new MouseEvent(type, { clientX: x, clientY: y, button: 0, bubbles: true, cancelable: true });
  Object.defineProperty(event, 'pointerId', { value: 1 }); canvas.dispatchEvent(event);
}

afterEach(() => { document.body.replaceChildren(); document.documentElement.removeAttribute('style'); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('readable local topology renderer', () => {
  it('deterministically lays out every capped node as a non-overlapping card without mutating input', () => {
    const view = fixture(); view.renderer.render(snapshot(170));
    const first = view.boxes().map(box => ({ ...box }));
    const positions = view.labels().filter(item => item.label.includes('№')).sort((a, b) => a.label.localeCompare(b.label));
    expect(first).toHaveLength(150);
    for (let a = 0; a < first.length; a++) for (let b = a + 1; b < first.length; b++) {
      const left = first[a]; const right = first[b];
      expect(left.x + left.width <= right.x || right.x + right.width <= left.x || left.y + left.height <= right.y || right.y + right.height <= left.y).toBe(true);
    }
    view.renderer.render(snapshot(150, true));
    expect(view.boxes().sort((a, b) => a.x - b.x || a.y - b.y)).toEqual(first.sort((a, b) => a.x - b.x || a.y - b.y));
    expect(view.labels().filter(item => item.label.includes('№')).sort((a, b) => a.label.localeCompare(b.label))).toEqual(positions);
    expect(view.labels().filter(item => item.label.includes('№'))).toHaveLength(150);
    expect(view.labels().every(item => item.font.includes('12px'))).toBe(true);
    view.renderer.dispose();
  });

  it('keeps dense mobile cards separate and reveals an offscreen alias with its ordinal intact', () => {
    const view = fixture(320, 240); view.renderer.render(snapshot(150));
    const cards = view.boxes();
    for (let a = 0; a < cards.length; a++) for (let b = a + 1; b < cards.length; b++) {
      const left = cards[a]; const right = cards[b];
      expect(left.x + left.width <= right.x || right.x + right.width <= left.x || left.y + left.height <= right.y || right.y + right.height <= left.y).toBe(true);
    }
    const alias = view.labels().find(item => item.label.endsWith('№150'));
    expect(alias).toBeDefined();
    expect(alias!.x < 0 || alias!.x > 320 || alias!.y < 0 || alias!.y > 240).toBe(true);
    view.renderer.selectNode('id-149');
    const visible = view.labels().find(item => item.label.endsWith('№150'));
    expect(visible!.x).toBeGreaterThan(0); expect(visible!.x).toBeLessThan(320);
    expect(visible!.y).toBeGreaterThan(0); expect(visible!.y).toBeLessThan(240);
    expect(visible!.label).toContain('№150');
    view.renderer.dispose();
  });

  it('uses theme contrast for selected edges and dims unrelated links without white-on-white highlights', async () => {
    const view = fixture();
    document.documentElement.style.setProperty('--focus', '#0e7490');
    const graph = snapshot(4);
    view.renderer.render({ ...graph, edges: [...graph.edges, { sourceId: 'id-002', targetId: 'id-003', kind: 'SharedEntity', weight: 1 }] });
    view.renderer.selectNode('id-000');
    expect(view.strokes()[0]).toMatchObject({ color: '#0e7490', width: 2.5, alpha: 1 });
    expect(view.strokes()[1].alpha).toBeLessThan(.2);
    document.documentElement.style.setProperty('--focus', '#6ee7f0'); await Promise.resolve();
    expect(view.strokes()[0].color).toBe('#6ee7f0');
    view.renderer.dispose();
  });

  it('supports click, keyboard selection, fit, clear and listener cleanup including empty selection callbacks', () => {
    const view = fixture(); view.renderer.render(snapshot());
    const first = view.boxes()[0];
    pointer(view.canvas, 'pointerdown', first.x + first.width / 2, first.y + first.height / 2);
    pointer(view.canvas, 'pointerup', first.x + first.width / 2, first.y + first.height / 2);
    expect(view.onSelect.mock.lastCall?.[0]).toMatchObject({ id: 'id-000', label: 'Факт №1' });
    expect(view.onSelect.mock.lastCall?.[0]).not.toHaveProperty('neighbors');
    view.canvas.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', cancelable: true }));
    expect(view.onSelect.mock.lastCall?.[0].id).toBe('id-001');
    view.renderer.selectNode(''); expect(view.onSelect.mock.lastCall?.[0]).toBeUndefined();
    view.renderer.selectNode('id-002'); view.renderer.render(snapshot(2), true); expect(view.onSelect.mock.lastCall?.[0]).toBeUndefined();
    view.renderer.selectNode('id-001'); view.renderer.reset(); expect(view.onSelect.mock.lastCall?.[0]).toBeUndefined();
    const count = view.onSelect.mock.calls.length;
    view.renderer.dispose(); view.renderer.dispose();
    view.canvas.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
    expect(view.onSelect).toHaveBeenCalledTimes(count); expect(view.disconnect).toHaveBeenCalledTimes(1);
  });

  it('pans without selecting on a drag and only consumes wheel scrolling when canvas is focused', () => {
    const view = fixture(); view.renderer.render(snapshot()); const before = view.boxes()[0].x;
    pointer(view.canvas, 'pointerdown', 10, 10); pointer(view.canvas, 'pointermove', 60, 30); pointer(view.canvas, 'pointerup', 60, 30);
    expect(view.boxes()[0].x).toBe(before + 50); expect(view.onSelect).not.toHaveBeenCalled();
    view.canvas.blur();
    const pageWheel = new WheelEvent('wheel', { deltaY: -10, cancelable: true }); view.canvas.dispatchEvent(pageWheel);
    expect(pageWheel.defaultPrevented).toBe(false);
    view.canvas.focus(); const graphWheel = new WheelEvent('wheel', { deltaY: -10, cancelable: true }); view.canvas.dispatchEvent(graphWheel);
    expect(graphWheel.defaultPrevented).toBe(true);
    const originalFont = view.context.font; view.renderer.zoomIn(); expect(view.context.font).toBe(originalFont);
    view.renderer.dispose();
  });
});
