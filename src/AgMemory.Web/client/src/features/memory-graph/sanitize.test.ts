import { describe, expect, it } from 'vitest';
import { safeResponse, mergePortion, unavailable, MAX_NODES } from './sanitize.js';

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
  it('discards unapproved content and appearance fields while assigning unique localized aliases', () => {
    const node = { id: 'n1', href: '/memory-reader/card?area=product', type: 'Fact', importanceBand: 2, confidenceBand: 3, degree: 1,
      label: 'private label', title: 'private title', canonicalText: 'private content', color: 'url(private)' };
    const result = safeResponse({ status: 'available', nodes: [node, { ...node, id: 'n2' }], edges: [] });
    expect(result.nodes.map(value => value.label)).toEqual(['Факт №1', 'Факт №2']);
    expect(result.nodes[0].color).toBe('#6172f3');
    expect(JSON.stringify(result)).not.toContain('private');
    expect(result.nodes[0]).not.toHaveProperty('title');
    expect(result.nodes[0]).not.toHaveProperty('canonicalText');
  });

  it('retains an unmapped reader node but rejects unsafe reader routes', () => {
    const node = { id: 'n1', href: null, type: 'Fact', importanceBand: 2, confidenceBand: 3, degree: 0 };
    const routes = ['https://evil.test/card', '//evil.test/card', '/memory-reader/../card', '/memory-reader/card?area=product&scope=private', '/memory-reader/card?area=../secret'];
    const result = safeResponse({ status: 'available', nodes: [node, ...routes.map((href, index) => ({ ...node, id: `bad${index}`, href }))], edges: [] });
    expect(result.nodes).toHaveLength(1);
    expect(result.nodes[0].href).toBe('');
    expect(result.nodes[0].label).toBe('Факт №1');
  });

  it('preserves accumulated aliases across cumulative portions without duplicate nodes or edges', () => {
    const nodes = ['n1', 'n2', 'n3'].map(id => ({ id, href: `/memory-reader/${id}`, type: 'Fact', importanceBand: 2, confidenceBand: 3, degree: 1 }));
    const edge = { sourceId: 'n1', targetId: 'n2', kind: 'SharedEntity', weight: 2 };
    const first = safeResponse({ status: 'available', nodes: nodes.slice(0, 2), edges: [edge], areaId: 'product', nextToken: 'next' });
    const second = safeResponse({ status: 'available', nodes, edges: [edge], areaId: 'product', nextToken: null });
    const merged = mergePortion(first, second);
    expect(merged.nodes.map(node => node.label)).toEqual(['Факт №1', 'Факт №2', 'Факт №3']);
    expect(merged.nodes.slice(0, 2)).toEqual(first.nodes);
    expect(merged.edges).toEqual([edge]);
    expect(merged.nextToken).toBeNull();
    expect(first.nodes).toHaveLength(2);
  });

  it('never mixes areas or changed generations', () => {
    const node = { id: 'n1', href: null, type: 'Fact', importanceBand: 2, confidenceBand: 3, degree: 0 };
    const first = safeResponse({ status: 'available', nodes: [node], edges: [], areaId: 'first' });
    const second = safeResponse({ status: 'available', nodes: [node], edges: [], areaId: 'second' });
    expect(mergePortion(first, second)).toEqual(unavailable('changed'));
    expect(mergePortion(first, unavailable('stale'))).toEqual(unavailable('stale'));
  });

});
