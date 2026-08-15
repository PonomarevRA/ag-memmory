import { describe, expect, it } from 'vitest';
import { resetAppVersionCache, safeAppVersion } from './api.js';

describe('app-version safeAppVersion', () => {
  it('accepts a product version and informational metadata', () => {
    expect(safeAppVersion({
      version: '1.1.0',
      informationalVersion: '1.1.0+abc1234'
    })).toEqual({ version: '1.1.0', informationalVersion: '1.1.0+abc1234' });
  });

  it('rejects malformed or oversized payloads', () => {
    resetAppVersionCache();
    expect(safeAppVersion(null)).toBeNull();
    expect(safeAppVersion({ version: '1.1.0<script>' })).toBeNull();
    expect(safeAppVersion({ version: '1.1.0', informationalVersion: '1.1.0 /etc/passwd' })).toEqual({
      version: '1.1.0',
      informationalVersion: '1.1.0'
    });
  });
});
