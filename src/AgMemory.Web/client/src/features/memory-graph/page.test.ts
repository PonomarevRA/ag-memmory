import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { GraphNode } from './sanitize.js';
vi.mock('./sanitize.js', async original => ({ ...await original<typeof import('./sanitize.js')>(), load: vi.fn() }));
vi.mock('./page-controller.js', () => ({ initialize: vi.fn(), render: vi.fn(), zoomIn: vi.fn(), zoomOut: vi.fn(), reset: vi.fn(), dispose: vi.fn(), selectNode: vi.fn(), setView: vi.fn().mockResolvedValue(undefined) }));
vi.mock('./cards.js', () => ({ loadCards: vi.fn() }));
import { loadCards } from './cards.js';
import { load, safeResponse } from './sanitize.js';
import * as controller from './page-controller.js';
import { disposeMemoryGraph, memoryGraphPage, mountMemoryGraphPage } from './page.js';

const rawNodes = [
  { id: 'n1', href: '/memory-reader/card1?area=product', type: 'Fact', importanceBand: 2, confidenceBand: 3, degree: 4 },
  { id: 'n2', href: '/memory-reader/card2?area=product', type: 'Event', importanceBand: 3, confidenceBand: 4, degree: 1 },
  { id: 'n3', href: '/memory-reader/card3?area=product', type: 'Fact', importanceBand: 4, confidenceBand: 5, degree: 1 }
];
const fixture = () => safeResponse({ status: 'available', nodes: rawNodes, edges: [{ sourceId: 'n1', targetId: 'n2', kind: 'SharedEntity', weight: 2 }], nextToken: null, areaId: 'product', areaLabel: 'Продукт' });
let selection: (node?: GraphNode) => void;

/** Mounts a deterministic safe graph without a native canvas implementation. */
async function mount(): Promise<void> {
  document.body.innerHTML = await memoryGraphPage();
  mountMemoryGraphPage();
}

function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(complete => { resolve = complete; }); return { promise, resolve }; }

describe('memory graph accessible page', () => {
  beforeEach(() => {
    vi.clearAllMocks(); history.replaceState(null, '', '/memory-graph');
    vi.mocked(load).mockResolvedValue(fixture());
    vi.mocked(loadCards).mockResolvedValue(null);
    vi.mocked(controller.initialize).mockImplementation((_, __, ___, changed) => { selection = changed!; });
    vi.mocked(controller.selectNode).mockImplementation(id => selection(vi.mocked(controller.render).mock.lastCall?.[0].nodes.find(node => node.id === id)));
  });
  afterEach(() => { disposeMemoryGraph(); document.body.innerHTML = ''; });

  it('defaults to readable 2D with localized aliases, legend and honest bounded progress', async () => {
    await mount();
    expect(document.querySelector('#memory-graph-canvas')?.hasAttribute('hidden')).toBe(true);
    expect(document.querySelector('#memory-graph-fallback')?.hasAttribute('hidden')).toBe(false);
    expect(document.querySelector('#graph-node-list')?.textContent).toContain('Факт №3');
    expect(document.querySelector('#graph-type-legend')?.textContent).toContain('Событие');
    expect(document.querySelector('#graph-help')?.textContent).toContain('не причинная связь');
    expect(document.querySelector('#graph-progress')?.textContent).toContain('ограниченная карта');
    expect(document.querySelector('#graph-progress')?.textContent).toContain('Продукт');
    document.querySelector<HTMLButtonElement>('#graph-zoom-in')!.click();
    expect(controller.zoomIn).toHaveBeenCalledOnce();
  });

  it('synchronizes keyboard-compatible node selection, weighted neighbors and area-bound Wiki links', async () => {
    await mount();
    document.querySelector<HTMLButtonElement>('[data-graph-node="n1"]')!.click();
    expect(document.querySelector('#graph-inspector-content')?.textContent).toContain('Факт №1');
    expect(document.querySelector('#graph-inspector-content')?.textContent).toContain('Общих сущностей: 2');
    expect(document.querySelector('#graph-inspector-content')?.textContent).toContain('Соседей на загруженной карте: 1');
    expect(document.querySelector<HTMLAnchorElement>('#graph-inspector-content a')?.getAttribute('href')).toBe('/memory-reader/card1?area=product');
    expect(document.querySelector<HTMLButtonElement>('#graph-node-list [data-graph-node="n1"]')?.getAttribute('aria-pressed')).toBe('true');
    document.querySelector<HTMLButtonElement>('.memory-graph-neighbor-select')!.click();
    expect(document.querySelector('#graph-inspector-content h2')?.textContent).toBe('Событие №2');
  });

  it('keeps canonical aliases when filtering and restores the full filtered view after clearing neighborhood selection', async () => {
    await mount();
    const input = document.querySelector<HTMLInputElement>('#graph-search')!;
    input.value = '№3'; input.dispatchEvent(new Event('input'));
    expect(vi.mocked(controller.render).mock.lastCall?.[0].nodes[0].label).toBe('Факт №3');
    expect(document.querySelectorAll('#graph-node-list button')).toHaveLength(1);
    input.value = ''; input.dispatchEvent(new Event('input'));
    document.querySelector<HTMLButtonElement>('#graph-node-list [data-graph-node="n1"]')!.click();
    const checkbox = document.querySelector<HTMLInputElement>('#graph-neighborhood')!;
    checkbox.checked = true; checkbox.dispatchEvent(new Event('change'));
    expect(vi.mocked(controller.render).mock.lastCall?.[0].nodes.map(node => node.id)).toEqual(['n1', 'n2']);
    document.querySelector<HTMLButtonElement>('#graph-clear')!.click();
    expect(vi.mocked(controller.render).mock.lastCall?.[0].nodes).toHaveLength(3);
    expect(checkbox.checked).toBe(false);
  });

  it('reveals a filtered neighbor before selecting it and keeps controls consistent', async () => {
    await mount();
    document.querySelector<HTMLButtonElement>('#graph-node-list [data-graph-node="n1"]')!.click();
    const types = document.querySelector<HTMLSelectElement>('#graph-type')!;
    types.value = 'Fact'; types.dispatchEvent(new Event('change'));
    expect(vi.mocked(controller.render).mock.lastCall?.[0].nodes.map(node => node.id)).toEqual(['n1', 'n3']);
    document.querySelector<HTMLButtonElement>('.memory-graph-neighbor-select')!.click();
    expect(document.querySelector('#graph-inspector-content h2')?.textContent).toBe('Событие №2');
    expect(types.value).toBe('');
    expect(vi.mocked(controller.render).mock.lastCall?.[0].nodes.map(node => node.id)).toContain('n2');
  });

  it('clears a removed type filter after refreshing to a new generation', async () => {
    await mount();
    const types = document.querySelector<HTMLSelectElement>('#graph-type')!;
    types.value = 'Fact'; types.dispatchEvent(new Event('change'));
    vi.mocked(load).mockResolvedValueOnce(safeResponse({ status: 'available', nodes: [rawNodes[1]], edges: [], nextToken: null, areaId: 'product' }));
    document.querySelector<HTMLButtonElement>('#graph-refresh')!.click();
    await vi.waitFor(() => expect(document.querySelector('#graph-node-list')?.textContent).toContain('Событие №1'));
    expect(types.value).toBe('');
    expect(vi.mocked(controller.render).mock.lastCall?.[0].nodes).toHaveLength(1);
  });

  it('merges cumulative portions once with stable aliases and does not duplicate their shared prefix', async () => {
    const first = fixture(); first.nodes = first.nodes.slice(0, 2); first.nextToken = 'portion2';
    vi.mocked(load).mockResolvedValueOnce(first);
    await mount();
    const next = deferred<ReturnType<typeof fixture>>(); vi.mocked(load).mockReturnValueOnce(next.promise);
    const button = document.querySelector<HTMLButtonElement>('#graph-more')!;
    button.click(); button.click();
    expect(load).toHaveBeenCalledTimes(2);
    next.resolve(fixture());
    await vi.waitFor(() => expect(document.querySelectorAll('#graph-node-list button')).toHaveLength(3));
    expect(document.querySelector('#graph-node-list')?.textContent).toContain('Факт №3');
    expect(document.querySelector('#graph-portion-message')?.textContent).toContain('может не включать все записи');
  });

  it('clears an old generation after a changed response instead of retaining mixed topology', async () => {
    vi.mocked(load).mockResolvedValueOnce({ ...fixture(), nextToken: 'portion2' });
    await mount(); vi.mocked(load).mockResolvedValueOnce(safeResponse({ status: 'changed' }));
    document.querySelector<HTMLButtonElement>('#graph-more')!.click();
    await vi.waitFor(() => expect(document.querySelector('#graph-portion-message')?.textContent).toContain('Память изменилась'));
    expect(document.querySelectorAll('#graph-node-list button')).toHaveLength(0);
    expect(vi.mocked(controller.render).mock.lastCall?.[0].nodes).toHaveLength(0);
  });

  it('ignores a late portion after the page is disposed', async () => {
    vi.mocked(load).mockResolvedValueOnce({ ...fixture(), nextToken: 'portion2' });
    await mount();
    const next = deferred<ReturnType<typeof fixture>>(); vi.mocked(load).mockReturnValueOnce(next.promise);
    document.querySelector<HTMLButtonElement>('#graph-more')!.click();
    const calls = vi.mocked(controller.render).mock.calls.length;
    disposeMemoryGraph(); document.body.innerHTML = '<p>Следующая страница</p>';
    next.resolve(fixture()); await next.promise;
    expect(controller.render).toHaveBeenCalledTimes(calls);
    expect(document.body.textContent).toBe('Следующая страница');
  });

  it('keeps graph topology but offers no Wiki links when the reader has no matching area', async () => {
    vi.mocked(load).mockResolvedValueOnce(safeResponse({ status: 'available', nodes: [{ ...rawNodes[0], href: null }], edges: [], nextToken: null }));
    await mount(); selection({ ...fixture().nodes[0], href: '' });
    expect(document.querySelector('#graph-inspector-content')?.textContent).toContain('Wiki не настроена');
    expect(document.querySelector('#graph-inspector-content a')).toBeNull();
    expect(document.querySelectorAll('#graph-node-list button')).toHaveLength(1);
  });

  it('forwards an explicit area hint so the endpoint can reject a graph from a different area', async () => {
    history.replaceState(null, '', '/memory-graph?area=other'); await mount();
    expect(load).toHaveBeenCalledWith(null, 'other');
  });

  it('restores the latest selection made while the optional 3D switch is pending', async () => {
    await mount(); selection(fixture().nodes[0]);
    const switched = deferred<void>(); vi.mocked(controller.setView).mockReturnValueOnce(switched.promise);
    document.querySelector<HTMLButtonElement>('#graph-view-3d')!.click();
    selection(fixture().nodes[2]); switched.resolve();
    await vi.waitFor(() => expect(controller.selectNode).toHaveBeenLastCalledWith('n3'));
    expect(document.querySelector('#graph-inspector-content h2')?.textContent).toBe('Факт №3');
  });

  it('searches real reader titles/previews, keeps literal text safe and preserves the focused search field', async () => {
    const cards = deferred<Map<string, { title: string; preview: string; version: number }> | null>();
    vi.mocked(loadCards).mockReturnValueOnce(cards.promise);
    await mount();
    const input = document.querySelector<HTMLInputElement>('#graph-search')!; input.focus();
    expect(input.compareDocumentPosition(document.querySelector('#memory-graph-fallback')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    cards.resolve(new Map([['/memory-reader/card1?area=product', { title: 'Восстановление памяти <img onerror=unsafe>', preview: 'Проверка резервной копии', version: 2 }]]));
    await vi.waitFor(() => expect(document.querySelector('#graph-node-list')?.textContent).toContain('Восстановление памяти'));
    expect(document.activeElement).toBe(input); expect(document.querySelector('img')).toBeNull();
    input.value = 'резервной'; input.dispatchEvent(new Event('input'));
    expect(document.querySelectorAll('#graph-node-list button')).toHaveLength(1);
    expect(vi.mocked(controller.render).mock.lastCall?.[0].nodes[0]).toMatchObject({ label: 'Восстановление памяти <img onerror=unsafe>', alias: 'Факт №1' });
    document.querySelector<HTMLButtonElement>('#graph-node-list button')!.click();
    expect(document.querySelector('#graph-inspector-content')?.textContent).toContain('Проверка резервной копии');
  });

  it('ignores old hydration after refresh and aborts it on disposal', async () => {
    const old = deferred<Map<string, { title: string; preview: string; version: number }> | null>();
    vi.mocked(loadCards).mockReturnValueOnce(old.promise);
    await mount(); const oldSignal = vi.mocked(loadCards).mock.calls[0][2];
    vi.mocked(load).mockResolvedValueOnce(safeResponse({ status: 'available', nodes: [{ ...rawNodes[1], id: 'new-id' }], edges: [], nextToken: null, areaId: 'product' }));
    document.querySelector<HTMLButtonElement>('#graph-refresh')!.click();
    await vi.waitFor(() => expect(document.querySelector('#graph-node-list')?.textContent).toContain('Событие №1'));
    expect(oldSignal.aborted).toBe(true);
    old.resolve(new Map([['/memory-reader/card1?area=product', { title: 'Stale title', preview: 'Stale', version: 1 }]]));
    await old.promise;
    expect(document.body.textContent).not.toContain('Stale title');
    const finalSignal = vi.mocked(loadCards).mock.calls.at(-1)![2];
    disposeMemoryGraph(); expect(finalSignal.aborted).toBe(true);
  });

  it('fits the camera without clearing the chosen record or neighbor mode', async () => {
    await mount();
    document.querySelector<HTMLButtonElement>('#graph-node-list [data-graph-node="n1"]')!.click();
    document.querySelector<HTMLButtonElement>('#graph-focus')!.click();
    expect(vi.mocked(controller.render).mock.lastCall?.[0].focusId).toBe('n1');
    document.querySelector<HTMLButtonElement>('#graph-reset')!.click();
    expect(document.querySelector('#graph-inspector-content h2')?.textContent).toBe('Факт №1');
    expect(document.querySelector<HTMLInputElement>('#graph-neighborhood')!.checked).toBe(true);
  });

  it('reveals actual neighbors when focusing a search result, and returns to all loaded records', async () => {
    await mount();
    const input = document.querySelector<HTMLInputElement>('#graph-search')!;
    input.value = '№1'; input.dispatchEvent(new Event('input'));
    document.querySelector<HTMLButtonElement>('#graph-node-list button')!.click();
    document.querySelector<HTMLButtonElement>('#graph-focus')!.click();
    expect(input.value).toBe('');
    expect(vi.mocked(controller.render).mock.lastCall?.[0].nodes.map(node => node.id)).toEqual(['n1', 'n2']);
    document.querySelector<HTMLButtonElement>('#graph-show-all')!.click();
    expect(vi.mocked(controller.render).mock.lastCall?.[0].nodes).toHaveLength(3);
    expect(document.querySelector('#graph-inspector-content h2')?.textContent).toBe('Факт №1');
  });

  it('starts on a connected record after initial hydration without stealing input focus', async () => {
    vi.mocked(loadCards).mockResolvedValue(new Map([['/memory-reader/card1?area=product', { title: 'Useful starting record', preview: 'Details', version: 2 }]]));
    await mount();
    await vi.waitFor(() => expect(document.querySelector('#graph-inspector-content h2')?.textContent).toBe('Useful starting record'));
    expect(vi.mocked(controller.render).mock.lastCall?.[0].focusId).toBe('n1');
    expect(document.querySelector<HTMLAnchorElement>('.graph-page-header a')!.getAttribute('href')).toBe('/memory-reader?area=product');
  });
  it('preserves the addressed Wiki link focus while the same record receives new card text', async () => {
    const cards = deferred<Map<string, { title: string; preview: string; version: number }> | null>();
    vi.mocked(loadCards).mockReturnValueOnce(cards.promise);
    await mount();
    document.querySelector<HTMLButtonElement>('#graph-node-list [data-graph-node="n1"]')!.click();
    const wiki = document.querySelector<HTMLAnchorElement>('#graph-inspector-content a')!;
    wiki.focus(); const href = wiki.getAttribute('href');
    cards.resolve(new Map([['/memory-reader/card1?area=product', { title: 'Current source', preview: 'New preview', version: 2 }]]));
    await vi.waitFor(() => expect(document.querySelector('#graph-selected-title')?.textContent).toBe('Current source'));
    expect(document.activeElement?.tagName).toBe('A'); expect(document.activeElement?.getAttribute('href')).toBe(href);
  });

  it('aborts pending cards and clears loading when a continuation changes generation', async () => {
    vi.mocked(load).mockResolvedValueOnce({ ...fixture(), nextToken: 'next' });
    const cards = deferred<Map<string, { title: string; preview: string; version: number }> | null>();
    vi.mocked(loadCards).mockReturnValueOnce(cards.promise);
    await mount(); const signal = vi.mocked(loadCards).mock.calls[0][2];
    vi.mocked(load).mockResolvedValueOnce(safeResponse({ status: 'changed' }));
    document.querySelector<HTMLButtonElement>('#graph-more')!.click();
    await vi.waitFor(() => expect(document.querySelector('#graph-portion-message')?.textContent).toContain('Память изменилась'));
    expect(signal.aborted).toBe(true);
    expect(document.querySelector('#graph-card-status')?.textContent).not.toContain('Загружаем');
    cards.resolve(new Map([['/memory-reader/card1?area=product', { title: 'Old content', preview: 'Must not appear', version: 1 }]]));
    await cards.promise;
    expect(document.body.textContent).not.toContain('Old content');
  });

});
