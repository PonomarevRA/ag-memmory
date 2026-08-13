import { load } from './sanitize.js';
import { dispose, initialize, render, reset, zoomIn, zoomOut } from './page-controller.js';

export async function memoryGraphPage(): Promise<string> {
  const graph = await load();
  if (graph.status !== 'available') return `<section class="memory-graph-page"><header class="page-header"><p class="eyebrow">Структурная проекция</p><h1>Граф памяти</h1></header><div class="status-banner status-banner--warning"><span class="status-banner__icon" aria-hidden="true">!</span><div><h2>Граф недоступен</h2><p>Настройте локальное хранилище и scope на сервере.</p></div></div></section>`;
  graphSnapshot = graph;
  return `<section class="memory-graph-page"><header class="page-header"><p class="eyebrow">Структурная проекция</p><h1>Граф памяти</h1></header><section class="memory-graph-card"><div class="memory-graph-toolbar" aria-label="Управление масштабом графа"><button id="graph-zoom-in" class="ui-button" type="button">Увеличить</button><button id="graph-zoom-out" class="ui-button" type="button">Уменьшить</button><button id="graph-reset" class="ui-button ui-button--secondary" type="button">Сбросить</button></div><div class="memory-graph-stage"><div class="memory-graph-scene"><canvas id="memory-graph-canvas" class="memory-graph-canvas" tabindex="0" role="img" aria-label="Интерактивная трёхмерная карта памяти" aria-describedby="memory-graph-selection"></canvas><canvas id="memory-graph-fallback" class="memory-graph-canvas" hidden aria-hidden="true"></canvas></div><aside class="memory-graph-inspector"><h2>Выбор</h2><p id="memory-graph-selection" class="memory-graph-selection" aria-live="polite">Выберите узел на карте.</p></aside></div></section><section class="memory-graph-summary"><h2>Текстовая сводка</h2><ul>${graph.nodes.map(n=>`<li><a href="${n.href}">${n.label}</a> · ${n.type}</li>`).join('')}</ul><p>Связей: ${graph.edges.length}</p></section></section>`;
}

let graphSnapshot: Awaited<ReturnType<typeof load>> | undefined;
/** Called by the router after it has mounted this page's canvases into the document. */
export function mountMemoryGraphPage(): void {
  const canvas = document.querySelector<HTMLCanvasElement>('#memory-graph-canvas');
  const fallback = document.querySelector<HTMLCanvasElement>('#memory-graph-fallback');
  const selection = document.querySelector<HTMLElement>('#memory-graph-selection');
  if (!canvas || !fallback || !selection || !graphSnapshot) return;
  initialize(canvas, fallback, selection); render(graphSnapshot);
  document.querySelector('#graph-zoom-in')?.addEventListener('click', zoomIn);
  document.querySelector('#graph-zoom-out')?.addEventListener('click', zoomOut);
  document.querySelector('#graph-reset')?.addEventListener('click', reset);
  window.addEventListener('pagehide', dispose, { once: true });
}
