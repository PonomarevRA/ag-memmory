import { expect, it, vi } from 'vitest';

vi.mock('./shell.js', () => ({ shell: (content: string) => `<main id="main" tabindex="-1">${content}</main>` }));
vi.mock('../features/memory-graph/page.js', () => ({ mountMemoryGraphPage: vi.fn(), disposeMemoryGraph: vi.fn() }));
vi.mock('../features/memory-reader/page-controller.js', () => ({ dispose: vi.fn(), mountMemoryReaderPage: vi.fn() }));
vi.mock('../features/navigation/index.js', () => ({ trackVisit: vi.fn() }));
vi.mock('../features/app-version/api.js', () => ({ loadAppVersion: vi.fn().mockResolvedValue(null) }));

import { startRouter } from './router.js';


/** Exercises the fallback for chat bookmarks on older static hosts. */
it('replaces a retired chat address with the memory reader', async () => {
  history.replaceState(null, '', '/chat/' + 'd'.repeat(32));
  const root = document.createElement('div');
  document.body.append(root);
  try {
    startRouter(root);
    await vi.waitFor(() => expect(root.querySelector('[data-memory-reader-route]')).not.toBeNull());
    expect(location.pathname).toBe('/memory-reader');
    expect(root.querySelector('#composer')).toBeNull();
  } finally { root.remove(); }
});

it('keeps only local current links in stored visits', async () => {
  const { historyPage } = await import('../features/history/page.js');
  const key = 'agmemory.ui.visit-log.v1';
  const routes = ['/memory-reader', '/x/../chat/legacy', '/\\example.invalid/', '/\t/example.invalid/', '//example.invalid/'];
  localStorage.setItem(key, JSON.stringify(routes.map(route => ({ route, title: 'Посещение', visitedAt: '2026-11-10' }))));
  try {
    const root = document.createElement('div');
    root.innerHTML = historyPage();
    expect([...root.querySelectorAll('a')].map(link => link.getAttribute('href'))).toEqual(['/memory-reader']);
  } finally { localStorage.removeItem(key); }
});
