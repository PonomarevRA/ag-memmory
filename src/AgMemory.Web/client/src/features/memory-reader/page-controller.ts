import { load, loadAreas, loadCatalog, loadTags, loadTree, type CatalogResponse, type ReaderArea, type TreeNode } from './api.js';
import type { MemoryReaderDocument, MemoryReaderRelation } from './sanitize.js';
import { ui } from '../../ui-kit/index.js';
import { ReaderHistory, streamStatus, type StreamStatus } from './view-state.js';
import { flattenTree, revealCurrent, treeMarkup, type TreeView } from './tree-navigation.js';

let observer: IntersectionObserver | undefined;
let activeRoot: HTMLElement | undefined;
let readerNavigation: ((event: MouseEvent) => void) | undefined;
let disposed = false;
let requestEpoch = 0;
let activeHistoryHref = '';
let activeState: CatalogState | DocumentState | undefined;

type ReaderBase = TreeView & { epoch: number; area: string | null; treeShown: number; treeLoading?: boolean };
type CatalogState = ReaderBase & { areas: ReaderArea[]; token: string | null; tagToken: string | null; namespace: string | null; tag: string | null; search: string; documents: CatalogResponse['documents']; tags: CatalogResponse['tags']; tagsShown: number; catalogLoading?: boolean; tagsLoading?: boolean; catalogStatus: StreamStatus; tagsStatus: StreamStatus; restored?: boolean };
type DocumentState = ReaderBase & { content: MemoryReaderDocument; routeKey: string };
type CatalogSnapshot = { state: CatalogState; x: number; y: number; treeScroll: number };
const catalogHistory = new ReaderHistory<CatalogSnapshot>();
let readerActions: ((event: Event) => void) | undefined;

/** Captures a catalog before navigation; searches and opaque cursors stay in JS memory only. */
function rememberCatalog(): void {
  if (!activeRoot || !activeState || !('documents' in activeState) || location.pathname !== '/memory-reader' || activeHistoryHref !== location.pathname + location.search) return;
  const state = activeState;
  const savedState: CatalogState = {
    ...state, documents: [...state.documents], tags: [...state.tags], tree: [...state.tree],
    collapsed: new Set(state.collapsed), catalogLoading: false, treeLoading: false, tagsLoading: false
  };
  const treeScroll = activeRoot.querySelector('.memory-reader-tree')?.scrollTop ?? 0;
  catalogHistory.remember({ state: savedState, x: window.scrollX, y: window.scrollY, treeScroll });
}

/** Records only scroll numbers on scroll events; does not clone result arrays or write browser storage. */
function rememberScroll(): void {
  if (!activeState || !('documents' in activeState) || activeHistoryHref !== location.pathname + location.search) return;
  const snapshot = catalogHistory.current();
  if (!snapshot) return;
  snapshot.x = window.scrollX; snapshot.y = window.scrollY;
  snapshot.treeScroll = activeRoot?.querySelector('.memory-reader-tree')?.scrollTop ?? 0;
}

/** Disposes callbacks and invalidates pending work without discarding private back-navigation snapshots. */
export function dispose(): void {
  rememberCatalog();
  disposed = true;
  requestEpoch++;
  activeState = undefined;
  observer?.disconnect();
  window.removeEventListener('scroll', rememberScroll, true);
  if (activeRoot && readerNavigation) activeRoot.removeEventListener('click', readerNavigation);
  if (activeRoot && readerActions) {
    for (const kind of ['click', 'submit', 'change', 'toggle']) activeRoot.removeEventListener(kind, readerActions, kind === 'toggle' || kind === 'change');
  }
  readerActions = undefined;
  observer = undefined;
  readerNavigation = undefined;
  activeRoot = undefined;
}

/** Mounts one reader session; disposed and superseded requests cannot modify its view. */
export async function mountMemoryReaderPage(routeKey: string): Promise<void> {
  const replacingCatalog = activeState && 'documents' in activeState && activeHistoryHref === location.pathname + location.search;
  dispose(); disposed = false;
  const root = document.querySelector<HTMLElement>('#memory-reader-root');
  if (!root) return;
  activeRoot = root;
  window.addEventListener('scroll', rememberScroll, { capture: true, passive: true });
  readerNavigation = event => {
    const link = (event.target as Element).closest<HTMLAnchorElement>('a[href]');
    if (event.defaultPrevented || !link || link.target || link.hasAttribute('download') || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || link.hash || link.origin !== location.origin) return;
    const match = /^\/memory-reader\/([^/?#]+)$/.exec(link.pathname);
    if (!match) return;
    event.preventDefault();
    event.stopPropagation();
    void loadDetailInPlace(root, decodeURIComponent(match[1]), new URL(link.href).searchParams.get('area'), link.pathname + link.search);
  };
  root.addEventListener('click', readerNavigation);
  readerActions = event => handleAction(root, event);
  for (const kind of ['click', 'submit', 'change', 'toggle']) root.addEventListener(kind, readerActions, kind === 'toggle' || kind === 'change');
  if (routeKey) { await mountDocument(root, routeKey); return; }
  await mountCatalog(root, !replacingCatalog);
}

function isCurrent(root: HTMLElement): boolean { return !disposed && activeRoot === root; }

/** A page cursor belongs to one view generation and must never update a newer view. */
function isCurrentState(root: HTMLElement, state: CatalogState | DocumentState): boolean {
  return isCurrent(root) && activeState === state && state.epoch === requestEpoch;
}

/** Replaces a view without moving the tree scroll position or a surviving control's focus. */
function replaceView(root: HTMLElement, markup: string): void {
  const treeScroll = root.querySelector('.memory-reader-tree')?.scrollTop ?? 0;
  const focused = document.activeElement instanceof HTMLElement && root.contains(document.activeElement) ? document.activeElement : null;
  const focusId = focused?.id;
  const focusTag = focused?.dataset.readerTag;
  const branchKey = focused instanceof HTMLElement && focused.tagName === 'SUMMARY' ? focused.parentElement?.dataset.readerBranch : undefined;
  const inputValue = focused instanceof HTMLInputElement ? focused.value : null;
  const selection = focused instanceof HTMLInputElement ? [focused.selectionStart, focused.selectionEnd] : null;
  root.innerHTML = markup;
  const tree = root.querySelector('.memory-reader-tree');
  if (tree) tree.scrollTop = treeScroll;
  const focusHref = focused instanceof HTMLAnchorElement ? focused.getAttribute('href') : null;
  let restored = focusId ? document.getElementById(focusId) : null;
  if (restored && !root.contains(restored)) restored = null;
  if (!restored && focusHref) {
    for (const link of root.querySelectorAll<HTMLAnchorElement>('a[href]')) {
      if (link.getAttribute('href') === focusHref) { restored = link; break; }
    }
  }
  if (!restored && focusTag) {
    for (const button of root.querySelectorAll<HTMLButtonElement>('[data-reader-tag]')) {
      if (button.dataset.readerTag === focusTag) { restored = button; break; }
    }
    restored ??= root.querySelector('#reader-search-input');
  }
  if (!restored && branchKey) {
    for (const branch of root.querySelectorAll<HTMLDetailsElement>('[data-reader-branch]')) {
      if (branch.dataset.readerBranch === branchKey) { restored = branch.querySelector('summary'); break; }
    }
  }
  restored?.focus({ preventScroll: true });
  if (restored instanceof HTMLInputElement && inputValue !== null) restored.value = inputValue;
  if (restored instanceof HTMLInputElement && selection) restored.setSelectionRange(selection[0], selection[1]);
}

/** Announces the loaded heading and moves keyboard focus into a newly opened document. */
function announceDocument(root: HTMLElement, content: MemoryReaderDocument, focus = false): void {
  document.title = `${content.title || 'Запись памяти'} · AgMemory`;
  root.closest('.memory-reader-page')?.querySelector('.page-header')?.setAttribute('hidden', '');
  if (focus) root.querySelector<HTMLElement>('.memory-reader-document-header h1')?.focus({ preventScroll: true });
}

/** Loads a direct document and tree independently so either resource remains recoverable on its own. */
async function mountDocument(root: HTMLElement, routeKey: string): Promise<void> {
  const epoch = ++requestEpoch;
  const areas = await loadAreas();
  if (!isCurrent(root) || epoch !== requestEpoch) return;
  const area = selectedArea(areas);
  persistArea(area);
  const content: MemoryReaderDocument = { status: 'unavailable', routeKey, title: null, namespace: null, tags: [], children: [], related: [], backlinks: [], blocks: [], nextBlockToken: null };
  const state: DocumentState = { epoch, content, routeKey, area, tree: [], treeToken: null, treeShown: 0, treeStatus: 'loading', collapsed: new Set(), railOpen: wideRail() };
  activeState = state;
  replaceView(root, `<div class="memory-reader-layout">${readerTree(state)}<div class="memory-reader-main" aria-busy="true"><p role="status">Загрузка страницы…</p></div></div>`);
  const documentWork = load(routeKey, null, area).then(next => {
    if (!isCurrentState(root, state)) return;
    state.content = next;
    const main = root.querySelector<HTMLElement>('.memory-reader-main')!;
    main.removeAttribute('aria-busy');
    if (next.status !== 'available') {
      main.innerHTML = `<div role="status">${ui.status('Страница недоступна или изменилась.', 'warning')}</div><button type="button" data-reader-document-retry="${ui.escape(routeKey)}" class="ui-button">Повторить загрузку страницы</button>`;
      return;
    }
    renderDocumentContent(root, state);
    announceDocument(root, next);
  });
  await Promise.all([documentWork, moreTreePages(root, state, true)]);
}

/** Renders the current document with its retained navigation rail. */
function renderDocument(root: HTMLElement, state: DocumentState): void {
  activeState = state;
  replaceView(root, `<div class="memory-reader-layout">${readerTree(state)}<div class="memory-reader-main"></div></div>`);
  renderDocumentContent(root, state);
  bindProgressive(root, state);
  revealCurrent(root, state);
  announceDocument(root, state.content);
}

/** Updates block content independently, leaving the retained tree and its pending requests intact. */
function renderDocumentContent(root: HTMLElement, state: DocumentState): void {
  const main = root.querySelector<HTMLElement>('.memory-reader-main')!;
  const restoreFocus = main.contains(document.activeElement);
  renderDocumentInto(main, state.content, state.routeKey, state.area, next => {
    if (!isCurrentState(root, state)) return;
    state.content = next;
    renderDocumentContent(root, state);
  });
  if (restoreFocus) {
    const target = main.querySelector<HTMLElement>('#reader-more-blocks') ?? main.querySelector<HTMLElement>('h1');
    target?.focus({ preventScroll: true });
  }
}

/** Renders escaped document content and keeps block continuation local to this document. */
function renderDocumentInto(
  main: HTMLElement,
  content: MemoryReaderDocument,
  routeKey: string,
  area: string | null,
  onMoreBlocks: (content: MemoryReaderDocument) => void): void {
  const e = ui.escape;
  const blocks = content.blocks.map(block => `<section class="memory-reader-block"><h2>${e(block.heading || 'Результат')}</h2><p class="memory-reader-block__content">${block.content.map(run => run.kind === 'link' && run.routeKey ? `<a href="${e(readerHref(`/memory-reader/${encodeURIComponent(run.routeKey)}`, area))}">${e(run.text)}</a>` : e(run.text)).join('')}</p></section>`).join('');
  main.innerHTML = `<article class="memory-reader-document"><header class="memory-reader-document-header">${ui.link(readerHref('/memory-reader', area), 'К каталогу страниц')}<p class="eyebrow">Вопрос</p><h1 tabindex="-1">${e(content.title || 'Запись памяти')}</h1><p class="small-copy">${e(content.namespace || '')}</p><div>${content.tags.map(tag => ui.chip(tag)).join('')}</div></header><section aria-label="Результат"><p class="eyebrow">Результат</p>${blocks || ui.status('В этой странице пока нет результата.', 'warning')}</section>${relations('Другие страницы', [...content.children, ...content.related, ...content.backlinks], area)}<div class="memory-reader-actions">${content.nextBlockToken ? ui.button('Загрузить ещё результат', { id: 'reader-more-blocks', secondary: true }) : ''}</div></article>`;
  const button = main.querySelector<HTMLButtonElement>('#reader-more-blocks');
  const blockStatus = document.createElement('p');
  blockStatus.setAttribute('role', 'status');
  button?.before(blockStatus);
  let refreshBlocks = false;
  if (button && content.nextBlockToken) button.addEventListener('click', async () => {
    button.disabled = true;
    blockStatus.textContent = 'Загрузка результата…';
    const next = await load(routeKey, refreshBlocks ? null : content.nextBlockToken, area);
    if (next.status === 'available') onMoreBlocks({ ...next, blocks: refreshBlocks ? next.blocks : [...content.blocks, ...next.blocks] });
    else {
      button.disabled = false;
      refreshBlocks = next.status === 'changed' || next.status === 'stale';
      blockStatus.textContent = refreshBlocks ? 'Память изменилась. Обновите результат страницы.' : 'Не удалось загрузить результат. Можно повторить попытку.';
      button.textContent = refreshBlocks ? 'Обновить результат' : 'Повторить загрузку результата';
    }
  });
}

/** Opens a document in the existing rail while committing only the latest navigation to browser history. */
async function loadDetailInPlace(root: HTMLElement, routeKey: string, linkedArea: string | null, href: string): Promise<void> {
  const previous = activeState;
  if (!previous) return;
  rememberCatalog();
  const area = linkedArea || previous.area;
  const epoch = ++requestEpoch;
  const main = root.querySelector<HTMLElement>('.memory-reader-main');
  main?.setAttribute('aria-busy', 'true');
  const content = await load(routeKey, null, area);
  if (!isCurrent(root) || epoch !== requestEpoch) return;
  main?.removeAttribute('aria-busy');
  if (content.status !== 'available') {
    previous.epoch = epoch;
    previous.treeLoading = false;
    if (previous.treeStatus === 'loading') previous.treeStatus = 'unavailable';
    if ('documents' in previous) {
      previous.catalogLoading = false; previous.tagsLoading = false;
      if (previous.catalogStatus === 'loading') previous.catalogStatus = 'unavailable';
      if (previous.tagsStatus === 'loading') previous.tagsStatus = 'unavailable';
      updateStream(root, previous, 'catalog');
      updateStream(root, previous, 'tags');
    }
    updateTree(root, previous);
    root.querySelector('#reader-navigation-error')?.remove();
    const warning = document.createElement('div');
    warning.id = 'reader-navigation-error';
    warning.setAttribute('role', 'status');
    warning.innerHTML = ui.status('Страница недоступна или изменилась. Выберите другую страницу или повторите попытку.', 'warning');
    main?.prepend(warning);
    return;
  }
  // In-place links are same-area links produced by the validated reader projection.
  // A new state invalidates old catalog/tree callbacks while retaining the already fetched rail.
  const state: DocumentState = { epoch, content, routeKey, area, tree: previous.tree, treeToken: previous.treeToken, treeShown: previous.treeShown, treeStatus: previous.treeStatus === 'loading' ? streamStatus('available', previous.tree.length) : previous.treeStatus, collapsed: previous.collapsed, railOpen: previous.railOpen };
  history.pushState({}, '', href);
  renderDocument(root, state);
  announceDocument(root, content, true);
}

/** Chooses a compact disclosure on narrow displays while retaining a full desktop rail. */
function wideRail(): boolean { return !window.matchMedia?.('(max-width: 62rem)').matches; }

/** Formats one stream status independently from loaded data and continuation ownership. */
function statusMessage(status: StreamStatus, noun: string): string {
  if (status === 'loading') return `Загрузка: ${noun}…`;
  if (status === 'changed') return 'Память изменилась. Обновите этот список, чтобы продолжить с нового состояния.';
  if (status === 'unavailable') return `Не удалось загрузить: ${noun}. Можно повторить попытку.`;
  if (status === 'empty') return `Нет данных: ${noun}.`;
  return '';
}

/** Builds the shared catalog/document tree with an independent loading and recovery status. */
function readerTree(state: ReaderBase & { routeKey?: string }): string {
  return treeMarkup(state, state.routeKey || '', path => readerHref(path, state.area), statusMessage(state.treeStatus, 'дерево страниц'));
}

/** Updates only the rail so independent document blocks, focus and search drafts survive paging. */
function updateTree(root: HTMLElement, state: CatalogState | DocumentState): void {
  const rail = root.querySelector<HTMLElement>('.memory-reader-tree');
  if (rail) {
    const scroll = rail.scrollTop;
    const focused = rail.contains(document.activeElement) ? document.activeElement as HTMLElement : null;
    const id = focused?.id;
    const href = focused instanceof HTMLAnchorElement ? focused.getAttribute('href') : null;
    const fragment = document.createElement('div');
    fragment.innerHTML = readerTree(state);
    rail.replaceWith(fragment.firstElementChild!);
    const replacement = root.querySelector<HTMLElement>('.memory-reader-tree')!;
    replacement.scrollTop = scroll;
    let target = id ? replacement.querySelector<HTMLElement>(`#${id}`) : null;
    if (href) for (const link of replacement.querySelectorAll<HTMLAnchorElement>('a[href]')) if (link.getAttribute('href') === href) target = link;
    if (focused) (target ?? replacement.querySelector<HTMLElement>('#reader-more-tree, .is-active'))?.focus({ preventScroll: true });
  }
  bindProgressive(root, state);
  rememberCatalog();
}

/** Loads or refreshes one tree stream. Changed cursors never append into old rows. */
async function moreTreePages(root: HTMLElement, state: CatalogState | DocumentState, refresh = false): Promise<void> {
  if (state.treeLoading || !isCurrentState(root, state)) return;
  const fresh = refresh || state.treeStatus === 'changed' || !state.treeToken;
  state.treeLoading = true;
  state.treeStatus = 'loading';
  updateTree(root, state);
  const epoch = state.epoch;
  const page = await loadTree(fresh ? null : state.treeToken, state.area);
  if (!isCurrentState(root, state) || state.epoch !== epoch) return;
  state.treeLoading = false;
  state.treeStatus = streamStatus(page.status, page.roots.length + (fresh ? 0 : state.tree.length));
  if (page.status === 'available') {
    state.tree = fresh ? page.roots : [...state.tree, ...page.roots];
    state.treeToken = page.nextToken;
    state.treeShown = flattenTree(state.tree).length;
  }
  updateTree(root, state);
  if (fresh) revealCurrent(root, state);
}

function relations(heading: string, values: MemoryReaderRelation[], area: string | null): string {
  if (!values.length) return '';
  const e = ui.escape;
  const rows = values.map(value => `<tr><td><span class="memory-reader-link-kind memory-reader-link-kind--${e(value.kind)}">${e(value.kind)}</span></td><td>${ui.link(readerHref(value.href, area), value.title)}<br><small>${e(value.namespace)}</small></td><td><span class="memory-reader-link-weight">${value.sharedEntityCount}</span></td></tr>`).join('');
  return ui.container(`<h2>${e(heading)}</h2>${ui.table('<tr><th>Тип</th><th>Страница</th><th>Вес</th></tr>', rows, 'memory-reader-links__table')}`, 'memory-reader-links');
}

/** Loads a fresh catalog or restores one private history snapshot after server area validation. */
async function mountCatalog(root: HTMLElement, restore = true): Promise<void> {
  const epoch = ++requestEpoch;
  const areas = await loadAreas();
  if (!isCurrent(root) || epoch !== requestEpoch) return;
  const area = selectedArea(areas);
  persistArea(area);
  const saved = restore ? catalogHistory.current() : undefined;
  if (saved && saved.state.area === area) {
    const state = { ...saved.state, epoch, areas, collapsed: new Set(saved.state.collapsed), restored: true };
    // A pending request belonged to the discarded view; its loading flag cannot survive Back.
    if (state.catalogStatus === 'loading') state.catalogStatus = state.documents.length ? 'available' : 'unavailable';
    if (state.tagsStatus === 'loading') state.tagsStatus = state.tags.length ? 'available' : 'unavailable';
    if (state.treeStatus === 'loading') state.treeStatus = state.tree.length ? 'available' : 'unavailable';
    renderCatalog(root, state);
    const rail = root.querySelector<HTMLElement>('.memory-reader-tree');
    if (rail) rail.scrollTop = saved.treeScroll;
    if (window.scrollX !== saved.x || window.scrollY !== saved.y) window.scrollTo(saved.x, saved.y);
    rememberScroll();
    return;
  }
  const state: CatalogState = { epoch, area, areas, token: null, treeToken: null, tagToken: null, namespace: null, tag: null, search: '', documents: [], tags: [], tree: [], treeShown: 0, tagsShown: 12, catalogStatus: 'loading', tagsStatus: 'loading', treeStatus: 'loading', collapsed: new Set(), railOpen: wideRail() };
  renderCatalog(root, state);
  await loadCatalogStreams(root, state, true);
}

/** Renders result cards only; text and hrefs come from the safe reader DTO. */
function documentCards(state: CatalogState): string {
  const e = ui.escape;
  const cards: string[] = [];
  for (const doc of state.documents) {
    cards.push(ui.card(`<p class="eyebrow">${e(doc.namespace)}</p><h2>${ui.link(readerHref(doc.href, state.area), doc.title)}</h2><p>${e(doc.preview)}</p><p class="memory-reader-card-tags">${doc.tags.map(e).join(' · ')}</p>`, 'memory-reader-catalog__item'));
  }
  if (!cards.length && state.catalogStatus === 'empty') return ui.status(state.search || state.tag ? 'По выбранным условиям страницы не найдены. Измените поиск или сбросьте фильтр.' : 'Память пока пуста.', 'warning');
  return cards.join('');
}

/** Presents loading/retry/refresh independently from existing rows; stale data remains readable. */
function streamControls(status: StreamStatus, id: string, token: string | null, label: string): string {
  const error = status === 'changed' || status === 'unavailable';
  const action = status === 'changed' ? 'Обновить список' : error ? 'Повторить загрузку' : `Загрузить ещё ${label}`;
  const button = token || error ? ui.button(action, { id, secondary: true, pending: status === 'loading' }) : '';
  return `<p role="status">${ui.escape(statusMessage(status, label))}</p>${button}`;
}

/** Renders the independently replaceable results section, including a stable status region. */
function catalogMarkup(state: CatalogState): string {
  return `<section id="reader-results" aria-label="Страницы" aria-busy="${state.catalogStatus === 'loading'}"><section class="memory-reader-catalog">${documentCards(state)}</section><div id="reader-catalog-sentinel"></div><div class="memory-reader-actions">${streamControls(state.catalogStatus, 'reader-more-catalog', state.token, 'страницы')}</div></section>`;
}

/** Renders stable locator-keyed facet controls without replacing the search input or document tree. */
function tagsMarkup(state: CatalogState): string {
  const tags: string[] = [];
  for (const tag of state.tags.slice(0, state.tagsShown)) {
    tags.push(`<button type="button" class="memory-reader-facet${state.tag === tag.locator ? ' is-active' : ''}" aria-pressed="${state.tag === tag.locator}" data-reader-tag="${ui.escape(tag.locator)}">${ui.escape(tag.label)} <small>${tag.count}</small></button>`);
  }
  return `<section id="reader-tags" class="memory-reader-facets" aria-busy="${state.tagsStatus === 'loading'}"><div class="memory-reader-facets__group"><span>Теги</span>${tags.join('')}</div><div id="reader-tag-sentinel"></div>${streamControls(state.tagsStatus, 'reader-more-tags', state.tagToken, 'теги')}</section>`;
}

/** Replaces a single stream subtree and restores a surviving facet/button without CSS interpolation. */
function updateStream(root: HTMLElement, state: CatalogState, part: 'catalog' | 'tags'): void {
  const id = part === 'catalog' ? 'reader-results' : 'reader-tags';
  const target = root.querySelector<HTMLElement>(`#${id}`);
  if (!target) return;
  const focused = target.contains(document.activeElement) ? document.activeElement as HTMLElement : null;
  const focusTag = focused?.dataset.readerTag;
  const focusId = focused?.id;
  const fragment = document.createElement('div');
  fragment.innerHTML = part === 'catalog' ? catalogMarkup(state) : tagsMarkup(state);
  target.replaceWith(fragment.firstElementChild!);
  if (focused) {
    let replacement = focusId ? root.querySelector<HTMLElement>(`#${focusId}`) : null;
    if (focusTag) for (const button of root.querySelectorAll<HTMLButtonElement>('[data-reader-tag]')) if (button.dataset.readerTag === focusTag) replacement = button;
    (replacement ?? root.querySelector<HTMLElement>('#reader-search-input'))?.focus({ preventScroll: true });
  }
  bindProgressive(root, state);
  rememberCatalog();
}

/** Changes only pagination/status controls, retaining accumulated cards and their DOM identity. */
function updateCatalogControls(root: HTMLElement, state: CatalogState): void {
  const results = root.querySelector<HTMLElement>('#reader-results');
  const actions = results?.querySelector<HTMLElement>('.memory-reader-actions');
  if (!results || !actions) return;
  const focused = actions.contains(document.activeElement);
  results.setAttribute('aria-busy', String(state.catalogStatus === 'loading'));
  actions.innerHTML = streamControls(state.catalogStatus, 'reader-more-catalog', state.token, 'страницы');
  if (focused) (actions.querySelector<HTMLElement>('button') ?? root.querySelector<HTMLElement>('#reader-search-input'))?.focus({ preventScroll: true });
  bindProgressive(root, state);
  rememberCatalog();
}

/** Renders the shell on entry/filter changes; paging replaces only its own stream subtree. */
function renderCatalog(root: HTMLElement, state: CatalogState): void {
  activeState = state;
  activeHistoryHref = location.pathname + location.search;
  const e = ui.escape;
  const areaSelect = state.areas.length > 1 ? `<label>Область<select id="reader-area">${state.areas.map(area => `<option value="${e(area.id)}"${area.id === state.area ? ' selected' : ''}>${e(area.label)}</option>`).join('')}</select></label>` : '';
  const restored = state.restored ? '<p class="small-copy" role="status">Восстановлен предыдущий просмотр. Если память изменилась, обновите списки.</p>' : '';
  replaceView(root, `<section class="memory-reader-wiki__toolbar"><div><h2>Каталог страниц</h2><p class="small-copy">Поиск выполняется только по вопросу и краткому результату.</p>${restored}<button type="button" id="reader-refresh" class="ui-button ui-button--secondary">Обновить списки</button></div><form id="reader-search" class="memory-reader-wiki__actions">${areaSelect}<label class="visually-hidden" for="reader-search-input">Поиск</label>${ui.input('reader-search-input', state.search, 'Поиск по Wiki')}${ui.button('Найти', { type: 'submit' })}</form></section><div class="memory-reader-layout">${readerTree(state)}<div class="memory-reader-main">${tagsMarkup(state)}${catalogMarkup(state)}</div></div>`);
  bindProgressive(root, state);
  rememberCatalog();
}

/** Records native disclosure choices without imposing a custom tree keyboard model. */
function rememberDisclosure(target: HTMLDetailsElement, state: CatalogState | DocumentState): void {
  if (target.id === 'reader-tree-panel') state.railOpen = target.open;
  const key = target.dataset.readerBranch;
  if (key) {
    if (target.open) state.collapsed.delete(key);
    else state.collapsed.add(key);
  }
  rememberCatalog();
}

/** Handles shared tree controls and reports whether a click belonged to this navigation rail. */
function handleTreeClick(root: HTMLElement, state: CatalogState | DocumentState, button: HTMLButtonElement): boolean {
  if (button.id === 'reader-more-tree') { void moreTreePages(root, state); return true; }
  if (button.id !== 'reader-reveal-current') return false;
  state.railOpen = true;
  const panel = root.querySelector<HTMLDetailsElement>('#reader-tree-panel');
  if (panel) panel.open = true;
  revealCurrent(root, state);
  return true;
}

/** Routes catalog-only commands while preserving independent stream refresh semantics. */
function handleCatalogClick(root: HTMLElement, state: CatalogState, button: HTMLButtonElement): void {
  const tag = button.dataset.readerTag;
  if (tag) { void resetCatalog(root, state, state.tag === tag ? null : tag, state.search); return; }
  switch (button.id) {
    case 'reader-more-catalog': void moreCatalog(root, state); return;
    case 'reader-more-tags': void moreTagsPages(root, state); return;
    case 'reader-refresh': void resetCatalog(root, state, state.tag, state.search, true); return;
  }
}

/** Handles delegated actions once per mounted reader; callbacks always use the active epoch state. */
function handleAction(root: HTMLElement, event: Event): void {
  const target = event.target instanceof Element ? event.target : null;
  if (!target) return;
  const retry = target.closest<HTMLElement>('[data-reader-document-retry]');
  if (event.type === 'click' && retry) { void mountDocument(root, retry.dataset.readerDocumentRetry!); return; }
  const state = activeState;
  if (!state) return;
  if (event.type === 'toggle' && target instanceof HTMLDetailsElement) { rememberDisclosure(target, state); return; }
  if (event.type === 'click') {
    const button = target.closest<HTMLButtonElement>('button');
    if (!button || handleTreeClick(root, state, button)) return;
    if ('documents' in state) handleCatalogClick(root, state, button);
    return;
  }
  if (!('documents' in state)) return;
  if (event.type === 'submit' && target.id === 'reader-search') {
    event.preventDefault();
    void resetCatalog(root, state, null, root.querySelector<HTMLInputElement>('#reader-search-input')?.value ?? '');
  }
  if (event.type === 'change' && target.id === 'reader-area') void resetArea(root, state, (target as HTMLSelectElement).value);
}

/** Observes only healthy cursors; errors require an explicit retry rather than an automatic request loop. */
function bindProgressive(root: HTMLElement, state: CatalogState | DocumentState): void {
  observer?.disconnect();
  observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (!entry.isIntersecting || !isCurrentState(root, state)) continue;
      if (entry.target.id === 'reader-tree-sentinel' && state.treeToken && state.treeStatus === 'available') void moreTreePages(root, state);
      if ('documents' in state) {
        if (entry.target.id === 'reader-tag-sentinel' && state.tagToken && state.tagsStatus === 'available') void moreTagsPages(root, state);
        if (entry.target.id === 'reader-catalog-sentinel' && state.token && state.catalogStatus === 'available') void moreCatalog(root, state);
      }
    }
  }, { rootMargin: '160px' });
  root.querySelectorAll('#reader-tree-sentinel, #reader-tag-sentinel, #reader-catalog-sentinel').forEach(node => observer?.observe(node));
}

/** Loads fresh independent streams, so a slow or failed rail cannot hide successful result rows. */
async function loadCatalogStreams(root: HTMLElement, state: CatalogState, includeTree: boolean): Promise<void> {
  const work: Promise<void>[] = [moreCatalog(root, state, true), moreTagsPages(root, state, true)];
  if (includeTree) work.push(moreTreePages(root, state, true));
  await Promise.all(work);
}

/** Commits a new filter epoch before requests; pending old streams cannot mutate the replacement view. */
async function resetCatalog(root: HTMLElement, state: CatalogState, tag: string | null, search: string, includeTree = false): Promise<void> {
  const restartTree = !includeTree && (state.treeLoading || state.treeStatus === 'loading');
  const next: CatalogState = { ...state, epoch: ++requestEpoch, search: search.trim().slice(0, 120), tag, documents: [], tags: state.tags, token: null, tagToken: null, catalogLoading: false, tagsLoading: false, catalogStatus: 'loading', tagsStatus: 'loading', restored: false };
  if (includeTree) Object.assign(next, { tree: [], treeToken: null, treeLoading: false, treeStatus: 'loading' });
  else next.treeLoading = false;
  renderCatalog(root, next);
  // The old tree request cannot finish this new epoch; reissue only its pending continuation.
  const work = [loadCatalogStreams(root, next, includeTree)];
  if (restartTree) work.push(moreTreePages(root, next));
  await Promise.all(work);
}

/** Starts a validated area history entry with every old stream cleared before any new request. */
async function resetArea(root: HTMLElement, state: CatalogState, area: string): Promise<void> {
  if (area === state.area || !state.areas.some(candidate => candidate.id === area)) return;
  rememberCatalog();
  const url = new URL(location.href);
  url.search = '';
  url.searchParams.set('area', area);
  history.pushState({}, '', url.pathname + url.search);
  persistArea(area);
  const next: CatalogState = { ...state, epoch: ++requestEpoch, area, token: null, treeToken: null, tagToken: null, namespace: null, tag: null, search: '', documents: [], tags: [], tree: [], treeShown: 0, tagsShown: 12, catalogLoading: false, treeLoading: false, tagsLoading: false, catalogStatus: 'loading', treeStatus: 'loading', tagsStatus: 'loading', collapsed: new Set(), restored: false };
  renderCatalog(root, next);
  await loadCatalogStreams(root, next, true);
}

/** Appends one safe catalog continuation, or replaces only that stream after a requested fresh load. */
async function moreCatalog(root: HTMLElement, state: CatalogState, refresh = false): Promise<void> {
  if (state.catalogLoading || !isCurrentState(root, state)) return;
  const fresh = refresh || state.catalogStatus === 'changed' || !state.token;
  state.catalogLoading = true; state.catalogStatus = 'loading';
  updateCatalogControls(root, state);
  const epoch = state.epoch;
  const catalog = await loadCatalog(fresh ? null : state.token, state.namespace, state.tag, state.search || null, state.area);
  if (!isCurrentState(root, state) || state.epoch !== epoch) return;
  state.catalogLoading = false;
  state.catalogStatus = streamStatus(catalog.status, catalog.documents.length + (fresh ? 0 : state.documents.length));
  if (catalog.status === 'available') {
    state.documents = fresh ? catalog.documents : [...state.documents, ...catalog.documents];
    state.token = catalog.nextToken;
  }
  if (fresh && catalog.status === 'available') updateStream(root, state, 'catalog');
  else {
    if (catalog.status === 'available') {
      root.querySelector('.memory-reader-catalog')?.insertAdjacentHTML('beforeend', documentCards({ ...state, documents: catalog.documents }));
    }
    updateCatalogControls(root, state);
  }
}

/** Loads tag facets independently and preserves exact locator ownership across failures and refreshes. */
async function moreTagsPages(root: HTMLElement, state: CatalogState, refresh = false): Promise<void> {
  if (state.tagsLoading || !isCurrentState(root, state)) return;
  const fresh = refresh || state.tagsStatus === 'changed' || !state.tagToken;
  state.tagsLoading = true; state.tagsStatus = 'loading';
  updateStream(root, state, 'tags');
  const epoch = state.epoch;
  const page = await loadTags(fresh ? null : state.tagToken, state.area);
  if (!isCurrentState(root, state) || state.epoch !== epoch) return;
  state.tagsLoading = false;
  state.tagsStatus = streamStatus(page.status, page.tags.length + (fresh ? 0 : state.tags.length));
  if (page.status === 'available') {
    state.tags = fresh ? page.tags : [...state.tags, ...page.tags];
    state.tagToken = page.nextToken; state.tagsShown = state.tags.length;
  }
  updateStream(root, state, 'tags');
}

/** Chooses only a server-listed area, preferring the explicit link over browser convenience state. */
function selectedArea(areas: ReaderArea[]): string | null {
  // Storage is only a browser convenience: accept an id solely after it is confirmed by the server-owned area catalog.
  const requested = areaFromUrl();
  const stored = readStoredArea();
  return areas.some(candidate => candidate.id === requested)
    ? requested
    : areas.some(candidate => candidate.id === stored)
    ? stored
    : areas.find(candidate => candidate.isDefault)?.id ?? areas[0]?.id ?? null;
}

function areaFromUrl(): string | null {
  try { return new URL(location.href).searchParams.get('area'); }
  catch { return null; }
}

function readerHref(href: string, area: string | null): string {
  if (!area || (href !== '/memory-reader' && !href.startsWith('/memory-reader/'))) return href;
  const separator = href.includes('?') ? '&' : '?';
  return href.includes('area=') ? href : `${href}${separator}area=${encodeURIComponent(area)}`;
}

function readStoredArea(): string | null {
  try { return sessionStorage.getItem('agmemory.reader.area'); }
  catch { return null; }
}

/** Canonicalizes the validated area in the current link and stores only its safe convenience ID. */
function persistArea(area: string | null): void {
  if (!area) return;
  const url = new URL(location.href);
  if (url.searchParams.get('area') !== area) {
    url.searchParams.set('area', area);
    history.replaceState(history.state, '', url.pathname + url.search);
  }
  try { sessionStorage.setItem('agmemory.reader.area', area); }
  catch { /* Storage may be disabled; the validated area remains in the current view state. */ }
}
