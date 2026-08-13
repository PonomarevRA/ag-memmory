import { beforeEach, describe, expect, it, vi } from 'vitest';
import { antiforgeryJsonHeaders, getAntiforgeryToken } from './antiforgery.js';

describe('antiforgery bootstrap', () => {
  beforeEach(() => { vi.restoreAllMocks(); });
  it('obtains a same-origin no-store token and uses the request header', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ requestToken: 'token' }), { status: 200 }));
    expect(await getAntiforgeryToken()).toBe('token');
    expect(fetchMock).toHaveBeenCalledWith('/api/antiforgery', { credentials: 'same-origin', cache: 'no-store' });
    expect(antiforgeryJsonHeaders('token')).toMatchObject({ 'Content-Type': 'application/json', RequestVerificationToken: 'token' });
  });
});
