import { load, type MemoryStatusResponse } from './api.js';
export async function memoryStatusPage(): Promise<string> {
  const state = await load();
  if (state.status !== 'available') return `<section class="memory-status-page"><header class="page-header"><p class="eyebrow">Локальная диагностика</p><h1>Состояние памяти</h1></header><div class="status-banner status-banner--warning"><span class="status-banner__icon" aria-hidden="true">!</span><div><h2>Состояние памяти недоступно</h2><p>Диагностика доступна только из loopback в Development при явно настроенном локальном scope.</p></div></div></section>`;
  return available(state);
}
function available(s: MemoryStatusResponse): string { const metric=(n:string,v:number)=>`<article class="memory-status-metric"><p>${n}</p><strong>${v}</strong></article>`; return `<section class="memory-status-page"><header class="page-header"><p class="eyebrow">Локальная диагностика</p><h1>Состояние памяти</h1></header><div class="memory-status-grid">${metric('Всего записей',s.totalMemoryCount)}${metric('Доступны для recall',s.activeMemoryCount)}${metric('Истекли',s.expiredMemoryCount)}${metric('Неактивны',s.inactiveMemoryCount)}</div><p class="small-copy">Страница не показывает текст записей, scope, actor или постоянные идентификаторы.</p></section>`; }
