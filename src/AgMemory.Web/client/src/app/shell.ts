import {bindWindowShell} from '@ag-design/components';
import {routeHistory} from './route-history.js';
import { escapeHtml } from '../shared/html.js';
import type { AppVersion } from '../features/app-version/api.js';

// The project has no icon package. Local SVG paths keep collapsed links legible without font-dependent glyphs.
const navigationIcons = ['M3 10 12 3l9 7v11H3z M9 21v-8h6v8', 'M4 3h16v18H4z M8 7h8 M8 11h8 M8 15h5', 'M12 3 3 9l9 12 9-12z M3 9h18 M12 3v18', 'M4 3h16v18H4z M8 7h8 M8 11h8 M8 15h8', 'M3 12h4l3-7 4 14 3-7h4', 'M8 3H3v18h18v-5 M10 14 21 3 M14 3h7v7', 'M12 8v1 M12 12v5 M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18'];
const settingsIcon = 'M12 3v4 M12 17v4 M3 12h4 M17 12h4 M6 6l3 3 M15 15l3 3 M6 18l3-3 M15 9l3-3 M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8';

/** Emits one decorative local icon; link labels and titles supply its accessible meaning. */
function navigationIcon(path: string): string {
  return `<svg class="primary-nav__icon" aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="${path}"/></svg>`;
}
let nativeSidebarInstalled = false;
let disposeWindowShell: (() => void) | undefined;

const links = [['/', 'Главная'], ['/memory-reader', 'Читать память'], ['/memory-graph', 'Граф памяти'], ['/knowledge', 'Знания и процедуры'], ['/memory-status', 'Состояние'], ['/connect', 'Подключить'], ['/about', 'О проекте']];

/** Keep navigation separate from the contextual header in both shells; narrow browsers use the existing drawer. */
export function shell(content: string, path: string, version: AppVersion | null = null): string {
  const nav = links.map(([href, label], index) => `<a href="${href}" title="${label}" aria-label="${label}" class="${path === href || (href !== '/' && path.startsWith(href)) ? 'active' : ''}"${path === href || (href !== '/' && path.startsWith(href)) ? ' aria-current="page"' : ''}>${navigationIcon(navigationIcons[index])}<span class="primary-nav__label">${label}</span></a>`).join('');
  const versionLabel = version
    ? `<span class="brand-version" title="${escapeHtml(version.informationalVersion)}">${escapeHtml(version.version)}</span>`
    : '';
  const nativeLayout = document.documentElement.classList.contains('native-desktop');
  const sidebarTools = nativeLayout ? '' : `<div class="primary-nav__tools"><span>Разделы</span><button id="browser-sidebar-toggle" class="ui-icon-button" type="button" aria-controls="primary-navigation" aria-expanded="true" aria-label="Свернуть меню" title="Свернуть меню">${navigationIcon('M4 4h16v16H4z M9 4v16')}</button></div>`;
  const navigation = `<nav id="primary-navigation" class="primary-nav" aria-label="Основная навигация">${sidebarTools}<button id="menu-close" class="primary-nav__close ui-icon-button" type="button" aria-label="Закрыть навигацию">×</button>${nav}</nav>`;
  disposeWindowShell?.();
  disposeWindowShell = undefined;
  queueMicrotask(wireMobileNavigation);
  return `<a class="skip-link" href="#main">К содержанию</a><div class="app-shell"><header class="app-header"><a class="brand" href="/" aria-label="AgMemory"><span class="brand-mark" aria-hidden="true"><img src="/assets/brand/agmemory-icon.svg" alt=""></span><span class="brand-name">AgMemory</span>${versionLabel}</a><button id="menu-toggle" class="header-menu-toggle ui-icon-button" type="button" aria-label="Открыть навигацию" aria-controls="primary-navigation" aria-expanded="false">Меню</button><div class="header-actions"><a class="ui-button ui-button--ghost" href="/settings" title="Настройки" aria-label="Настройки">${navigationIcon(settingsIcon)}<span class="primary-nav__label">Настройки</span></a></div></header>${navigation}<main id="main" class="app-main" tabindex="-1">${content}</main><footer class="app-footer"><span>Локальная платформа памяти для агентов</span><a href="/about">О проекте</a></footer></div>`;
}

/** Registers one native event listener across SPA rerenders; browser pages keep their existing navigation. */
function installNativeSidebar(): void {
  if (nativeSidebarInstalled) return;
  nativeSidebarInstalled = true;
  try {
    document.documentElement.classList.toggle('sidebar-collapsed', localStorage.getItem('agmemory.sidebarCollapsed') === 'true');
  } catch { /* Restricted storage must not prevent an ephemeral sidebar toggle. */ }
  window.addEventListener('agmemory:toggle-sidebar', () => {
    const root = document.documentElement;
    if (!root.classList.contains('native-toolbar-ready')) return;
    const collapsed = root.classList.toggle('sidebar-collapsed');
    try { localStorage.setItem('agmemory.sidebarCollapsed', String(collapsed)); }
    catch { /* The root class still works for this window even when persistence is unavailable. */ }
  });
}

/** Binds the mobile drawer to its current route subtree; native collapse state stays on the root element. */
function wireMobileNavigation(): void {
  installNativeSidebar();
  wireBrowserSidebar();
  const header = document.querySelector<HTMLElement>('.app-header');
  if (header) {
    const browserHistory = routeHistory();
    const commands = document.createElement('span'); commands.hidden = true;
    for (const key of ['back','forward'] as const) {
      const button = document.createElement('button'); button.id = 'route-history-' + key;
      button.disabled = !browserHistory[key];
      button.onclick = () => { if (!button.disabled) history.go(key === 'back' ? -1 : 1); };
      commands.append(button);
    }
    header.append(commands);
    const titleSource = document.querySelector('#primary-navigation [aria-current="page"]') && !location.pathname.startsWith('/memory-reader/') ? '#primary-navigation [aria-current="page"]' : '#main h1';
    const controller = bindWindowShell({identity:'AgMemory',header,titleSource,navigation:'#primary-navigation [aria-current="page"]',existingBack:'#route-history-back',existingForward:'#route-history-forward',actions:[{selector:'#menu-toggle',id:'primary'},{selector:'.header-actions a[href="/settings"]',id:'settings'}],panels:[
      ...(document.documentElement.classList.contains('native-desktop') ? [{selector:'#primary-navigation',edge:'right' as const,variable:'--memory-nav-width',layout:':root',minWidth:200}] : []),
      {selector:'.memory-graph-inspector',edge:'left',variable:'--memory-inspector-width',layout:':root'},
      {selector:'.memory-reader-tree',edge:'right',variable:'--memory-tree-width',layout:':root'}
    ]});
    // Move the real menu control into the shared toolbar; its controller restores the original node on disposal.
    const menuControl = header.querySelector<HTMLButtonElement>('#menu-toggle');
    const navigationHost = header.querySelector('.ag-window-toolbar__navigation');
    if (menuControl && navigationHost) navigationHost.prepend(menuControl);
    disposeWindowShell = () => { controller.dispose(); commands.remove(); };
  }
  const toggle = document.querySelector<HTMLButtonElement>('#menu-toggle');
  const close = document.querySelector<HTMLButtonElement>('#menu-close');
  const nav = document.querySelector<HTMLElement>('#primary-navigation');
  if (!toggle || !close || !nav) return;
  let returnFocus: HTMLElement = toggle;
  const closeMenu = (restoreFocus = false) => {
    nav.classList.remove('primary-nav--open');
    toggle.setAttribute('aria-expanded', 'false');
    if (!restoreFocus || !returnFocus.isConnected) return;
    if (returnFocus.closest('[hidden], [inert]') || returnFocus.getClientRects().length === 0) return;
    returnFocus.focus();
  };
  const openMenu = () => {
    returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : toggle;
    nav.classList.add('primary-nav--open');
    toggle.setAttribute('aria-expanded', 'true');
    close.focus();
  };
  toggle.addEventListener('click', openMenu);
  close.addEventListener('click', () => closeMenu(true));
  nav.addEventListener('click', event => {
    if ((event.target as Element).closest('a')) closeMenu();
  });
  nav.addEventListener('keydown', event => {
    if (event.key === 'Tab' && nav.classList.contains('primary-nav--open') && getComputedStyle(nav).position === 'fixed') {
      // Only the fixed mobile drawer traps focus; desktop navigation must allow Tab to leave.
      // Resizing may hide the close button, so exclude controls that stopped being interactive.
      const controls = Array.from(nav.querySelectorAll<HTMLElement>('a[href], button:not([disabled]):not(#browser-sidebar-toggle)'))
        .filter(control => !control.closest('[hidden], [inert]') && control.getClientRects().length > 0);
      const first = controls[0], last = controls[controls.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    }
    if (event.key === 'Escape' && nav.classList.contains('primary-nav--open')) {
      event.preventDefault();
      closeMenu(true);
    }
  });
}


/** Reuse the persisted sidebar preference in browsers; native commands keep their capability guard. */
function wireBrowserSidebar(): void {
  const control = document.querySelector<HTMLButtonElement>('#browser-sidebar-toggle');
  if (!control) return;
  const root = document.documentElement;
  const update = () => {
    const collapsed = root.classList.contains('sidebar-collapsed');
    const label = collapsed ? 'Развернуть меню' : 'Свернуть меню';
    control.setAttribute('aria-expanded', String(!collapsed));
    control.setAttribute('aria-label', label);
    control.title = label;
  };
  update();
  control.addEventListener('click', () => {
    const collapsed = root.classList.toggle('sidebar-collapsed');
    try { localStorage.setItem('agmemory.sidebarCollapsed', String(collapsed)); }
    catch { /* Ephemeral navigation remains usable when preference storage is unavailable. */ }
    update();
  });
}
