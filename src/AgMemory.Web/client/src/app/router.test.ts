import { expect, it, vi } from 'vitest';

vi.mock('./shell.js', () => ({ shell: (content: string) => `<main tabindex="-1">${content}</main>` }));
vi.mock('../pages/index.js', () => ({ renderPage: vi.fn(async (route: { name: string }) => route.name) }));
vi.mock('../features/memory-graph/page.js', () => ({ mountMemoryGraphPage: vi.fn(), disposeMemoryGraph: vi.fn() }));
vi.mock('../features/memory-graph/page-controller.js', () => ({ dispose: vi.fn() }));
vi.mock('../features/memory-reader/page-controller.js', () => ({ dispose: vi.fn(), mountMemoryReaderPage: vi.fn() }));
vi.mock('../features/navigation/index.js', () => ({ trackVisit: vi.fn(), beginNavigation: vi.fn() }));
vi.mock('../features/app-version/api.js', () => ({ loadAppVersion: vi.fn() }));

import { loadAppVersion } from '../features/app-version/api.js';
import { trackVisit } from '../features/navigation/index.js';
import { startRouter, navigate, render } from './router.js';

it('rejects stale route renders and preserves native link gestures', async () => {
  const scrolling = vi.spyOn(window,'scrollTo').mockImplementation(() => {});
  const focus = vi.spyOn(HTMLElement.prototype,'focus');
  let completeOld!: (value: null) => void;
  vi.mocked(loadAppVersion).mockReturnValueOnce(new Promise(resolve => { completeOld = resolve; })).mockResolvedValue(null);
  history.replaceState(null, '', '/');
  const target = document.createElement('div');
  document.body.append(target);
  startRouter(target);
  navigate('/about');
  await vi.waitFor(() => expect(target.textContent).toBe('about'));
  expect(scrolling).toHaveBeenCalledWith({left:0,top:0,behavior:'instant'});
  expect(focus).toHaveBeenLastCalledWith({preventScroll:true});
  completeOld(null);
  await Promise.resolve();
  expect(target.textContent).toBe('about');
  expect(trackVisit).toHaveBeenCalledTimes(1);

  const anchor = document.createElement('a');
  anchor.href = '/settings';
  target.append(anchor);
  // This listener runs after the router, then suppresses jsdom's unsupported native navigation.
  let intercepted = false;
  const suppressNative = (event: MouseEvent) => { intercepted = event.defaultPrevented; event.preventDefault(); };
  document.addEventListener('click', suppressNative);
  for (const gesture of [{ ctrlKey: true }, { shiftKey: true }, { altKey: true }, { metaKey: true }, { button: 1 }]) {
    const event = new MouseEvent('click', { ...gesture, bubbles: true, cancelable: true });
    anchor.dispatchEvent(event);
    expect(intercepted).toBe(false);
    expect(location.pathname).toBe('/about');
  }
  anchor.setAttribute('download', 'settings');
  const download = new MouseEvent('click', { bubbles: true, cancelable: true });
  anchor.dispatchEvent(download);
  expect(intercepted).toBe(false);
  anchor.removeAttribute('download');
  const click = new MouseEvent('click', { bubbles: true, cancelable: true });
  anchor.dispatchEvent(click);
  expect(intercepted).toBe(true);
  await vi.waitFor(() => expect(target.textContent).toBe('settings'));
  document.removeEventListener('click', suppressNative);

  const { renderPage } = await import('../pages/index.js');
  let finish!: (value: string) => void;
  vi.mocked(renderPage).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
  history.replaceState(null, '', '/about');
  const old = render();
  await vi.waitFor(() => expect(finish).toBeTypeOf('function'));
  navigate('/settings');
  await vi.waitFor(() => expect(target.textContent).toBe('settings'));
  finish('late about');
  await old;
  expect(target.textContent).toBe('settings');

  const graph = await import('../features/memory-graph/page.js');
  expect(graph.mountMemoryGraphPage).not.toHaveBeenCalled();
  navigate('/memory-graph');
  await vi.waitFor(() => expect(graph.mountMemoryGraphPage).toHaveBeenCalledTimes(1));
  expect(target.textContent).toBe('graph');
  navigate('/memory-reader');
  await vi.waitFor(() => expect(target.textContent).toBe('reader'));
  expect(graph.disposeMemoryGraph).toHaveBeenCalledTimes(1);

  let rejectRoute!: (reason: Error) => void;
  vi.mocked(renderPage).mockReturnValueOnce(new Promise((_, reject) => { rejectRoute = reject; }));
  navigate('/about');
  await vi.waitFor(() => expect(rejectRoute).toBeTypeOf('function'));
  expect(target.getAttribute('aria-busy')).toBe('true');
  expect(target.querySelector('main')?.hasAttribute('inert')).toBe(true);
  rejectRoute(new Error('private backend details'));
  await vi.waitFor(() => expect(target.querySelector('[role="alert"]')).not.toBeNull());
  expect(target.hasAttribute('aria-busy')).toBe(false);
  expect(target.textContent).not.toContain('private backend details');
  target.querySelector<HTMLButtonElement>('button')!.click();
  await vi.waitFor(() => expect(target.textContent).toBe('about'));
  const calls = scrolling.mock.calls.length;
  await render();
  expect(scrolling.mock.calls.length).toBe(calls);
  scrolling.mockRestore();focus.mockRestore();
});
