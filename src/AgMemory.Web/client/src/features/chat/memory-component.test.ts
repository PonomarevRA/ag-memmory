import { describe, expect, it } from 'vitest';
import { safeMemoryComponent, safeMemoryComponents } from './memory-component.js';

describe('chat evidence boundary', () => {
  const preview = { type: 'Fact', title: 'Rule', preview: 'Evidence' };
  it('accepts only opaque local revalidation links and whitelisted metadata', () => {
    expect(safeMemoryComponent({ ...preview, href: '/api/chat-memory/sources/opaque_token-123', revision: 2, capturedAt: '2026-10-21T10:00:00Z', sourceState: 'available', scope: 'secret', storagePath: '/secret' }))
      .toEqual({ ...preview, href: '/api/chat-memory/sources/opaque_token-123', revision: 2, capturedAt: '2026-10-21T10:00:00Z', sourceState: 'available' });
    for (const href of ['https://evil.test', '//evil.test', 'javascript:alert(1)', '/memory-reader/raw?area=work', '/api/chat-memory/sources/token?scope=secret', '/api/chat-memory/sources/../secret']) {
      expect(safeMemoryComponent({ ...preview, href })).toEqual(preview);
    }
  });
  it('bounds evidence and drops malformed components', () => {
    expect(safeMemoryComponents([null, {}, { ...preview, title: 5 }])).toEqual([]);
    expect(safeMemoryComponents(Array.from({ length: 50 }, () => preview))).toHaveLength(12);
    const safe = safeMemoryComponent({ ...preview, title: 'x'.repeat(1000), preview: 'x'.repeat(2000), revision: -1, capturedAt: 'bad' });
    expect(safe?.title).toHaveLength(160);
    expect(safe?.preview).toHaveLength(320);
    expect(safe).not.toHaveProperty('revision');
  });
});
