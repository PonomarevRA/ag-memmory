import { describe, expect, it } from 'vitest';
import { isSafeNamespace, isSafeTagLocator, safeDocumentResponse, MAX_BLOCKS } from './sanitize.js';

describe('memory-reader sanitize', () => {
  it('rejects documents without required fields', () => {
    expect(safeDocumentResponse({ status: 'available' }).status).toBe('unavailable');
  });

  it('caps blocks to MAX_BLOCKS', () => {
    const blocks = Array.from({ length: 20 }, (_, index) => ({
      id: `b${index}`,
      heading: null,
      content: [{ kind: 'text', text: 'x' }]
    }));
    const result = safeDocumentResponse({
      status: 'available',
      routeKey: 'rk',
      title: 'Title',
      namespace: 'inbox',
      tags: [],
      children: [],
      related: [],
      backlinks: [],
      blocks,
      nextBlockToken: null
    });
    expect(result.blocks).toHaveLength(MAX_BLOCKS);
  });

  it('validates namespace and tag locators', () => {
    expect(isSafeNamespace('inbox')).toBe(true);
    expect(isSafeNamespace('bad/name!')).toBe(false);
    expect(isSafeTagLocator('tag/' + 'a'.repeat(64))).toBe(true);
  });
});
