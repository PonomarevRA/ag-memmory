import { describe, expect, it } from 'vitest';
import { matchRoute } from './routes.js';
import { shell } from './shell.js';

describe('SPA routes and shell', () => {
  it('recognizes deep reader and chat routes', () => {
    expect(matchRoute('/memory-reader/opaque-card').name).toBe('reader');
    expect(matchRoute('/chat/invalid').name).toBe('not-found');
    expect(matchRoute(`/chat/${'a'.repeat(32)}`).params.id).toBe('a'.repeat(32));
  });

  it('renders main, skip link and active navigation', () => {
    const root = document.createElement('div');
    root.innerHTML = shell('<h1>Reader</h1>', '/memory-reader');
    expect(root.querySelector('main')?.textContent).toContain('Reader');
    expect(root.querySelector('.skip-link')?.getAttribute('href')).toBe('#main');
    expect(root.querySelector('[aria-current="page"]')?.textContent).toBe('Читать память');
  });
});
