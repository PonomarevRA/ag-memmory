import { agentSetupPage } from '../features/agent-setup/page.js';
import type { Route } from '../app/routes.js';
import { memoryStatusPage } from '../features/memory-status/page.js';
import { memoryReaderPage } from '../features/memory-reader/page.js';
import { chatPage } from '../features/chat/page.js';
import { settingsPage } from '../features/settings/page.js';
import { historyPage } from '../features/history/page.js';
import { ui } from '../ui-kit/index.js';
import { knowledgePage } from '../features/knowledge/page.js';

/** Keep each home destination a single native link with a full-card hit area. */
const card = (href: string, title: string, text: string) => ui.card(`<h2><a href="${href}">${title}</a></h2><p>${text}</p>`, 'feature-card');

function aboutPage(): string {
  return `<section class="page-header"><p class="eyebrow">О проекте</p><h1>AgMemory</h1><p>Локальная память для агентов: факты, решения и ссылки остаются в настроенном локальном хранилище, а интерфейс показывает только безопасные проекции.</p></section><section class="feature-grid" aria-label="Границы локального интерфейса">${ui.card('<h2>Что доступно в интерфейсе</h2><p>Чат, wiki, граф и состояние памяти помогают читать и проверять контекст. Wiki может показать безопасную проекцию записи; история навигации и чата остаётся локальным состоянием браузера.</p>')}${ui.card('<h2>Что остаётся вне браузера</h2><p>Настройка хранилища, точного контекста, идентификатора агента и доступа выполняется на стороне процесса. Интерфейс не получает эти конфигурационные значения, необработанное содержимое хранилища или учётные данные.</p>')}${ui.card('<h2>Граница применения</h2><p>Текущий контур предназначен для локальной разработки и диагностики. Он не является моделью производственной авторизации, общей политики пользователей или гарантией безопасной одновременной записи нескольких процессов.</p>')}</section>`;
}

export async function renderPage(route: Route): Promise<string> {
  switch (route.name) {
    case 'home': return `<section class="hero"><p class="eyebrow">Локальная память для агентов</p><h1>Ваш контекст — под рукой</h1><p class="hero__lead">Читайте сохранённые записи, проверяйте источники и находите связи. Подключайте агентов через MCP, чтобы сохранять проверяемый контекст.</p><div class="button-row"><a class="ui-button" href="/memory-reader">Открыть память</a><a class="ui-button ui-button--secondary" href="/connect">Подключить агента</a></div></section><section aria-labelledby="workspace-heading"><header class="home-section-heading"><h2 id="workspace-heading">Рабочее пространство</h2><p class="muted">Выберите, с чем хотите поработать</p></header><div class="feature-grid home-features">${card('/memory-reader','Записи памяти','Читайте факты, решения и ссылки на источники.')}${card('/knowledge','Знания и опыт','Проверяйте классифицированные записи и процедуры.')}${card('/memory-graph','Связи памяти','Исследуйте связи в структурном представлении.')}${card('/memory-status','Состояние сервиса','Проверьте доступность памяти и локальных функций.')}</div></section>`;
    case 'chat': return chatPage(route.params.id);
    case 'status': return memoryStatusPage();
    case 'knowledge': return knowledgePage();
    case 'reader': return memoryReaderPage(route.params.id);
    // The 3D renderer is loaded only for the graph route, keeping Wiki navigation lightweight.
    case 'graph': return (await import('../features/memory-graph/page.js')).memoryGraphPage();
    case 'settings': return settingsPage();
    case 'history': return historyPage();
    case 'connect': return agentSetupPage();
    case 'about': return aboutPage();
    default: return `<section class="page-header"><h1>Страница не найдена</h1><p>Проверьте адрес или <a href="/">вернитесь на главную</a>.</p></section>`;
  }
}
