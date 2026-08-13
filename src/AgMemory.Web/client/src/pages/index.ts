import type { Route } from '../app/routes.js';
import { memoryStatusPage } from '../features/memory-status/page.js';
import { memoryReaderPage } from '../features/memory-reader/page.js';
import { memoryGraphPage } from '../features/memory-graph/page.js';
import { chatPage } from '../features/chat/page.js';
import { settingsPage } from '../features/settings/page.js';
import { historyPage } from '../features/history/page.js';

const card = (href: string, title: string, text: string) => `<article class="ui-card"><h2><a href="${href}">${title}</a></h2><p>${text}</p></article>`;
export async function renderPage(route: Route): Promise<string> {
  switch (route.name) {
    case 'home': return `<section class="hero"><p class="eyebrow">Платформа памяти для агентов</p><h1>Контекст, который остаётся проверяемым.</h1><p class="hero__lead">AgMemory хранит компактные факты, решения и ссылки на источники. История интерфейса остаётся локально в браузере.</p><div class="button-row"><a class="ui-button" href="/chat">Начать чат</a><a class="ui-button ui-button--secondary" href="/connect">Подключить агента</a></div></section><section class="feature-grid">${card('/chat','Чат','Модель через серверный gateway.')}${card('/memory-reader','Читать память','Wiki-представление локальных записей.')}${card('/memory-graph','Граф памяти','Безопасная структурная проекция.')}${card('/memory-status','Состояние памяти','Агрегированная локальная диагностика.')}</section>`;
    case 'chat': return chatPage(route.params.id);
    case 'status': return memoryStatusPage();
    case 'reader': return memoryReaderPage(route.params.id);
    case 'graph': return memoryGraphPage();
    case 'settings': return settingsPage();
    case 'history': return historyPage();
    case 'connect': return `<section class="page-header"><p class="eyebrow">Интеграция</p><h1>Подключение агента</h1><p>Настройте локальный агент и exact scope вне браузера. Учётные данные никогда не вводятся в этот интерфейс.</p></section>`;
    case 'about': return `<section class="page-header"><p class="eyebrow">О проекте</p><h1>AgMemory</h1><p>Локальная память агентов с безопасными API-проекциями и независимой браузерной историей.</p></section>`;
    case 'ui-kit': return `<section class="page-header"><h1>UI kit</h1><p>Компоненты интерфейса Vite SPA.</p></section>`;
    default: return `<section class="page-header"><h1>Страница не найдена</h1><p>Проверьте адрес или <a href="/">вернитесь на главную</a>.</p></section>`;
  }
}
