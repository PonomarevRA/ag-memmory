import { describe, expect, it } from 'vitest';
import { safeResponse, unavailable, MAX_NODES, MAX_EDGES } from './sanitize.js';

describe('memory-graph sanitize', () => {
  it('returns unavailable for invalid payloads', () => {
    expect(safeResponse(null)).toEqual(unavailable());
  });

  it('caps nodes and edges and deduplicates self loops', () => {
    const nodes = Array.from({ length: MAX_NODES + 5 }, (_, index) => ({
      id: `n${index}`,
      href: `/memory-reader/${index}`,
      type: 'Event',
      importanceBand: 3,
      confidenceBand: 3,
      degree: index
    }));
    const edges = [
      { sourceId: 'n0', targetId: 'n0', kind: 'SharedEntity', weight: 5 },
      { sourceId: 'n0', targetId: 'n1', kind: 'SharedEntity', weight: 900 }
    ];
    const result = safeResponse({ status: 'available', nodes, edges, nextToken: null });
    expect(result.nodes).toHaveLength(MAX_NODES);
    expect(result.edges).toHaveLength(1);
    expect(result.edges[0]?.weight).toBe(200);
    expect(result.nodes.every(node => node.layoutKey === node.id)).toBe(true);
  });
});
