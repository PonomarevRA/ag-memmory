const links = [['/', 'Главная'], ['/chat', 'Чат'], ['/memory-reader', 'Читать память'], ['/memory-graph', 'Граф памяти'], ['/memory-status', 'Состояние'], ['/connect', 'Подключить'], ['/about', 'О проекте']];

export function shell(content: string, path: string): string {
  const nav = links.map(([href, label]) => `<a href="${href}"${path === href || (href !== '/' && path.startsWith(href)) ? ' aria-current="page"' : ''}>${label}</a>`).join('');
  return `<a class="skip-link" href="#main">К содержанию</a><header class="site-header"><a class="brand" href="/">AgMemory</a><nav aria-label="Основная навигация">${nav}</nav><a href="/settings">Настройки</a></header><main id="main" tabindex="-1">${content}</main><footer class="site-footer">Локальная платформа памяти для агентов</footer>`;
}
