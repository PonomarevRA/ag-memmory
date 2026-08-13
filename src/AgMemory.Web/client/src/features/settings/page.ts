import { getPreferences, applyPreferences } from '../navigation/browser-state.js';
export function settingsPage(): string {
  const preference = getPreferences();
  queueMicrotask(() => {
    document.querySelector<HTMLSelectElement>('#theme')?.addEventListener('change', event => applyPreferences({ theme: (event.target as HTMLSelectElement).value as 'system' | 'light' | 'dark', reduceMotion: document.querySelector<HTMLInputElement>('#motion')?.checked }));
    document.querySelector<HTMLInputElement>('#motion')?.addEventListener('change', event => applyPreferences({ theme: document.querySelector<HTMLSelectElement>('#theme')?.value as 'system' | 'light' | 'dark', reduceMotion: (event.target as HTMLInputElement).checked }));
  });
  return `<section class="page-header"><p class="eyebrow">Локальные предпочтения</p><h1>Настройки интерфейса</h1><p>Настройки сохраняются только в браузере.</p><article class="ui-card settings-card"><label>Тема <select id="theme"><option value="system"${preference.theme==='system'?' selected':''}>Системная</option><option value="light"${preference.theme==='light'?' selected':''}>Светлая</option><option value="dark"${preference.theme==='dark'?' selected':''}>Тёмная</option></select></label><label><input id="motion" type="checkbox"${preference.reduceMotion?' checked':''}/> Уменьшить движение</label></article></section>`;
}
