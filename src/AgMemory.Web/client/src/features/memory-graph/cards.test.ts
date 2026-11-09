import { afterEach, describe, expect, it, vi } from 'vitest';
import { loadCards } from './cards.js';
import { safeResponse } from './sanitize.js';

const nodes = () => safeResponse({ status: 'available', areaId: 'product', nodes: [
  { id: 'one', href: '/memory-reader/card1?area=product', type: 'Fact', importanceBand: 3, confidenceBand: 3, degree: 1 }
], edges: [] }).nodes;
const signal = () => new AbortController().signal;
afterEach(() => vi.unstubAllGlobals());

describe('separate authorized reader cards', () => {
  it('requests only existing area-matched routes and preserves literal bounded content', async () => {
    const fetch = vi.fn().mockResolvedValue({ ok: true, json: async () => ({ status: 'available', areaId: 'product', cards: [
      { routeKey: 'card1', title: '<img onerror=unsafe>', preview: 'A real source preview', version: 7 }
    ] }) }); vi.stubGlobal('fetch', fetch);
    const result = await loadCards(nodes(), 'product', signal());
    expect(result?.get('/memory-reader/card1?area=product')).toEqual({ title: '<img onerror=unsafe>', preview: 'A real source preview', version: 7 });
    expect(fetch.mock.calls[0][0]).toBe('/api/memory-reader/cards?area=product&routes=card1');
    expect(fetch.mock.calls[0][1]).toMatchObject({ cache: 'no-store', credentials: 'same-origin' });
  });

  it('rejects wrong-area, injected extra-route, duplicate and oversized responses', async () => {
    for (const body of [
      { status: 'available', areaId: 'other', cards: [] },
      { status: 'available', areaId: 'product', cards: [{ routeKey: 'foreign', title: 'Secret', preview: 'Secret', version: 1 }] },
      { status: 'available', areaId: 'product', cards: Array(2).fill({ routeKey: 'card1', title: 'One', preview: '', version: 1 }) },
      { status: 'available', areaId: 'product', cards: [{ routeKey: 'card1', title: 'x'.repeat(161), preview: '', version: 1 }] }
    ]) {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => body }));
      expect(await loadCards(nodes(), 'product', signal())).toBeNull();
    }
  });

  it('never sends foreign-area, unapproved or oversized route batches', async () => {
    const fetch = vi.fn(); vi.stubGlobal('fetch', fetch);
    expect(await loadCards(nodes(), 'foreign', signal())).toEqual(new Map());
    expect(await loadCards(Array(26).fill(nodes()[0]), 'product', signal())).toBeNull();
    expect(await loadCards([{ ...nodes()[0], href: 'https://evil.example/secret' }], 'product', signal())).toEqual(new Map());
    expect(fetch).not.toHaveBeenCalled();
  });
});
