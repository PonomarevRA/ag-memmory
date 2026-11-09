import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { startRouter, navigate } from './router.js';
import { getPreferences } from '../features/navigation/browser-state.js';

vi.mock('../features/app-version/api.js', () => ({ loadAppVersion: vi.fn(async () => null) }));

beforeEach(() => { vi.spyOn(window,'scrollTo').mockImplementation(() => {}); });

afterEach(() => {
  document.body.replaceChildren();
  delete document.documentElement.dataset.theme;
  delete document.documentElement.dataset.reduceMotion;
  localStorage.clear();
});

it('mounts the real settings route after commit and disposes preference handlers when leaving', async () => {
  localStorage.clear();
  history.replaceState(null, '', '/settings');
  const root = document.createElement('div');
  document.body.append(root);
  startRouter(root);
  await vi.waitFor(() => expect(root.querySelector('#theme')).not.toBeNull());
  const theme = root.querySelector<HTMLSelectElement>('#theme')!;
  const motion = root.querySelector<HTMLInputElement>('#motion')!;

  theme.value = 'light';
  theme.dispatchEvent(new Event('change', { bubbles: true }));
  expect(document.documentElement.dataset.theme).toBe('light');
  expect(getPreferences().theme).toBe('light');

  motion.checked = true;
  motion.dispatchEvent(new Event('change', { bubbles: true }));
  expect(document.documentElement.dataset.reduceMotion).toBe('true');
  expect(getPreferences().reduceMotion).toBe(true);

  navigate('/');
  await vi.waitFor(() => expect(root.querySelector('#theme')).toBeNull());
  theme.value = 'dark';
  theme.dispatchEvent(new Event('change', { bubbles: true }));
  motion.checked = false;
  motion.dispatchEvent(new Event('change', { bubbles: true }));
  expect(getPreferences()).toEqual({ theme: 'light', reduceMotion: true });

  navigate('/settings');
  await vi.waitFor(() => expect(root.querySelector('#theme')).not.toBeNull());
  expect(root.querySelector<HTMLSelectElement>('#theme')!.value).toBe('light');
  expect(root.querySelector<HTMLInputElement>('#motion')!.checked).toBe(true);
});


it('reports blocked preference storage without undoing the visible choice or stealing focus',async()=>{
  const {settingsPage,mountSettingsPage}=await import('../features/settings/page.js');
  const root=document.createElement('div');root.innerHTML=settingsPage();document.body.append(root);
  const dispose=mountSettingsPage(root);
  const write=vi.spyOn(Storage.prototype,'setItem').mockImplementation(()=>{throw new DOMException('Unavailable','QuotaExceededError');});
  const theme=root.querySelector<HTMLSelectElement>('#theme')!;theme.focus();theme.value='light';theme.dispatchEvent(new Event('change'));
  expect(document.documentElement.dataset.theme).toBe('light');
  expect(root.querySelector('[role=status]')?.textContent).toContain('применены временно');
  expect(document.activeElement).toBe(theme);
  dispose();
  root.innerHTML=settingsPage();
  const nextDispose=mountSettingsPage(root);
  const nextTheme=root.querySelector<HTMLSelectElement>('#theme')!;
  expect(nextTheme.value).toBe('light');
  expect(root.querySelector('[role=status]')?.textContent).toContain('применены временно');
  const nextMotion=root.querySelector<HTMLInputElement>('#motion')!;
  nextMotion.checked=true;nextMotion.dispatchEvent(new Event('change'));
  expect(document.documentElement.dataset.theme).toBe('light');
  write.mockRestore();nextTheme.value='dark';nextTheme.dispatchEvent(new Event('change'));
  expect(root.querySelector('[role=status]')?.textContent).toBe('Настройки сохранены в этом браузере.');
  nextDispose();
});
