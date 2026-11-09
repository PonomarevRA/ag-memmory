import { escapeHtml as e } from '../../shared/html.js';
import { load, mergePortion, typeColor, typeLabel, MAX_NODES, MAX_EDGES, type GraphNode, type GraphSnapshot } from './sanitize.js';
import * as graphView from './page-controller.js';
import { loadCards, type ReaderCard } from './cards.js';

type PageState = { root: HTMLElement; snapshot: GraphSnapshot; selectedId?: string; search: string; type: string; neighborhood: boolean; loading: boolean; epoch: number; requestedArea: string | null; switching?: boolean; cards: Map<string, ReaderCard>; attempted: Set<string>; contentEpoch: number; cardController?: AbortController; cardLoading: boolean; cardUnavailable: boolean; interacted: boolean; initialFocusDone: boolean };
let initial: GraphSnapshot | undefined;
let active: PageState | undefined;
let requestEpoch = 0;

/** Loads only the fixed server-configured graph; supplied area hints remain subject to endpoint validation. */
export async function memoryGraphPage(): Promise<string> {
  const epoch = ++requestEpoch;
  const graph = await load(null, areaFromUrl());
  if (epoch !== requestEpoch) return '';
  initial = graph;
  return `<section class="memory-graph-page">
    <header class="page-header graph-page-header"><div><p class="eyebrow">Исследование памяти</p><h1>Граф памяти</h1>
    <p>Найдите запись, прочитайте её и исследуйте показанные связи.</p></div><a class="ui-button ui-button--secondary" href="${e(graph.areaId ? `/memory-reader?area=${graph.areaId}` : '/memory-reader')}">Читать память</a></header>
    <div id="memory-graph-root">
    <section class="memory-graph-card">
    <div class="graph-discovery-bar" data-ag-form-row="auto">
      <label data-ag-form-field>Найти на загруженной карте<input id="graph-search" class="ui-input" maxlength="120" type="search" placeholder="Название, текст или обозначение"></label>
      <label data-ag-form-field>Тип записи<select id="graph-type"><option value="">Все типы</option></select></label>
      <button id="graph-refresh" type="button" class="ui-button ui-button--secondary">Обновить</button>
    </div>
    <div id="graph-overview" class="graph-overview" aria-label="Сводка загруженной карты"></div>
    <div class="memory-graph-toolbar">
      <div class="memory-graph-view-switch" role="group" aria-label="Вид карты">
      <button id="graph-view-2d" type="button" class="ui-button" aria-pressed="true">Карта</button>
      <button id="graph-view-3d" type="button" class="ui-button ui-button--secondary" aria-pressed="false">3D</button></div>
      <button id="graph-show-all" class="ui-button ui-button--secondary" type="button">Все записи</button><label class="graph-focus-toggle"><input id="graph-neighborhood" type="checkbox" disabled> Только ближайшие соседи</label>
      <div class="memory-graph-zoom" role="group" aria-label="Масштаб карты">
      <button id="graph-zoom-out" class="ui-button ui-button--secondary" type="button" aria-label="Уменьшить карту">−</button>
      <button id="graph-zoom-in" class="ui-button ui-button--secondary" type="button" aria-label="Увеличить карту">+</button>
      <button id="graph-reset" class="ui-button ui-button--secondary" type="button">Вместить карту</button></div>
    </div>
    <p id="graph-view-notice" class="memory-graph-view-notice" role="status"></p>
    <div class="memory-graph-stage">
      <div class="memory-graph-scene">
      <canvas id="memory-graph-fallback" class="memory-graph-canvas" tabindex="0" role="img" aria-label="Двумерная карта связей памяти" aria-describedby="graph-help graph-progress"></canvas>
      <canvas id="memory-graph-canvas" class="memory-graph-canvas" tabindex="0" role="img" hidden aria-hidden="true" aria-label="Объёмная карта связей памяти" aria-describedby="graph-help graph-progress"></canvas>
      </div>
      <aside class="memory-graph-inspector" aria-label="Выбранная запись">
        <div id="graph-inspector-content" aria-live="polite"></div>
        <div class="memory-graph-selection-actions"><button id="graph-focus" type="button" class="ui-button" disabled>Рассмотреть связи</button>
        <button id="graph-clear" type="button" class="ui-button ui-button--secondary" disabled>Снять выбор</button></div>
        <div id="memory-graph-selection" hidden aria-hidden="true"></div>
      </aside>
    </div>
    <div class="graph-status-bar"><p id="graph-progress" role="status"></p>
      <p id="graph-card-status" role="status"></p><button id="graph-cards-retry" class="ui-button ui-button--secondary" type="button" hidden>Повторить загрузку названий</button>
      <div id="graph-portion-message" role="status"></div></div>
    <details class="memory-graph-explanation"><summary>Как читать эту карту</summary>
      <p id="graph-help">Линия показывает общие сущности — это не причинная связь. Толщина отражает их количество. Группы объединяют только связанные записи; расстояние не означает сходство.</p>
      <p class="small-copy">Поиск работает только по загруженным названиям, фрагментам и обозначениям. Колесо и кнопки меняют масштаб; перетаскивание перемещает карту. В 2D стрелки выбирают записи, Shift + стрелки перемещают карту, Escape снимает выбор. Для полного текста откройте Wiki.</p>
      <div id="graph-type-legend" class="memory-graph-type-legend" aria-label="Типы записей"></div>
    </details>
    </section>
    <section class="memory-graph-summary" aria-label="Записи на карте"><div class="graph-list-heading"><h2>Записи на карте</h2><span id="graph-list-count" class="small-copy"></span></div>
    <ul id="graph-node-list" class="memory-graph-summary__nodes"></ul>
    <div class="memory-graph-pagination"><button id="graph-more" type="button" class="ui-button">Загрузить ещё записи</button></div>
    </section></div></section>`;
}

/** Releases page callbacks and prevents any pending portion from touching another route. */
export function disposeMemoryGraph(): void {
  requestEpoch++;
  active?.cardController?.abort();
  active = undefined; initial = undefined;
  window.removeEventListener('pagehide', disposeMemoryGraph);
  graphView.dispose();
}

/** Mounts stable controls once; portions update the bounded snapshot and never replace the focused search field. */
export function mountMemoryGraphPage(): void {
  const root = document.querySelector<HTMLElement>('#memory-graph-root');
  const canvas = root?.querySelector<HTMLCanvasElement>('#memory-graph-canvas');
  const flat = root?.querySelector<HTMLCanvasElement>('#memory-graph-fallback');
  const summary = root?.querySelector<HTMLElement>('#memory-graph-selection');
  if (!root || !canvas || !flat || !summary || !initial) return;
  const state: PageState = { root, snapshot: initial, search: '', type: '', neighborhood: false, loading: false, epoch: requestEpoch, requestedArea: areaFromUrl(), cards: new Map(), attempted: new Set(), contentEpoch: 0, cardLoading: false, cardUnavailable: false, interacted: false, initialFocusDone: false };
  active = state;
  root.addEventListener('pointerdown', () => { state.interacted = true; }, { capture: true });
  root.addEventListener('keydown', () => { state.interacted = true; }, { capture: true });
  graphView.initialize(canvas, flat, summary, node => { if (!state.switching || node) select(state, node); }, (view, unavailable) => updateView(state, view, unavailable));
  root.querySelector('#graph-zoom-in')?.addEventListener('click', graphView.zoomIn);
  root.querySelector('#graph-zoom-out')?.addEventListener('click', graphView.zoomOut);
  root.querySelector('#graph-reset')?.addEventListener('click', () => {
    state.switching = true; graphView.reset();
    if (state.selectedId) graphView.selectNode(state.selectedId);
    state.switching = false;
  });
  root.querySelector('#graph-focus')?.addEventListener('click', () => focusNeighborhood(state));
  root.querySelector('#graph-show-all')?.addEventListener('click', () => { clearFilters(state); state.neighborhood = false; update(state); });
  root.querySelector('#graph-cards-retry')?.addEventListener('click', () => { state.attempted.clear(); state.cardUnavailable = false; void hydrateCards(state); });
  root.querySelector('#graph-clear')?.addEventListener('click', () => { graphView.selectNode(''); select(state); });
  root.querySelector('#graph-view-2d')?.addEventListener('click', () => void changeView(state, '2d'));
  root.querySelector('#graph-view-3d')?.addEventListener('click', () => void changeView(state, '3d'));
  root.querySelector<HTMLInputElement>('#graph-neighborhood')?.addEventListener('change', event => { if ((event.target as HTMLInputElement).checked) focusNeighborhood(state); else { state.neighborhood = false; update(state); } });
  root.querySelector<HTMLInputElement>('#graph-search')?.addEventListener('input', event => { state.search = (event.target as HTMLInputElement).value.trim().slice(0, 120); state.neighborhood = false; update(state, true); });
  root.querySelector<HTMLSelectElement>('#graph-type')?.addEventListener('change', event => { state.type = (event.target as HTMLSelectElement).value; update(state, true); });
  root.querySelector('#graph-more')?.addEventListener('click', () => void fetchPortion(state, false));
  root.querySelector('#graph-refresh')?.addEventListener('click', () => void fetchPortion(state, true));
  root.addEventListener('click', event => {
    const button = event.target instanceof Element ? event.target.closest<HTMLButtonElement>('[data-graph-node]') : null;
    const id = button?.dataset.graphNode;
    if (!id || !state.snapshot.nodes.some(node => node.id === id)) return;
    if (!visibleSnapshot(state).nodes.some(node => node.id === id)) {
      state.search = ''; state.type = ''; state.neighborhood = false;
      root.querySelector<HTMLInputElement>('#graph-search')!.value = '';
      update(state, true);
    }
    graphView.selectNode(id);
  });
  window.addEventListener('pagehide', disposeMemoryGraph, { once: true });
  update(state);
  void hydrateCards(state);
}

function current(state: PageState): boolean { return active === state && state.epoch === requestEpoch && state.root.isConnected; }
function areaFromUrl(): string | null { return new URL(location.href).searchParams.get('area'); }

/** Changes the optional rendering mode while retaining selection and ignoring an unmounted page. */
async function changeView(state: PageState, view: graphView.GraphView): Promise<void> {
  if (!current(state)) return;
  const button = state.root.querySelector<HTMLButtonElement>('#graph-view-3d');
  if (button) button.disabled = true;
  state.switching = true;
  await graphView.setView(view);
  if (!current(state)) return;
  state.switching = false;
  if (button) button.disabled = false;
  if (state.selectedId) graphView.selectNode(state.selectedId);
}

/** Reports the actual renderer mode and safe WebGL fallback without exposing renderer exception text. */
function updateView(state: PageState, view: graphView.GraphView, unavailable = false): void {
  if (!current(state)) return;
  state.root.querySelector('#graph-view-2d')?.setAttribute('aria-pressed', String(view === '2d'));
  state.root.querySelector('#graph-view-3d')?.setAttribute('aria-pressed', String(view === '3d'));
  const notice = state.root.querySelector<HTMLElement>('#graph-view-notice');
  if (notice) notice.textContent = unavailable ? '3D недоступен в этом браузере. Читаемая 2D-карта и список записей остаются доступны.' : '';
}

/** Shares selection between canvas, keyboard list and inspector; filtered aliases always come from the canonical snapshot. */
function select(state: PageState, node?: GraphNode): void {
  if (!current(state)) return;
  const next = node && state.snapshot.nodes.some(candidate => candidate.id === node.id) ? node.id : undefined;
  const changed = state.selectedId !== next;
  const inspectorHadFocus = state.root.querySelector('#graph-inspector-content')?.contains(document.activeElement);
  const wasNeighborhood = state.neighborhood;
  state.selectedId = next;
  if (!next) state.neighborhood = false;
  updateInspector(state);
  if (changed && inspectorHadFocus) state.root.querySelector<HTMLElement>('#graph-selected-title')?.focus({ preventScroll: true });
  for (const button of state.root.querySelectorAll<HTMLButtonElement>('[data-graph-node]')) button.setAttribute('aria-pressed', String(button.dataset.graphNode === next));
  if (changed && wasNeighborhood) update(state);
}

/** Calculates a local view from loaded topology; it never changes the canonical nodes, aliases, degrees or edges. */
function visibleSnapshot(state: PageState): GraphSnapshot {
  const search = state.search.toLocaleLowerCase('ru');
  let nodes = state.snapshot.nodes.filter(node => (!state.type || node.type === state.type) && (!search || `${node.label} ${state.cards.get(node.href)?.title ?? ''} ${state.cards.get(node.href)?.preview ?? ''}`.toLocaleLowerCase('ru').includes(search)));
  if (state.neighborhood && state.selectedId) {
    const neighbors = new Set([state.selectedId]);
    for (const edge of state.snapshot.edges) {
      if (edge.sourceId === state.selectedId) neighbors.add(edge.targetId);
      if (edge.targetId === state.selectedId) neighbors.add(edge.sourceId);
    }
    nodes = nodes.filter(node => neighbors.has(node.id));
  }
  const ids = new Set(nodes.map(node => node.id));
  return { ...state.snapshot, nodes: nodes.map(node => {
    const card = state.cards.get(node.href);
    return card ? { ...node, label: card.title, alias: node.label, subtitle: `${typeLabel(node.type)} · ${node.degree} связей` } : node;
  }), edges: state.snapshot.edges.filter(edge => ids.has(edge.sourceId) && ids.has(edge.targetId)), focusId: state.neighborhood ? state.selectedId : undefined };
}

/** Refreshes the view and accessible node list, with an honest bound rather than a claim to represent all stored memory. */
function update(state: PageState, preserveView = false): void {
  if (!current(state)) return;
  if (state.type && !state.snapshot.nodes.some(node => node.type === state.type)) state.type = '';
  let visible = visibleSnapshot(state);
  if (state.selectedId && !visible.nodes.some(node => node.id === state.selectedId)) {
    state.selectedId = undefined; state.neighborhood = false; visible = visibleSnapshot(state);
  }
  graphView.render(visible, preserveView);
  if (state.selectedId) graphView.selectNode(state.selectedId);
  const list = state.root.querySelector<HTMLElement>('#graph-node-list')!;
  const focusedId = document.activeElement instanceof HTMLElement ? document.activeElement.dataset.graphNode : undefined;
  list.innerHTML = visible.nodes.map(node => {
    const card = state.cards.get(node.href);
    return `<li><button type="button" class="memory-graph-summary__node" data-graph-node="${e(node.id)}" aria-pressed="${node.id === state.selectedId}">
    <span class="memory-graph-type-dot" style="background:${typeColor(node.type)}" aria-hidden="true"></span><strong>${e(node.label)}</strong>
    ${card ? `<span class="graph-record-preview">${e(card.preview)}</span>` : ''}<span>${e(node.alias ?? node.label)} · Связей в проекции: ${node.degree}</span></button></li>`;
  }).join('') || `<li class="muted">${state.snapshot.status === 'available' ? 'Нет записей по выбранным условиям. Очистите поиск или измените тип.' : 'Нет доступной карты. Нажмите «Обновить».'}</li>`;
  if (focusedId) [...list.querySelectorAll<HTMLButtonElement>('[data-graph-node]')].find(button => button.dataset.graphNode === focusedId)?.focus({ preventScroll: true });
  state.root.querySelector('#graph-list-count')!.textContent = `Показано ${visible.nodes.length} из ${state.snapshot.nodes.length}`;
  const connected = new Set<string>();
  for (const edge of state.snapshot.edges) { connected.add(edge.sourceId); connected.add(edge.targetId); }
  const metrics = [[state.snapshot.nodes.length, 'загружено записей'], [state.snapshot.edges.length, 'показано связей'], [connected.size, 'записей со связями'], [state.snapshot.nodes.length - connected.size, 'без показанных связей']];
  state.root.querySelector('#graph-overview')!.innerHTML = metrics.map(([number, label]) => `<div><strong>${number}</strong><span>${label}</span></div>`).join('');
  const progress = state.root.querySelector<HTMLElement>('#graph-progress')!;
  const area = state.snapshot.areaLabel ? `Область: ${state.snapshot.areaLabel}. ` : 'Настроенная карта. ';
  progress.textContent = `${area}Загружено ${state.snapshot.nodes.length} записей и ${state.snapshot.edges.length} связей (ограниченная карта, до ${MAX_NODES} записей и ${MAX_EDGES} связей). Показано: ${visible.nodes.length}.`;
  const more = state.root.querySelector<HTMLButtonElement>('#graph-more')!;
  more.hidden = !state.snapshot.nextToken || state.snapshot.nodes.length >= MAX_NODES;
  more.disabled = state.loading;
  updateTypes(state); updateInspector(state); updateCardStatus(state);
  if (state.snapshot.status !== 'available') showMessage(state, state.snapshot.status === 'changed' || state.snapshot.status === 'stale'
    ? 'Память изменилась. Обновите карту, чтобы загрузить новое поколение.' : 'Граф сейчас недоступен. Он привязан к настроенной области; выбранная область может не совпадать. Повторите загрузку.');
}

/** Updates type options and their counts without replacing the active search input. */
function updateTypes(state: PageState): void {
  const counts = new Map<string, number>();
  for (const node of state.snapshot.nodes) counts.set(node.type, (counts.get(node.type) ?? 0) + 1);
  const types = [...counts].sort((a, b) => typeLabel(a[0]).localeCompare(typeLabel(b[0]), 'ru'));
  const select = state.root.querySelector<HTMLSelectElement>('#graph-type')!;
  select.innerHTML = '<option value="">Все типы</option>' + types.map(([type, count]) => `<option value="${e(type)}"${type === state.type ? ' selected' : ''}>${e(typeLabel(type))} · ${count}</option>`).join('');
  state.root.querySelector<HTMLElement>('#graph-type-legend')!.innerHTML = types.map(([type, count]) => `<span><span class="memory-graph-type-dot" style="background:${typeColor(type)}" aria-hidden="true"></span>${e(typeLabel(type))} · ${count}</span>`).join('');
}

/** Lists only neighbors evidenced by the loaded edges, and distinguishes them from the server projection's degree. */
function updateInspector(state: PageState): void {
  const node = state.snapshot.nodes.find(candidate => candidate.id === state.selectedId);
  const inspector = state.root.querySelector<HTMLElement>('#graph-inspector-content')!;
  const focused = document.activeElement instanceof HTMLElement && inspector.contains(document.activeElement) ? document.activeElement : undefined;
  const focusKey = focused?.dataset.graphNode ?? focused?.getAttribute('href') ?? focused?.id;
  const selectionKey = node?.id ?? '';
  const sameSelection = inspector.dataset.selection === selectionKey;
  inspector.dataset.selection = selectionKey;
  /** Restores an addressed control only when the same selected record is being re-rendered. */
  const restoreFocus = () => {
    if (!focused || !sameSelection || !focusKey) return;
    const controls = inspector.querySelectorAll<HTMLElement>('button,a,h2[tabindex]');
    for (const control of controls) {
      const key = control.dataset.graphNode ?? control.getAttribute('href') ?? control.id;
      if (control.tagName === focused.tagName && key === focusKey) { control.focus({ preventScroll: true }); break; }
    }
  };
  const checkbox = state.root.querySelector<HTMLInputElement>('#graph-neighborhood')!;
  checkbox.disabled = !node; checkbox.checked = state.neighborhood;
  state.root.querySelector<HTMLButtonElement>('#graph-clear')!.disabled = !node;
  state.root.querySelector<HTMLButtonElement>('#graph-focus')!.disabled = !node;
  if (!node) {
    const starts = state.snapshot.nodes.filter(candidate => candidate.degree > 0).sort((a, b) => b.degree - a.degree).slice(0, 3);
    inspector.innerHTML = `<p class="eyebrow">С чего начать</p><h2>Выберите запись</h2><p class="small-copy">Откройте карточку на карте или найдите её по названию. Здесь появятся фрагмент текста и ближайшие соседи.</p>
    ${starts.length ? `<h3>Больше показанных связей</h3><ul class="graph-starting-points">${starts.map(candidate => `<li><button class="memory-graph-neighbor-select" data-graph-node="${e(candidate.id)}">${e(state.cards.get(candidate.href)?.title ?? candidate.label)}</button><span>${candidate.degree} связей в проекции</span></li>`).join('')}</ul>` : '<p class="small-copy">В этой порции нет показанных связей. Можно открыть запись в Wiki или загрузить следующую порцию.</p>'}`;
    restoreFocus(); return;
  }
  const neighbors: Array<{ node: GraphNode; weight: number }> = [];
  for (const edge of state.snapshot.edges) {
    const other = edge.sourceId === node.id ? edge.targetId : edge.targetId === node.id ? edge.sourceId : null;
    const neighbor = other ? state.snapshot.nodes.find(candidate => candidate.id === other) : undefined;
    if (neighbor) neighbors.push({ node: neighbor, weight: edge.weight });
  }
  neighbors.sort((a, b) => b.weight - a.weight || a.node.label.localeCompare(b.node.label, 'ru'));
  const card = state.cards.get(node.href);
  inspector.innerHTML = `<p class="eyebrow">${e(node.label)}</p><h2 id="graph-selected-title" tabindex="-1">${e(card?.title ?? node.label)}</h2>
    ${card ? `<p class="graph-selected-preview">${e(card.preview)}</p>` : '<p class="small-copy">Содержимое записи пока недоступно. Показано только обозначение карты.</p>'}
    <p class="small-copy">${e(typeLabel(node.type))} · важность ${node.importanceBand}/5 · уверенность ${node.confidenceBand}/5</p>
    <p>Связей в проекции: ${node.degree}. Соседей на загруженной карте: ${neighbors.length}.</p>${node.href ? `<a class="ui-button ui-button--secondary" href="${e(node.href)}">Открыть в Wiki</a>` : '<p class="small-copy">Wiki не настроена для области этой карты.</p>'}<h3>Ближайшие соседи</h3>${neighbors.length ? `<ul class="memory-graph-neighbors">${neighbors.map(neighbor => `<li>
    <button type="button" class="memory-graph-neighbor-select" data-graph-node="${e(neighbor.node.id)}">${e(state.cards.get(neighbor.node.href)?.title ?? neighbor.node.label)}</button>${neighbor.node.href ? `<a href="${e(neighbor.node.href)}" aria-label="${e(`Открыть ${neighbor.node.label} в Wiki`)}">Wiki ↗</a>` : ''}<span>Общих сущностей: ${neighbor.weight}</span>
    </li>`).join('')}</ul>` : '<p class="small-copy">На загруженной карте нет связей с этой записью.</p>'}`;
  restoreFocus();
}

function showMessage(state: PageState, message: string): void { state.root.querySelector<HTMLElement>('#graph-portion-message')!.textContent = message; }

/** Loads one cursor once; stale generations replace the graph with a neutral state instead of mixing portions. */
async function fetchPortion(state: PageState, refresh: boolean): Promise<void> {
  if (!current(state) || state.loading || (!refresh && !state.snapshot.nextToken)) return;
  state.loading = true;
  if (refresh) { state.contentEpoch++; state.cardController?.abort(); state.cardLoading = false; }
  const more = state.root.querySelector<HTMLButtonElement>('#graph-more')!;
  const reload = state.root.querySelector<HTMLButtonElement>('#graph-refresh')!;
  more.disabled = true; reload.disabled = true;
  showMessage(state, 'Загрузка записей…');
  const next = await load(refresh ? null : state.snapshot.nextToken, state.requestedArea);
  if (!current(state)) return;
  state.loading = false; reload.disabled = false; more.disabled = false;
  if (next.status === 'unavailable') {
    showMessage(state, 'Не удалось загрузить карту. Можно повторить попытку.');
    if (refresh) { state.attempted.clear(); state.cardUnavailable = true; updateCardStatus(state); }
    return;
  }
  state.snapshot = refresh ? next : mergePortion(state.snapshot, next);
  if (refresh || state.snapshot.status !== 'available') {
    if (state.snapshot.status !== 'available') { state.contentEpoch++; state.cardController?.abort(); state.cardLoading = false; }
    state.cards.clear(); state.attempted.clear(); state.cardUnavailable = false;
  }
  if (refresh || state.snapshot.status !== 'available') { state.selectedId = undefined; state.neighborhood = false; graphView.selectNode(''); }
  showMessage(state, state.snapshot.nextToken ? '' : 'Загружена доступная ограниченная карта. Она может не включать все записи памяти.');
  update(state, !refresh);
  void hydrateCards(state);
}

/** Announces partial/current reader hydration separately from topology availability. */
function updateCardStatus(state: PageState): void {
  const status = state.root.querySelector<HTMLElement>('#graph-card-status')!;
  const count = state.cards.size;
  status.textContent = state.snapshot.status !== 'available' ? 'Названия появятся после обновления карты.' : state.cardLoading ? 'Загружаем названия и фрагменты…' : state.cardUnavailable
    ? 'Названия части записей не загрузились. Карта и обозначения доступны.'
    : state.snapshot.areaId ? `Названия доступны для ${count} из ${state.snapshot.nodes.length} записей. Содержимое читается отдельно от связей.`
    : 'Для карты не настроена область чтения. Доступны только обозначения.';
  state.root.querySelector<HTMLButtonElement>('#graph-cards-retry')!.hidden = !state.cardUnavailable || state.cardLoading;
}

/** Hydrates only new routes in25-card batches; abort, mount epoch and membership fences reject late content. */
async function hydrateCards(state: PageState): Promise<void> {
  if (!current(state) || state.cardLoading || !state.snapshot.areaId || state.snapshot.status !== 'available') return;
  const pending = state.snapshot.nodes.filter(node => node.href && !state.cards.has(node.href) && !state.attempted.has(node.href)).slice(0, 25);
  if (!pending.length) { updateCardStatus(state); return; }
  const epoch = state.contentEpoch;
  const area = state.snapshot.areaId;
  const controller = new AbortController();
  state.cardController = controller; state.cardLoading = true;
  for (const node of pending) state.attempted.add(node.href);
  updateCardStatus(state);
  const cards = await loadCards(pending, area, controller.signal);
  if (!current(state) || epoch !== state.contentEpoch || state.snapshot.areaId !== area || controller.signal.aborted) return;
  state.cardLoading = false;
  if (!cards) state.cardUnavailable = true;
  else for (const [href, card] of cards) {
    if (state.snapshot.nodes.some(node => node.href === href)) state.cards.set(href, card);
  }
  let autoFocused = false;
  if (!state.initialFocusDone) {
    state.initialFocusDone = true;
    const activeControl = document.activeElement instanceof Element && state.root.contains(document.activeElement) && document.activeElement.matches('input,select,button,a,canvas');
    if (!state.interacted && !activeControl && !state.selectedId && !state.search && !state.type && state.cards.size) {
      const degrees = new Map<string, number>();
      for (const edge of state.snapshot.edges) {
        degrees.set(edge.sourceId, (degrees.get(edge.sourceId) ?? 0) + 1);
        degrees.set(edge.targetId, (degrees.get(edge.targetId) ?? 0) + 1);
      }
      const start = state.snapshot.nodes.filter(node => state.cards.has(node.href)).sort((a, b) => (degrees.get(b.id) ?? 0) - (degrees.get(a.id) ?? 0))[0];
      if (start && degrees.get(start.id)) { state.selectedId = start.id; state.neighborhood = true; autoFocused = true; }
    }
  }
  update(state, !autoFocused);
  void hydrateCards(state);
}

/** Clears local filtering so a neighborhood action actually reveals all loaded neighbors. */
function clearFilters(state: PageState): void {
  state.search = ''; state.type = '';
  state.root.querySelector<HTMLInputElement>('#graph-search')!.value = '';
}

/** Makes the chosen record and its loaded neighbors a readable focus view. */
function focusNeighborhood(state: PageState): void {
  if (!state.selectedId) return;
  clearFilters(state); state.neighborhood = true; update(state);
}
