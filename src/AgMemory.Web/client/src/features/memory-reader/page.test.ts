import { describe, expect, it } from 'vitest';

import { memoryReaderPage } from './page.js';

describe('memory reader page', () => {
  it('renders an inert document shell for the controller', () => {
    document.body.innerHTML = memoryReaderPage('card');
    expect(document.querySelector('[data-memory-reader-route]')?.getAttribute('data-memory-reader-route')).toBe('card');
    expect(document.querySelector('#memory-reader-root')).not.toBeNull();
  });

  it('keeps the UI kit internal rather than rendering a UI-kit page', () => {
    document.body.innerHTML = memoryReaderPage('');
    expect(document.body.textContent).not.toContain('UI kit');
  });
});
