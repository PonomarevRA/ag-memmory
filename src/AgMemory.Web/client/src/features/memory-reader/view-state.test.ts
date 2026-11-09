import { afterEach, describe, expect, it } from 'vitest';
import { ReaderHistory, streamStatus } from './view-state.js';

describe('private reader history and recovery states', () => {
  afterEach(() => history.replaceState(null, '', '/memory-reader'));
  it('keeps queries and cursors out of serialized History and evicts old tab-only snapshots', () => {
    const cache = new ReaderHistory<{ search: string; token: string }>(2);
    cache.remember({ search: 'private phrase', token: 'opaque-1' });
    const first = history.state;
    expect(JSON.stringify(first)).not.toContain('private phrase');
    expect(JSON.stringify(first)).not.toContain('opaque-1');
    history.pushState({}, '', '/memory-reader?area=second');
    cache.remember({ search: 'second', token: 'opaque-2' });
    history.pushState({}, '', '/memory-reader?area=third');
    cache.remember({ search: 'third', token: 'opaque-3' });
    expect(cache.current()?.search).toBe('third');
    history.replaceState(first, '', '/memory-reader');
    expect(cache.current()).toBeUndefined();
  });
  it('distinguishes stale data and empty data from unavailable resources', () => {
    expect(streamStatus('available', 0)).toBe('empty');
    expect(streamStatus('available', 2)).toBe('available');
    expect(streamStatus('changed', 0)).toBe('changed');
    expect(streamStatus('stale', 0)).toBe('changed');
    expect(streamStatus('catalog-not-ready', 0)).toBe('unavailable');
  });
});
