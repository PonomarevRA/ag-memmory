import { describe, expect, it } from 'vitest';
import { sanitizeRoute, sanitizeThreadId, normalizedVisits } from './browser-state.js';

describe('navigation browser-state', () => {
  it('sanitizes routes', () => {
    expect(sanitizeRoute('chat')).toBe('/chat');
    expect(sanitizeRoute('/memory-reader?x=1')).toBe('/memory-reader');
  });

  it('accepts canonical thread ids only', () => {
    expect(sanitizeThreadId('abc')).toBeNull();
    expect(sanitizeThreadId('a'.repeat(32))).toHaveLength(32);
  });

  it('returns empty visits when storage is unreadable', () => {
    expect(normalizedVisits()).toEqual([]);
  });
});
