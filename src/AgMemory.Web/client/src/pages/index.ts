import type { Route } from '../app/routes.js';
import { memoryStatusPage } from '../features/memory-status/page.js';
import { memoryReaderPage } from '../features/memory-reader/page.js';
import { memoryGraphPage } from '../features/memory-graph/page.js';
import { chatPage } from '../features/chat/page.js';
import { settingsPage } from '../features/settings/page.js';
import { historyPage } from '../features/history/page.js';
import { ui } from '../ui-kit/index.js';

const card = (href: string, title: string, text: string) => ui.card(`<h2><a href="${href}">${title}</a></h2><p>${text}</p>`, 'feature-card');

function aboutPage(): string {
  return `<section class="page-header"><p class="eyebrow">О проекте</p><h1>AgMemory</h1><p>Локальная память для агентов: факты, решения и ссылки остаются в настроенном локальном хранилище, а интерфейс показывает только безопасные проекции.</p></section><section class="feature-grid" aria-label="Границы локального интерфейса">${ui.card('<h2>Что доступно в интерфейсе</h2><p>Чат, wiki, граф и состояние памяти помогают читать и проверять контекст. Wiki может показать безопасную проекцию записи; история навигации и чата остаётся локальным состоянием браузера.</p>')}${ui.card('<h2>Что остаётся вне браузера</h2><p>Настройка хранилища, точного контекста, идентификатора агента и доступа выполняется на стороне процесса. Интерфейс не получает эти конфигурационные значения, необработанное содержимое хранилища или учётные данные.</p>')}${ui.card('<h2>Граница применения</h2><p>Текущий контур предназначен для локальной разработки и диагностики. Он не является моделью производственной авторизации, общей политики пользователей или гарантией безопасной одновременной записи нескольких процессов.</p>')}</section>`;
}

function connectPage(): string {
  return `<section class="page-header"><p class="eyebrow">Интеграция</p><h1>Подключение агента</h1><p>Подключайте Codex, Cursor или Claude через отдельный локальный MCP-процесс. Учётные данные и параметры подключения в браузер не вводятся и не передаются.</p></section><section class="feature-grid" aria-label="Порядок подключения агента">${ui.card('<h2>Три операции агента</h2><p><code>memory_status</code> проверяет доступность настроенной памяти; <code>memory_remember</code> сохраняет подтверждённый факт; <code>memory_recall</code> возвращает подходящий контекст в границах настроенной памяти.</p>')}${ui.card('<h2>Настройка процесса</h2><p>Для Codex, Cursor и Claude используйте одинаковую схему регистрации MCP, подставив утверждённые значения только в конфигурацию процесса MCP. Хранилище и точный контекст фиксируются там, а не передаются агентом при каждом вызове. Для намеренно общей памяти используйте одну настройку хранилища и контекста; каждому клиенту назначайте отдельное происхождение, чтобы действия оставались различимыми.</p><pre><code>AGMEMORY_STORAGE_PATH=YOUR_LOCAL_STORAGE_PATH\nAGMEMORY_ACTOR_ID=YOUR_ACTOR_ID\nAGMEMORY_TENANT_ID=YOUR_TENANT_ID\nAGMEMORY_CLIENT_ID=YOUR_CLIENT_ID</code></pre>')}${ui.card('<h2>Ручная проверка</h2><ol><li>Запустите <code>memory_status</code>.</li><li>Сохраните тестовый подтверждённый факт через <code>memory_remember</code>.</li><li>Вызовите <code>memory_recall</code> и убедитесь, что результат соответствует этой же настроенной памяти.</li></ol><p>Конкретные значения задавайте только в конфигурации вашей среды.</p>')}</section>`;
}
export async function renderPage(route: Route): Promise<string> {
  switch (route.name) {
    case 'home': return `<section class="hero"><p class="eyebrow">Платформа памяти для агентов</p><h1>Контекст, который остаётся проверяемым.</h1><p class="hero__lead">AgMemory хранит компактные факты, решения и ссылки на источники. История интерфейса остаётся локально в браузере.</p><div class="button-row"><a class="ui-button" href="/chat">Начать чат</a><a class="ui-button ui-button--secondary" href="/connect">Подключить агента</a></div></section><section class="feature-grid">${card('/chat','Чат','Модель через серверный gateway.')}${card('/memory-reader','Читать память','Wiki-представление локальных записей.')}${card('/memory-graph','Граф памяти','Безопасная структурная проекция.')}${card('/memory-status','Состояние памяти','Агрегированная локальная диагностика.')}</section>`;
    case 'chat': return chatPage(route.params.id);
    case 'status': return memoryStatusPage();
    case 'reader': return memoryReaderPage(route.params.id);
    case 'graph': return memoryGraphPage();
    case 'settings': return settingsPage();
    case 'history': return historyPage();
    case 'connect': return connectPage();
    case 'about': return aboutPage();
    default: return `<section class="page-header"><h1>Страница не найдена</h1><p>Проверьте адрес или <a href="/">вернитесь на главную</a>.</p></section>`;
  }
}
