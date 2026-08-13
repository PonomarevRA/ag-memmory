const links = [['/', 'Главная'], ['/chat', 'Чат'], ['/memory-reader', 'Читать память'], ['/memory-graph', 'Граф памяти'], ['/memory-status', 'Состояние'], ['/connect', 'Подключить'], ['/about', 'О проекте']];

export function shell(content: string, path: string): string {
  const nav = links.map(([href, label]) => `<a href="${href}" class="${path === href || (href !== '/' && path.startsWith(href)) ? 'active' : ''}"${path === href || (href !== '/' && path.startsWith(href)) ? ' aria-current="page"' : ''}>${label}</a>`).join('');
  queueMicrotask(wireMobileNavigation);
  return `<a class="skip-link" href="#main">К содержанию</a><div class="app-shell"><header class="app-header"><a class="brand" href="/"><span class="brand-mark" aria-hidden="true"><img src="/assets/brand/agmemory-icon.svg" alt=""></span><span>AgMemory</span></a><button id="menu-toggle" class="header-menu-toggle ui-icon-button" type="button" aria-label="Открыть навигацию" aria-controls="primary-navigation" aria-expanded="false">Меню</button><nav id="primary-navigation" class="primary-nav" aria-label="Основная навигация"><button id="menu-close" class="primary-nav__close ui-icon-button" type="button" aria-label="Закрыть навигацию">×</button>${nav}</nav><div class="header-actions"><a class="ui-button ui-button--secondary" href="/settings">Настройки</a></div></header><main id="main" class="app-main" tabindex="-1">${content}</main><footer class="app-footer"><span>Локальная платформа памяти для агентов</span><a href="/about">О проекте</a></footer></div>`;
}

function wireMobileNavigation(): void {
  const toggle = document.querySelector<HTMLButtonElement>('#menu-toggle');
  const close = document.querySelector<HTMLButtonElement>('#menu-close');
  const nav = document.querySelector<HTMLElement>('#primary-navigation');
  if (!toggle || !close || !nav) return;
  const closeMenu = () => {
    nav.classList.remove('primary-nav--open');
    toggle.setAttribute('aria-expanded', 'false');
  };
  const openMenu = () => {
    nav.classList.add('primary-nav--open');
    toggle.setAttribute('aria-expanded', 'true');
    close.focus();
  };
  toggle.addEventListener('click', openMenu);
  close.addEventListener('click', () => { closeMenu(); toggle.focus(); });
  nav.addEventListener('click', event => {
    if ((event.target as Element).closest('a')) closeMenu();
  });
  nav.addEventListener('keydown', event => {
    if (event.key === 'Escape' && nav.classList.contains('primary-nav--open')) {
      closeMenu();
      toggle.focus();
    }
  });
}
