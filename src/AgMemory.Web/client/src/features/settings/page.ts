import { getPreferences, applyPreferences, arePreferencesTemporary } from '../navigation/browser-state.js';
const temporaryNotice = 'Настройки применены временно. Браузер не разрешил сохранить их; после перезагрузки они могут сброситься.';

/** Renders effective local preferences without binding controls before the route DOM exists. */
export function settingsPage(): string {
  const preference = getPreferences();
  return `<section class="page-header"><p class="eyebrow">Локальные предпочтения</p><h1>Настройки интерфейса</h1><p>Настройки применяются сразу и сохраняются в этом браузере, если локальное хранилище доступно.</p><article class="ui-card settings-card"><label class="ag-field">Тема <select id="theme" class="ag-input"><option value="system"${preference.theme==='system'?' selected':''}>Системная</option><option value="light"${preference.theme==='light'?' selected':''}>Светлая</option><option value="dark"${preference.theme==='dark'?' selected':''}>Тёмная</option></select></label><label><input id="motion" type="checkbox"${preference.reduceMotion?' checked':''}/> Уменьшить движение</label></article><p id="preferences-status" class="muted" role="status" aria-live="polite">${arePreferencesTemporary() ? temporaryNotice : ''}</p></section>`;
}

/** Binds this committed settings subtree and returns cleanup for route replacement. */
export function mountSettingsPage(root: HTMLElement): () => void {
  const theme = root.querySelector<HTMLSelectElement>('#theme');
  const motion = root.querySelector<HTMLInputElement>('#motion');
  if (!theme || !motion) return () => {};

  const update = () => {
    const result = applyPreferences({
      theme: theme.value as 'system' | 'light' | 'dark',
      reduceMotion: motion.checked
    });
    const status = root.querySelector<HTMLElement>('#preferences-status');
    if (status) status.textContent = result.persisted
      ? 'Настройки сохранены в этом браузере.'
      : temporaryNotice;
  };
  theme.addEventListener('change', update);
  motion.addEventListener('change', update);
  return () => {
    theme.removeEventListener('change', update);
    motion.removeEventListener('change', update);
  };
}
