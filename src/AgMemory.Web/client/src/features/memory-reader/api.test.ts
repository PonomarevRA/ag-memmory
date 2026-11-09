import { afterEach, expect, it, vi } from 'vitest';
import { loadTree } from './api.js';

afterEach(() => vi.unstubAllGlobals());

it('preserves deep flat hierarchy context independently of the nested payload recursion guard', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => ({
    status: 'available', nextToken: null,
    roots: [{ kind: 'document', label: 'Deep page', href: '/memory-reader/deep',
      nodeKey: 'n299', parentKey: 'n298', depth: 299, children: [] }],
  }) }));
  const page = await loadTree();
  expect(page.roots).toHaveLength(1);
  expect(page.roots[0]).toMatchObject({ nodeKey: 'n299', parentKey: 'n298', depth: 299 });
});
