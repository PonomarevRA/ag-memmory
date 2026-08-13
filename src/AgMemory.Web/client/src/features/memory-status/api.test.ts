import { describe, expect, it } from 'vitest';
import { safeResponse, unavailable } from './api.js';

describe('memory-status safeResponse', () => {
  it('returns unavailable for malformed payloads', () => {
    expect(safeResponse(null)).toEqual(unavailable());
    expect(safeResponse({ status: 'available' })).toEqual(unavailable());
  });

  it('caps counts and type rows', () => {
    const result = safeResponse({
      status: 'available',
      totalMemoryCount: 999,
      activeMemoryCount: 100,
      expiredMemoryCount: 1,
      inactiveMemoryCount: 2,
      latestUpdateAt: '2026-08-12T12:00:00Z',
      activeByType: Array.from({ length: 20 }, (_, index) => ({ type: `T${index}`, count: index }))
    });
    expect(result.status).toBe('available');
    expect(result.activeByType).toHaveLength(12);
    expect(result.totalMemoryCount).toBe(999);
  });
});
