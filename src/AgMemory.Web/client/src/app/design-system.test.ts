import { afterEach, expect, it, vi } from 'vitest';
import { beginRouteLoading, endRouteLoading, enhancePage, showRouteFailure } from './design-system.js';
import { shell } from './shell.js';

afterEach(() => {
  document.body.replaceChildren();
  document.documentElement.className = '';
  vi.restoreAllMocks();
});

it('preserves semantic variants, disabled controls, names and checkboxes', () => {
  const root = document.createElement('div');
  root.innerHTML = '<button class="ui-button ui-button--danger" disabled>Удалить</button>'
    + '<button class="ui-icon-button" aria-label="Закрыть">×</button>'
    + '<select><option>A</option></select><input type="checkbox">';
  const dispose = enhancePage(root);

  expect(root.querySelector('button')?.classList.contains('ag-button--danger')).toBe(true);
  expect(root.querySelector('button')?.disabled).toBe(true);
  expect(root.querySelector('.ag-icon-button')?.getAttribute('aria-label')).toBe('Закрыть');
  expect(root.querySelector('select')?.classList.contains('ag-input')).toBe(true);
  expect(root.querySelector('input')?.classList.contains('ag-input')).toBe(false);
  dispose();
});

it('moves focus without changing router selection and disposes navigation', () => {
  const root = document.createElement('div');
  root.innerHTML = '<nav id="primary-navigation"><a href="/" aria-current="page">Главная</a>'
    + '<a href="/chat">Чат</a></nav>';
  document.body.append(root);
  const dispose = enhancePage(root);
  const links = root.querySelectorAll('a');
  links[0].focus();
  links[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true, cancelable: true }));

  expect(document.activeElement).toBe(links[1]);
  expect(links[0].getAttribute('aria-current')).toBe('page');
  dispose();
  links[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true, cancelable: true }));
  expect(document.activeElement).toBe(links[1]);
});

it('announces loading once and blocks only stale main content', () => {
  const root = document.createElement('div');
  root.innerHTML = '<nav><a href="/chat">Чат</a></nav><main><button>Сохранить</button></main>';
  beginRouteLoading(root);
  beginRouteLoading(root);

  expect(root.getAttribute('aria-busy')).toBe('true');
  expect(root.querySelector('main')?.hasAttribute('inert')).toBe(true);
  expect(root.querySelector('nav')?.hasAttribute('inert')).toBe(false);
  expect(root.querySelectorAll('[role="status"]')).toHaveLength(1);
  endRouteLoading(root);
  expect(root.hasAttribute('aria-busy')).toBe(false);
  expect(root.querySelector('main')?.hasAttribute('inert')).toBe(false);
  expect(root.querySelector('[role="status"]')).toBeNull();
});

it('renders an accessible failure with an explicit safe retry', () => {
  const root = document.createElement('div');
  root.innerHTML = '<main></main>';
  const retry = vi.fn();
  showRouteFailure(root, retry);

  expect(root.querySelector('[role="alert"]')?.textContent).toContain('Попробуйте ещё раз');
  expect(root.querySelector('script')).toBeNull();
  root.querySelector<HTMLButtonElement>('button')?.click();
  expect(retry).toHaveBeenCalledOnce();
});

it('cycles drawer focus and restores its trigger on Escape', async () => {
  vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue([new DOMRect()] as unknown as DOMRectList);
  const root = document.createElement('div');
  root.innerHTML = shell('Home', '/');
  document.body.append(root);
  await Promise.resolve();
  const toggle = root.querySelector<HTMLButtonElement>('#menu-toggle')!;
  const nav = root.querySelector<HTMLElement>('#primary-navigation')!;
  const close = root.querySelector<HTMLButtonElement>('#menu-close')!;
  nav.style.position = 'fixed';
  toggle.focus();
  toggle.click();
  expect(document.activeElement).toBe(close);

  close.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
  expect(document.activeElement).toBe(nav.querySelector('a:last-of-type[href="/about"]'));
  document.activeElement?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
  expect(document.activeElement).toBe(close);
  close.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
  expect(toggle.getAttribute('aria-expanded')).toBe('false');
  expect(document.activeElement).toBe(toggle);
});


it('excludes CSS-hidden, hidden-ancestor and inert controls from the drawer focus trap', async () => {
  vi.spyOn(HTMLElement.prototype, 'getClientRects').mockImplementation(function (this: HTMLElement) {
    const visible = getComputedStyle(this).display !== 'none';
    return (visible ? [new DOMRect()] : []) as unknown as DOMRectList;
  });
  const root = document.createElement('div');
  root.innerHTML = shell('Home', '/');
  document.body.append(root);
  await Promise.resolve();
  const toggle = root.querySelector<HTMLButtonElement>('#menu-toggle')!;
  const nav = root.querySelector<HTMLElement>('#primary-navigation')!;
  const close = root.querySelector<HTMLButtonElement>('#menu-close')!;
  nav.style.position = 'fixed';
  const links = nav.querySelectorAll('a');
  toggle.click();
  close.style.display = 'none';
  links[links.length - 1].setAttribute('inert', '');
  const hidden = document.createElement('div');
  hidden.hidden = true;
  links[0].replaceWith(hidden);
  hidden.append(links[0]);

  links[1].focus();
  links[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
  expect(document.activeElement).toBe(links[links.length - 2]);
  links[links.length - 2].dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
  expect(document.activeElement).toBe(links[1]);
});

it('releases the focus trap after resizing to desktop and never restores an invisible trigger', async () => {
  vi.spyOn(HTMLElement.prototype, 'getClientRects').mockImplementation(function (this: HTMLElement) {
    return (getComputedStyle(this).display === 'none' ? [] : [new DOMRect()]) as unknown as DOMRectList;
  });
  const root = document.createElement('div');
  root.innerHTML = shell('Home', '/');
  document.body.append(root);
  await Promise.resolve();
  const toggle = root.querySelector<HTMLButtonElement>('#menu-toggle')!;
  const nav = root.querySelector<HTMLElement>('#primary-navigation')!;
  nav.style.position = 'fixed';
  toggle.focus();
  toggle.click();

  // CSS changes the drawer to ordinary navigation at the desktop breakpoint.
  nav.style.position = 'static';
  toggle.style.display = 'none';
  const links = nav.querySelectorAll('a');
  const last = links[links.length - 1];
  last.focus();
  const tab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
  last.dispatchEvent(tab);
  expect(tab.defaultPrevented).toBe(false);
  last.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
  expect(nav.classList.contains('primary-nav--open')).toBe(false);
  expect(toggle.getAttribute('aria-expanded')).toBe('false');
  expect(document.activeElement).toBe(last);
});
