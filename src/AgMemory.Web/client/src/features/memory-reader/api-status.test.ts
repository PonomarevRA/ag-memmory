import { afterEach, describe, expect, it, vi } from 'vitest';
import { loadTags, loadTree } from './api.js';

describe('reader continuation recovery DTOs', () => {
  afterEach(() => vi.unstubAllGlobals());
  it('preserves changed generations through both tree and facet sanitizers', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => ({ status: 'changed' }) }));
    expect(await loadTree('old', 'product')).toEqual({ status: 'changed', roots: [], nextToken: null });
    expect(await loadTags('old', 'product')).toEqual({ status: 'changed', tags: [], nextToken: null });
  });
});
