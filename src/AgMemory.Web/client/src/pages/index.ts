import { agentSetupPage } from '../features/agent-setup/page.js';
import type { Route } from '../app/routes.js';
import { memoryStatusPage } from '../features/memory-status/page.js';
import { memoryReaderPage } from '../features/memory-reader/page.js';
import { settingsPage } from '../features/settings/page.js';
import { historyPage } from '../features/history/page.js';
import { ui } from '../ui-kit/index.js';
import { knowledgePage } from '../features/knowledge/page.js';

/** Keep each home destination a single native link with a full-card hit area. */
const card = (href: string, title: string, text: string) => ui.card(`<h2><a href="${href}">${title}</a></h2><p>${text}</p>`, 'feature-card');

function aboutPage(): string {
  return `<section class="page-header"><p class="eyebrow">О проекте</p><h1>AgMemory</h1><p>Локальная память для агентов: факты, решения и ссылки остаются в настроенном локальном хранилище, а интерфейс показывает только безопасные проекции.</p></section><section class="feature-grid" aria-label="Границы локального интерфейса">${ui.card('<h2>Что доступно в интерфейсе</h2><p>Обозреватель, граф и каталог знаний помогают найти записи, прочитать процедуры и проверить связи. История переходов хранится локально в браузере.</p>')}${ui.card('<h2>Что остаётся вне браузера</h2><p>Настройка хранилища, точного контекста, идентификатора агента и доступа выполняется на стороне процесса. Интерфейс не получает эти конфигурационные значения, необработанное содержимое хранилища или учётные данные.</p>')}${ui.card('<h2>Граница применения</h2><p>Текущий контур предназначен для локальной разработки и диагностики. Он не является моделью производственной авторизации, общей политики пользователей или гарантией безопасной одновременной записи нескольких процессов.</p>')}</section>`;
}

export async function renderPage(route: Route): Promise<string> {
  switch (route.name) {
    case 'home': return `<section class="hero memory-home" data-ag-width="fill"><p class="eyebrow">Локальная память для агентов</p><h1>Память проекта</h1><p class="hero__lead" data-ag-width="content">Найдите запись, проверьте её источник или откройте процедуру для повторяющейся задачи.</p><div class="button-row"><a class="ui-button" href="/memory-reader">Найти запись</a><a class="ui-button ui-button--secondary" href="/connect">Подключить агента</a></div></section><section class="memory-home-workspace" aria-labelledby="workspace-heading" data-ag-width="fill"><header class="home-section-heading"><h2 id="workspace-heading">Работа с памятью</h2></header><div class="feature-grid home-features">${card('/knowledge','Найти процедуру','Фильтруйте знания и открывайте шаги и предусловия.')}${card('/memory-graph','Исследовать связи','Откройте граф, чтобы перейти от связи к записи.')}${card('/memory-status','Проверить хранилище','Посмотрите, доступна ли память и сколько записей активно.')}</div></section>`;
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
