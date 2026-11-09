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


describe('native sidebar capability', () => {
  it('ignores native commands in a browser and handles one toggle after multiple route renders', async () => {
    document.documentElement.className = '';
    document.body.innerHTML = shell('<h1>Home</h1>', '/');
    await Promise.resolve();
    window.dispatchEvent(new Event('agmemory:toggle-sidebar'));
    expect(document.documentElement.classList.contains('sidebar-collapsed')).toBe(false);
    document.documentElement.classList.add('native-toolbar-ready');
    document.body.innerHTML = shell('<h1>Chat</h1>', '/chat');
    await Promise.resolve();
    window.dispatchEvent(new Event('agmemory:toggle-sidebar'));
    expect(document.documentElement.classList.contains('sidebar-collapsed')).toBe(true);
    const link = document.querySelector('a[aria-current="page"]');
    expect(link?.getAttribute('aria-label')).toBe('Чат');
    expect(link?.getAttribute('title')).toBe('Чат');
    expect(link?.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
    document.body.innerHTML = shell('<h1>Home</h1>', '/');
    await Promise.resolve();
    expect(document.documentElement.classList.contains('sidebar-collapsed')).toBe(true);
    window.dispatchEvent(new Event('agmemory:toggle-sidebar'));
    expect(document.documentElement.classList.contains('sidebar-collapsed')).toBe(false);
    document.documentElement.className = '';
  });

  it('keeps the sidebar usable when local storage rejects writes', async () => {
    document.documentElement.className = 'native-toolbar-ready';
    document.body.innerHTML = shell('Home', '/');
    await Promise.resolve();
    const original = Storage.prototype.setItem;
    Storage.prototype.setItem = () => { throw new Error('Storage blocked'); };
    try {
      window.dispatchEvent(new Event('agmemory:toggle-sidebar'));
      expect(document.documentElement.classList.contains('sidebar-collapsed')).toBe(true);
      window.dispatchEvent(new Event('agmemory:toggle-sidebar'));
      expect(document.documentElement.classList.contains('sidebar-collapsed')).toBe(false);
    } finally {
      Storage.prototype.setItem = original;
      document.documentElement.className = '';
    }
  });
});


describe('native brand header and independent left rail', () => {
  it('keeps branding in the header and navigation outside it across collapse and route replacement', async () => {
    document.documentElement.className = 'native-desktop native-sidebar-layout native-toolbar-ready';
    try {
      document.body.innerHTML = shell('Home', '/');
      await Promise.resolve();
      expect(document.querySelector('.app-header .brand-name')?.textContent).toBe('AgMemory');
      expect(document.querySelector('.app-header .brand-mark img')).not.toBeNull();
      expect(document.querySelector('.app-header #primary-navigation')).toBeNull();
      expect(document.querySelector('.app-shell > #primary-navigation')).not.toBeNull();
      window.dispatchEvent(new Event('agmemory:toggle-sidebar'));
      document.body.innerHTML = shell('Chat', '/chat');
      await Promise.resolve();
      expect(document.querySelector('.app-header .brand-name')?.textContent).toBe('AgMemory');
      expect(document.querySelector('.app-shell > #primary-navigation')).not.toBeNull();
    } finally {
      document.documentElement.className = '';
      localStorage.removeItem('agmemory.sidebarCollapsed');
    }
  });

  it('gives browsers the same independent leading navigation region', () => {
    document.documentElement.className = '';
    document.body.innerHTML = shell('Browser', '/');
    expect(document.querySelector('.app-header > #primary-navigation')).toBeNull();
    expect(document.querySelector('.app-shell > #primary-navigation')).not.toBeNull();
  });
});


it('browser sidebar toggle updates its accessible state and preserves active link identity',async()=>{
  document.documentElement.className='';localStorage.removeItem('agmemory.sidebarCollapsed');
  document.body.innerHTML=shell('<h1>Memory</h1>','/knowledge');
  await Promise.resolve();
  const control=document.querySelector<HTMLButtonElement>('#browser-sidebar-toggle')!;
  control.focus();control.click();
  expect(control.getAttribute('aria-expanded')).toBe('false');
  expect(control.getAttribute('aria-label')).toBe('Развернуть меню');
  expect(document.activeElement).toBe(control);
  expect(document.querySelector('[aria-current=page]')?.getAttribute('aria-label')).toBe('Знания и решения');
  document.body.innerHTML=shell('Again','/knowledge');await Promise.resolve();
  expect(document.documentElement.classList.contains('sidebar-collapsed')).toBe(true);
  localStorage.removeItem('agmemory.sidebarCollapsed');document.documentElement.className='';
});


it('places the existing mobile menu in shared toolbar navigation without cloning its command',async()=>{
  document.documentElement.className='';
  document.body.innerHTML=shell('Browser','/');await Promise.resolve();
  expect(document.querySelectorAll('#menu-toggle')).toHaveLength(1);
  expect(document.querySelector('.ag-window-toolbar__navigation #menu-toggle')).not.toBeNull();
  expect(document.querySelector('.ag-window-toolbar__actions a[aria-label="Настройки"] svg')).not.toBeNull();
});
