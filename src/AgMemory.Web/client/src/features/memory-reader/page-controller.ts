import { load, loadCatalog, loadTags, loadTree, type CatalogResponse, type TreeNode } from './api.js';
import type { MemoryReaderDocument, MemoryReaderRelation } from './sanitize.js';
import { ui } from '../../ui-kit/index.js';

let observer: IntersectionObserver | undefined;
let activeRoot: HTMLElement | undefined;
let readerNavigation: ((event: MouseEvent) => void) | undefined;
let disposed = false;

type CatalogState = { token: string | null; treeToken: string | null; tagToken: string | null; namespace: string | null; tag: string | null; search: string; documents: CatalogResponse['documents']; tags: CatalogResponse['tags']; tree: TreeNode[]; treeShown: number; tagsShown: number };
type DocumentState = { content: MemoryReaderDocument; routeKey: string; tree: TreeNode[]; treeToken: string | null; treeShown: number };

export function dispose(): void {
  disposed = true;
  observer?.disconnect();
  if (activeRoot && readerNavigation) activeRoot.removeEventListener('click', readerNavigation);
  observer = undefined;
  readerNavigation = undefined;
  activeRoot = undefined;
}

export async function mountMemoryReaderPage(routeKey: string): Promise<void> {
  dispose(); disposed = false;
  const root = document.querySelector<HTMLElement>('#memory-reader-root');
  if (!root) return;
  activeRoot = root;
  readerNavigation = event => {
    const link = (event.target as Element).closest<HTMLAnchorElement>('a[href]');
    if (!link || link.target || event.metaKey || link.origin !== location.origin) return;
    const match = /^\/memory-reader\/([^/?#]+)$/.exec(link.pathname);
    if (!match) return;
    event.preventDefault();
    event.stopPropagation();
    void loadDetailInPlace(root, decodeURIComponent(match[1]));
  };
  root.addEventListener('click', readerNavigation);
  if (routeKey) { await mountDocument(root, routeKey); return; }
  await mountCatalog(root);
}

function isCurrent(root: HTMLElement): boolean { return !disposed && activeRoot === root; }

async function mountDocument(root: HTMLElement, routeKey: string): Promise<void> {
  // The document and its navigation rail are separate paged resources. Loading them together
  // keeps direct card navigation from replacing the persistent Wiki tree with a main-only view.
  const [content, tree] = await Promise.all([load(routeKey), loadTree()]);
  if (!isCurrent(root)) return;
  if (content.status !== 'available') { root.innerHTML = ui.status('Страница недоступна или изменилась.', 'warning'); return; }
  renderDocument(root, { content, routeKey, tree: tree.roots, treeToken: tree.nextToken, treeShown: 30 });
}

function renderDocument(root: HTMLElement, state: DocumentState): void {
  root.innerHTML = `<div class="memory-reader-layout">${treeMarkup(state.tree, state.treeShown, state.treeToken, state.routeKey)}<main class="memory-reader-main"></main></div>`;
  const main = root.querySelector<HTMLElement>('.memory-reader-main')!;
  renderDocumentInto(main, state.content, state.routeKey, next => renderDocument(root, { ...state, content: next }));
  bindDocumentTree(root, state);
}

function renderDocumentInto(
  main: HTMLElement,
  content: MemoryReaderDocument,
  routeKey: string,
  onMoreBlocks: (content: MemoryReaderDocument) => void): void {
  const e = ui.escape;
  const blocks = content.blocks.map(block => `<section class="memory-reader-block"><h2>${e(block.heading || 'Результат')}</h2><p class="memory-reader-block__content">${block.content.map(run => run.kind === 'link' && run.routeKey ? `<a href="/memory-reader/${encodeURIComponent(run.routeKey)}">${e(run.text)}</a>` : e(run.text)).join('')}</p></section>`).join('');
  main.innerHTML = `<article class="memory-reader-document"><header class="memory-reader-document-header"><p class="eyebrow">Вопрос</p><h1>${e(content.title || 'Запись памяти')}</h1><p class="small-copy">${e(content.namespace || '')}</p><div>${content.tags.map(tag => ui.chip(tag)).join('')}</div></header><section aria-label="Результат"><p class="eyebrow">Результат</p>${blocks || ui.status('В этой странице пока нет результата.', 'warning')}</section>${relations('Другие страницы', [...content.children, ...content.related, ...content.backlinks])}<div class="memory-reader-actions">${content.nextBlockToken ? ui.button('Загрузить ещё результат', { id: 'reader-more-blocks', secondary: true }) : ''}</div></article>`;
  const button = main.querySelector<HTMLButtonElement>('#reader-more-blocks');
  if (button && content.nextBlockToken) button.addEventListener('click', async () => {
    button.disabled = true;
    const next = await load(routeKey, content.nextBlockToken);
    if (next.status === 'available') onMoreBlocks({ ...next, blocks: [...content.blocks, ...next.blocks] });
  });
}

async function loadDetailInPlace(root: HTMLElement, routeKey: string): Promise<void> {
  const main = root.querySelector<HTMLElement>('.memory-reader-main');
  if (!main) return;
  const content = await load(routeKey);
  if (!isCurrent(root) || content.status !== 'available') return;
  root.querySelectorAll<HTMLAnchorElement>('.memory-reader-tree__link').forEach(link =>
    link.classList.toggle('is-active', link.pathname === `/memory-reader/${routeKey}`));
  const render = (page: MemoryReaderDocument) => renderDocumentInto(main, page, routeKey, render);
  render(content);
}

function treeMarkup(tree: TreeNode[], shown: number, token: string | null, activeRouteKey = ''): string {
  const e = ui.escape;
  const activeHref = activeRouteKey ? `/memory-reader/${encodeURIComponent(activeRouteKey)}` : '';
  const nodes = flattenTree(tree).slice(0, shown);
  return `<aside class="memory-reader-tree"><div class="memory-reader-tree__header"><h2>Дерево страниц</h2></div><nav class="memory-reader-tree__nav" role="tree"><ul>${nodes.map(node => `<li role="treeitem" data-parent-key="${e(node.parentKey || '')}" style="margin-inline-start:${node.depth * 0.75}rem">${node.href ? ui.link(node.href, `${node.label}${node.linkWeight !== null ? ` (${node.linkWeight})` : ''}`, `memory-reader-tree__link${node.href === activeHref ? ' is-active' : ''}`) : `<span class="memory-reader-tree__folder">${e(node.label)}</span>`}</li>`).join('')}</ul></nav><div id="reader-tree-sentinel"></div>${token ? ui.button('Загрузить ещё дерево', { id: 'reader-more-tree', secondary: true }) : ''}</aside>`;
}

function bindDocumentTree(root: HTMLElement, state: DocumentState): void {
  const moreTree = () => void moreDocumentTreePages(root, state);
  root.querySelector('#reader-more-tree')?.addEventListener('click', moreTree);
  observer?.disconnect();
  observer = new IntersectionObserver(entries => {
    for (const entry of entries) if (entry.isIntersecting && entry.target.id === 'reader-tree-sentinel' && state.treeToken) moreTree();
  }, { rootMargin: '160px' });
  root.querySelectorAll('#reader-tree-sentinel').forEach(node => observer?.observe(node));
}

async function moreDocumentTreePages(root: HTMLElement, state: DocumentState): Promise<void> {
  if (!state.treeToken) return;
  const page = await loadTree(state.treeToken);
  if (!isCurrent(root)) return;
  state.tree.push(...page.roots); state.treeToken = page.nextToken; state.treeShown = state.tree.length;
  renderDocument(root, state);
}

function relations(heading: string, values: MemoryReaderRelation[]): string {
  if (!values.length) return '';
  const e = ui.escape;
  const rows = values.map(value => `<tr><td><span class="memory-reader-link-kind memory-reader-link-kind--${e(value.kind)}">${e(value.kind)}</span></td><td>${ui.link(value.href, value.title)}<br><small>${e(value.namespace)}</small></td><td><span class="memory-reader-link-weight">${value.sharedEntityCount}</span></td></tr>`).join('');
  return ui.container(`<h2>${e(heading)}</h2>${ui.table('<tr><th>Тип</th><th>Страница</th><th>Вес</th></tr>', rows, 'memory-reader-links__table')}`, 'memory-reader-links');
}

async function mountCatalog(root: HTMLElement): Promise<void> {
  const [catalog, tree, tagPage] = await Promise.all([loadCatalog(), loadTree(), loadTags()]);
  if (!isCurrent(root)) return;
  const state: CatalogState = { token: catalog.nextToken, treeToken: tree.nextToken, tagToken: tagPage.nextToken, namespace: null, tag: null, search: '', documents: catalog.documents, tags: tagPage.tags, tree: tree.roots, treeShown: 30, tagsShown: 12 };
  renderCatalog(root, state);
}

function renderCatalog(root: HTMLElement, state: CatalogState): void {
  const e = ui.escape;
  const documents = state.documents.map(doc => ui.card(`<p class="eyebrow">${e(doc.namespace)}</p><h2>${ui.link(doc.href, doc.title)}</h2><p>${e(doc.preview)}</p><p class="memory-reader-card-tags">${doc.tags.map(e).join(' · ')}</p>`, 'memory-reader-catalog__item')).join('') || ui.status('Память пока пуста или недоступна.', 'warning');
  const tags = state.tags.slice(0, state.tagsShown).map(tag => `<button type="button" class="memory-reader-facet${state.tag === tag.locator ? ' is-active' : ''}" data-reader-tag="${e(tag.locator)}">${e(tag.label)} <small>${tag.count}</small></button>`).join('');
  root.innerHTML = `<section class="memory-reader-wiki__toolbar"><div><h2>Каталог страниц</h2><p class="small-copy">Поиск выполняется только по вопросу и краткому результату.</p></div><form id="reader-search" class="memory-reader-wiki__actions"><label class="visually-hidden" for="reader-search-input">Поиск</label>${ui.input('reader-search-input', state.search, 'Поиск по Wiki')}${ui.button('Найти', { type: 'submit' })}</form></section><div class="memory-reader-layout">${treeMarkup(state.tree, state.treeShown, state.treeToken)}<main class="memory-reader-main"><section class="memory-reader-facets"><div class="memory-reader-facets__group"><span>Теги</span>${tags || '<span class="muted">Нет тегов</span>'}</div><div id="reader-tag-sentinel"></div>${state.tagToken ? ui.button('Загрузить ещё теги', { id: 'reader-more-tags', secondary: true }) : ''}</section><section class="memory-reader-catalog">${documents}</section><div id="reader-catalog-sentinel"></div>${state.token ? `<div class="memory-reader-actions">${ui.button('Загрузить ещё страницы', { id: 'reader-more-catalog', secondary: true })}</div>` : ''}</main></div>`;
  root.querySelector<HTMLFormElement>('#reader-search')?.addEventListener('submit', event => { event.preventDefault(); const input = root.querySelector<HTMLInputElement>('#reader-search-input'); void resetCatalog(root, state, null, input?.value ?? ''); });
  root.querySelectorAll<HTMLButtonElement>('[data-reader-tag]').forEach(button => button.addEventListener('click', () => void resetCatalog(root, state, button.dataset.readerTag || null, state.search)));
  bindProgressive(root, state);
}

function bindProgressive(root: HTMLElement, state: CatalogState): void {
  const moreTree = () => void moreTreePages(root, state);
  const moreTags = () => void moreTagsPages(root, state);
  root.querySelector('#reader-more-tree')?.addEventListener('click', moreTree);
  root.querySelector('#reader-more-tags')?.addEventListener('click', moreTags);
  root.querySelector('#reader-more-catalog')?.addEventListener('click', () => void moreCatalog(root, state));
  observer?.disconnect();
  observer = new IntersectionObserver(entries => { for (const entry of entries) if (entry.isIntersecting) { if (entry.target.id === 'reader-tree-sentinel' && state.treeToken) moreTree(); else if (entry.target.id === 'reader-tag-sentinel' && state.tagToken) moreTags(); else if (entry.target.id === 'reader-catalog-sentinel' && state.token) void moreCatalog(root, state); } }, { rootMargin: '160px' });
  root.querySelectorAll('#reader-tree-sentinel, #reader-tag-sentinel, #reader-catalog-sentinel').forEach(node => observer?.observe(node));
}

async function resetCatalog(root: HTMLElement, state: CatalogState, tag: string | null, search: string): Promise<void> {
  const normalizedSearch = search.trim().slice(0, 120);
  // Facets are an independent paged stream. A filter/search reset must never retain its old cursor or rows.
  const [catalog, tagPage] = await Promise.all([loadCatalog(null, state.namespace, tag, normalizedSearch), loadTags()]);
  if (!isCurrent(root)) return;
  Object.assign(state, { token: catalog.nextToken, tag, search: normalizedSearch, documents: catalog.documents, tags: tagPage.tags, tagToken: tagPage.nextToken, tagsShown: 12 }); renderCatalog(root, state);
}

async function moreCatalog(root: HTMLElement, state: CatalogState): Promise<void> {
  if (!state.token) return;
  const catalog = await loadCatalog(state.token, state.namespace, state.tag, state.search);
  if (!isCurrent(root)) return;
  state.documents.push(...catalog.documents); state.token = catalog.nextToken; renderCatalog(root, state);
}

async function moreTreePages(root: HTMLElement, state: CatalogState): Promise<void> {
  if (!state.treeToken) return;
  const page = await loadTree(state.treeToken);
  if (!isCurrent(root)) return;
  state.tree.push(...page.roots); state.treeToken = page.nextToken; state.treeShown = state.tree.length; renderCatalog(root, state);
}

async function moreTagsPages(root: HTMLElement, state: CatalogState): Promise<void> {
  if (!state.tagToken) return;
  const page = await loadTags(state.tagToken);
  if (!isCurrent(root)) return;
  state.tags.push(...page.tags); state.tagToken = page.nextToken; state.tagsShown = state.tags.length; renderCatalog(root, state);
}

function flattenTree(nodes: TreeNode[]): TreeNode[] { return nodes.flatMap(node => [node, ...flattenTree(node.children)]); }
