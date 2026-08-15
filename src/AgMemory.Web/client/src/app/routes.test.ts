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
    expect(root.querySelector('.app-shell')).not.toBeNull();
    expect(root.querySelector('.app-header')).not.toBeNull();
    expect(root.querySelector('.primary-nav')).not.toBeNull();
    expect(root.querySelector('.app-main')).not.toBeNull();
    expect(root.querySelector('.app-footer')).not.toBeNull();
    expect(root.querySelector('.header-actions .ui-button')?.textContent).toBe('Настройки');
    expect(root.querySelector('#menu-toggle')?.getAttribute('aria-controls')).toBe('primary-navigation');
    expect(root.querySelector('#primary-navigation .primary-nav__close')).not.toBeNull();
  });

  it('renders the product version in the header', () => {
    const root = document.createElement('div');
    root.innerHTML = shell('<h1>Home</h1>', '/', { version: '1.1.0', informationalVersion: '1.1.0+abc' });
    const badge = root.querySelector('.brand-version');
    expect(badge?.textContent).toBe('1.1.0');
    expect(badge?.getAttribute('title')).toBe('1.1.0+abc');
  });
});
