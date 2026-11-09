import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('./api.js', () => ({ load: vi.fn(), loadAreas: vi.fn(), loadCatalog: vi.fn(), loadTags: vi.fn(), loadTree: vi.fn() }));

import { load, loadAreas, loadCatalog, loadTags, loadTree } from './api.js';
import { dispose, mountMemoryReaderPage } from './page-controller.js';

class TestObserver {
  constructor(_: IntersectionObserverCallback) {}
  observe() {}
  disconnect() {}
}

describe('memory reader page controller', () => {
  afterEach(() => { dispose(); document.body.innerHTML = ''; sessionStorage.clear(); history.replaceState(null, '', '/memory-reader'); vi.clearAllMocks(); });

  it('keeps deep parent context while capping indentation for a usable narrow tree rail', async () => {
    vi.stubGlobal('IntersectionObserver', TestObserver);
    vi.mocked(loadAreas).mockResolvedValue([]);
    vi.mocked(loadCatalog).mockResolvedValue({ status: 'available', documents: [], namespaces: [], tags: [], nextToken: null });
    vi.mocked(loadTags).mockResolvedValue({ status: 'available', tags: [], nextToken: null });
    vi.mocked(loadTree).mockResolvedValue({ status: 'available', roots: [{ kind: 'document', label: 'Deep',
      locator: null, href: '/memory-reader/deep', linkWeight: null, itemCount: 1, children: [],
      nodeKey: 'n299', parentKey: 'n298', depth: 299 }], nextToken: null });
    document.body.innerHTML = '<div id="memory-reader-root"></div>';
    await mountMemoryReaderPage('');
    const row = document.querySelector<HTMLElement>('.memory-reader-tree li');
    expect(row?.dataset.parentKey).toBe('n298');
    expect(row?.dataset.depth).toBe('299');
    expect(row?.style.marginInlineStart).toBe('6rem');
  });

  it('uses a server-listed area from a deep link and carries it into card and tree hrefs', async () => {
    vi.stubGlobal('IntersectionObserver', TestObserver);
    history.replaceState(null, '', '/memory-reader?area=product');
    vi.mocked(loadAreas).mockResolvedValue([{ id: 'engineering', label: 'Engineering', isDefault: true }, { id: 'product', label: 'Product', isDefault: false }]);
    vi.mocked(loadCatalog).mockResolvedValue({ status: 'available', documents: [{ href: '/memory-reader/product-card', type: 'Fact', title: 'Product', namespace: 'product', tags: [], preview: 'Only product' }], namespaces: [], tags: [], nextToken: null });
    vi.mocked(loadTree).mockResolvedValue({ status: 'available', roots: [{ kind: 'document', label: 'Product', locator: null, href: '/memory-reader/product-card', linkWeight: 1, itemCount: null, children: [], nodeKey: 'product', parentKey: null, depth: 0 }], nextToken: null });
    vi.mocked(loadTags).mockResolvedValue({ status: 'available', tags: [], nextToken: null });
    document.body.innerHTML = '<div id="memory-reader-root"></div>';

    await mountMemoryReaderPage('');

    expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, null, 'product');
    expect(document.querySelector<HTMLAnchorElement>('.memory-reader-catalog a')?.getAttribute('href')).toBe('/memory-reader/product-card?area=product');
    expect(document.querySelector<HTMLAnchorElement>('.memory-reader-tree a')?.getAttribute('href')).toBe('/memory-reader/product-card?area=product');
  });

  it('resets the independent tag stream on search and fetches its next page from the network', async () => {
    vi.stubGlobal('IntersectionObserver', TestObserver);
    vi.mocked(loadAreas).mockResolvedValue([]);
    vi.mocked(loadCatalog).mockResolvedValue({ status: 'available', documents: [], namespaces: [], tags: [], nextToken: null });
    vi.mocked(loadTree).mockResolvedValue({ status: 'available', roots: [], nextToken: null });
    vi.mocked(loadTags)
      .mockResolvedValueOnce({ status: 'available', tags: [{ locator: 'tag/' + 'a'.repeat(64), label: 'Первый', count: 1 }], nextToken: 'tags-2' })
      .mockResolvedValueOnce({ status: 'available', tags: [{ locator: 'tag/' + 'b'.repeat(64), label: 'После поиска', count: 1 }], nextToken: 'tags-3' })
      .mockResolvedValueOnce({ status: 'available', tags: [{ locator: 'tag/' + 'c'.repeat(64), label: 'Следующая страница', count: 1 }], nextToken: null });
    document.body.innerHTML = '<div id="memory-reader-root"></div>';

    await mountMemoryReaderPage('');
    const input = document.querySelector<HTMLInputElement>('#reader-search-input')!;
    input.value = 'новый запрос';
    document.querySelector<HTMLFormElement>('#reader-search')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(document.body.textContent).toContain('После поиска'));
    expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, 'новый запрос', null);
    expect(document.body.textContent).toContain('После поиска');
    expect(document.body.textContent).not.toContain('Первый');
    expect(loadTree).toHaveBeenCalledTimes(1);

    document.querySelector<HTMLButtonElement>('#reader-more-tags')!.click();
    await vi.waitFor(() => expect(loadTags).toHaveBeenCalledWith('tags-3', null));
    expect(document.body.textContent).toContain('Следующая страница');
  });

  it('keeps the paged tree in the left rail when a card document opens', async () => {
    vi.stubGlobal('IntersectionObserver', TestObserver);
    vi.mocked(loadAreas).mockResolvedValue([]);
    vi.mocked(load).mockResolvedValue({
      status: 'available', routeKey: 'current-card', title: 'Открытая карточка', namespace: 'wiki', tags: [],
      children: [], related: [], backlinks: [], blocks: [{ id: 'answer', heading: 'Результат', content: [{ kind: 'text', text: 'Данные карточки', routeKey: null, blockToken: null }] }], nextBlockToken: null
    });
    vi.mocked(loadTree)
      .mockResolvedValueOnce({ status: 'available', roots: [{ kind: 'document', label: 'Открытая карточка', locator: null, href: '/memory-reader/current-card', linkWeight: 4, itemCount: null, children: [], nodeKey: 'current', parentKey: null, depth: 0 }], nextToken: 'tree-2' })
      .mockResolvedValueOnce({ status: 'available', roots: [{ kind: 'document', label: 'Следующая карточка', locator: null, href: '/memory-reader/next-card', linkWeight: 2, itemCount: null, children: [], nodeKey: 'next', parentKey: null, depth: 0 }], nextToken: null });
    document.body.innerHTML = '<div id="memory-reader-root"></div>';

    await mountMemoryReaderPage('current-card');
    const layout = document.querySelector('.memory-reader-layout')!;
    expect(layout.firstElementChild?.tagName).toBe('ASIDE');
    expect(document.querySelector('.memory-reader-main')?.textContent).toContain('Данные карточки');
    expect(document.querySelector<HTMLAnchorElement>('.memory-reader-tree__link.is-active')?.getAttribute('href')).toBe('/memory-reader/current-card');

    document.querySelector<HTMLButtonElement>('#reader-more-tree')!.click();
    await vi.waitFor(() => expect(loadTree).toHaveBeenLastCalledWith('tree-2', null));
    expect(document.body.textContent).toContain('Следующая карточка');
    expect(document.querySelector('.memory-reader-main')?.textContent).toContain('Данные карточки');
  });

  it('loads a card detail into the right pane without requesting the tree again', async () => {
    vi.stubGlobal('IntersectionObserver', TestObserver);
    vi.mocked(loadAreas).mockResolvedValue([]);
    vi.mocked(loadCatalog).mockResolvedValue({
      status: 'available',
      documents: [{ href: '/memory-reader/open-card', type: 'Fact', title: 'Карточка', namespace: 'wiki', tags: [], preview: 'Кратко' }],
      namespaces: [], tags: [], nextToken: null
    });
    vi.mocked(loadTags).mockResolvedValue({ status: 'available', tags: [], nextToken: null });
    vi.mocked(loadTree).mockResolvedValue({
      status: 'available',
      roots: [{ kind: 'document', label: 'Карточка', locator: null, href: '/memory-reader/open-card', linkWeight: 1, itemCount: null, children: [], nodeKey: 'open', parentKey: null, depth: 0 }],
      nextToken: null
    });
    vi.mocked(load).mockResolvedValue({
      status: 'available', routeKey: 'open-card', title: 'Карточка', namespace: 'wiki', tags: [], children: [], related: [], backlinks: [],
      blocks: [{ id: 'answer', heading: 'Результат', content: [{ kind: 'text', text: 'Детали справа', routeKey: null, blockToken: null }] }], nextBlockToken: null
    });
    document.body.innerHTML = '<div id="memory-reader-root"></div>';

    await mountMemoryReaderPage('');
    document.querySelector<HTMLAnchorElement>('.memory-reader-catalog a[href="/memory-reader/open-card"]')!.click();
    await vi.waitFor(() => expect(document.querySelector('.memory-reader-main')?.textContent).toContain('Детали справа'));

    expect(load).toHaveBeenCalledWith('open-card', null, null);
    expect(loadTree).toHaveBeenCalledTimes(1);
    expect(document.querySelector('.memory-reader-tree')?.textContent).toContain('Карточка');
    expect(document.querySelector<HTMLAnchorElement>('.memory-reader-tree__link')?.classList.contains('is-active')).toBe(true);
  });

  it('changes area by resetting every paged reader stream', async () => {
    vi.stubGlobal('IntersectionObserver', TestObserver);
    vi.mocked(loadAreas).mockResolvedValue([{ id: 'engineering', label: 'Engineering', isDefault: true }, { id: 'product', label: 'Product', isDefault: false }]);
    vi.mocked(loadCatalog).mockResolvedValue({ status: 'available', documents: [], namespaces: [], tags: [], nextToken: null });
    vi.mocked(loadTree).mockResolvedValue({ status: 'available', roots: [], nextToken: null });
    vi.mocked(loadTags).mockResolvedValue({ status: 'available', tags: [], nextToken: null });
    document.body.innerHTML = '<div id="memory-reader-root"></div>';
    await mountMemoryReaderPage('');
    const selector = document.querySelector<HTMLSelectElement>('#reader-area')!;
    selector.value = 'product'; selector.dispatchEvent(new Event('change'));
    await vi.waitFor(() => expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, null, 'product'));
    expect(loadTree).toHaveBeenLastCalledWith(null, 'product');
    expect(loadTags).toHaveBeenLastCalledWith(null, 'product');
  });

  it('keeps a server-recognized stored area when returning from a detail page to the catalog', async () => {
    vi.stubGlobal('IntersectionObserver', TestObserver);
    sessionStorage.setItem('agmemory.reader.area', 'product');
    vi.mocked(loadAreas).mockResolvedValue([{ id: 'engineering', label: 'Engineering', isDefault: true }, { id: 'product', label: 'Product', isDefault: false }]);
    vi.mocked(load).mockResolvedValue({
      status: 'available', routeKey: 'product-card', title: 'Карточка продукта', namespace: 'product', tags: [], children: [], related: [], backlinks: [],
      blocks: [{ id: 'answer', heading: 'Результат', content: [{ kind: 'text', text: 'Детали продукта', routeKey: null, blockToken: null }] }], nextBlockToken: null
    });
    vi.mocked(loadCatalog).mockResolvedValue({ status: 'available', documents: [], namespaces: [], tags: [], nextToken: null });
    vi.mocked(loadTree).mockResolvedValue({ status: 'available', roots: [], nextToken: null });
    vi.mocked(loadTags).mockResolvedValue({ status: 'available', tags: [], nextToken: null });
    document.body.innerHTML = '<div id="memory-reader-root"></div>';

    await mountMemoryReaderPage('product-card');
    expect(load).toHaveBeenCalledWith('product-card', null, 'product');
    expect(loadTree).toHaveBeenLastCalledWith(null, 'product');

    await mountMemoryReaderPage('');
    expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, null, 'product');
    expect(loadTree).toHaveBeenLastCalledWith(null, 'product');
    expect(loadTags).toHaveBeenLastCalledWith(null, 'product');
    expect(sessionStorage.getItem('agmemory.reader.area')).toBe('product');
  });

  it('ignores an unknown stored area and selects the server default', async () => {
    vi.stubGlobal('IntersectionObserver', TestObserver);
    sessionStorage.setItem('agmemory.reader.area', 'untrusted-area');
    vi.mocked(loadAreas).mockResolvedValue([{ id: 'engineering', label: 'Engineering', isDefault: true }, { id: 'product', label: 'Product', isDefault: false }]);
    vi.mocked(loadCatalog).mockResolvedValue({ status: 'available', documents: [], namespaces: [], tags: [], nextToken: null });
    vi.mocked(loadTree).mockResolvedValue({ status: 'available', roots: [], nextToken: null });
    vi.mocked(loadTags).mockResolvedValue({ status: 'available', tags: [], nextToken: null });
    document.body.innerHTML = '<div id="memory-reader-root"></div>';

    await mountMemoryReaderPage('');

    expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, null, 'engineering');
    expect(sessionStorage.getItem('agmemory.reader.area')).toBe('engineering');
  });
  it('commits only the latest detail navigation to content, title and history', async () => {
    await mountTwoCardCatalog();
    const first = deferred<Awaited<ReturnType<typeof load>>>();
    const second = deferred<Awaited<ReturnType<typeof load>>>();
    vi.mocked(load).mockImplementation(key => key === 'first' ? first.promise : second.promise);
    document.querySelector<HTMLAnchorElement>('a[href="/memory-reader/first"]')!.click();
    document.querySelector<HTMLAnchorElement>('a[href="/memory-reader/second"]')!.click();
    second.resolve(readerDocument('second', 'Вторая страница'));
    await vi.waitFor(() => expect(location.pathname).toBe('/memory-reader/second'));
    first.resolve(readerDocument('first', 'Первая страница'));
    await first.promise;
    expect(document.querySelector('.memory-reader-main')?.textContent).toContain('Вторая страница');
    expect(document.querySelector('.memory-reader-main')?.textContent).not.toContain('Первая страница');
    expect(document.title).toBe('Вторая страница · AgMemory');
    expect(document.activeElement?.tagName).toBe('H1');
    expect(document.querySelector('a.is-active')?.getAttribute('aria-current')).toBe('page');
    expect(loadTree).toHaveBeenCalledTimes(1);
  });

  it('does not intercept a modified document click', async () => {
    await mountTwoCardCatalog();
    const click = new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: true });
    // Prevent jsdom's unsupported browser navigation after testing the reader listener.
    document.addEventListener('click', event => event.preventDefault(), { once: true });
    document.querySelector<HTMLAnchorElement>('a[href="/memory-reader/first"]')!.dispatchEvent(click);
    expect(load).not.toHaveBeenCalled();
  });

  it('does not let a pending catalog tree request restore the catalog over an opened page', async () => {
    await mountTwoCardCatalog('tree-next');
    const page = deferred<Awaited<ReturnType<typeof loadTree>>>();
    vi.mocked(loadTree).mockReturnValueOnce(page.promise);
    document.querySelector<HTMLButtonElement>('#reader-more-tree')!.click();
    vi.mocked(load).mockResolvedValue(readerDocument('first', 'Открытая страница'));
    document.querySelector<HTMLAnchorElement>('a[href="/memory-reader/first"]')!.click();
    await vi.waitFor(() => expect(document.querySelector('.memory-reader-main')?.textContent).toContain('Открытая страница'));
    page.resolve({ status: 'available', roots: [], nextToken: null });
    await page.promise;
    expect(document.querySelector('.memory-reader-main')?.textContent).toContain('Открытая страница');
    expect(document.querySelector('.memory-reader-catalog')).toBeNull();
    expect(document.querySelector('#reader-more-tree')).not.toBeNull();
  });

  it('fetches one tree continuation at a time and retains keyboard focus and scroll', async () => {
    await mountTwoCardCatalog('tree-next');
    const page = deferred<Awaited<ReturnType<typeof loadTree>>>();
    vi.mocked(loadTree).mockReturnValueOnce(page.promise);
    const tree = document.querySelector<HTMLElement>('.memory-reader-tree')!;
    tree.scrollTop = 80;
    const button = document.querySelector<HTMLButtonElement>('#reader-more-tree')!;
    button.focus(); button.click(); button.click();
    expect(loadTree).toHaveBeenCalledTimes(2);
    page.resolve({ status: 'available', roots: [], nextToken: 'tree-last' });
    await vi.waitFor(() => expect(document.querySelector<HTMLElement>('.memory-reader-tree')?.scrollTop).toBe(80));
    expect(document.activeElement?.id).toBe('reader-more-tree');
    expect(document.querySelector('[role="tree"]')).toBeNull();
    expect(document.querySelector('.memory-reader-tree__nav')?.getAttribute('aria-label')).toBe('Дерево страниц');
  });

  it('shows failed document navigation without changing the shared URL', async () => {
    await mountTwoCardCatalog();
    vi.mocked(load).mockResolvedValue({ ...readerDocument('first', 'Недоступно'), status: 'unavailable' });
    document.querySelector<HTMLAnchorElement>('a[href="/memory-reader/first"]')!.click();
    await vi.waitFor(() => expect(document.querySelector('#reader-navigation-error')?.textContent).toContain('Страница недоступна'));
    expect(location.pathname).toBe('/memory-reader');
    expect(document.querySelector('.memory-reader-main')?.hasAttribute('aria-busy')).toBe(false);
  });

  it('pages the retained tree of a newly opened document without replacing its content DOM', async () => {
    await mountTwoCardCatalog('tree-next');
    vi.mocked(load).mockResolvedValue(readerDocument('second', 'Новая страница'));
    document.querySelector<HTMLAnchorElement>('a[href="/memory-reader/second"]')!.click();
    await vi.waitFor(() => expect(location.pathname).toBe('/memory-reader/second'));
    const article = document.querySelector('.memory-reader-document');
    vi.mocked(loadTree).mockResolvedValue({ status: 'available', roots: [treeRow('third')], nextToken: null });
    document.querySelector<HTMLButtonElement>('#reader-more-tree')!.click();
    await vi.waitFor(() => expect(document.querySelector('a[href="/memory-reader/third"]')).not.toBeNull());
    expect(document.querySelector('.memory-reader-document')).toBe(article);
    expect(document.querySelector('.memory-reader-main')?.textContent).toContain('Новая страница');
  });

  it('prevents duplicate catalog continuations when the button is clicked twice', async () => {
    await mountTwoCardCatalog();
    vi.mocked(loadCatalog).mockResolvedValue({ status: 'available', documents: [], namespaces: [], tags: [], nextToken: 'catalog-next' });
    await mountMemoryReaderPage('');
    const page = deferred<Awaited<ReturnType<typeof loadCatalog>>>();
    vi.mocked(loadCatalog).mockReturnValueOnce(page.promise);
    const button = document.querySelector<HTMLButtonElement>('#reader-more-catalog')!;
    button.click(); button.click();
    expect(loadCatalog).toHaveBeenCalledTimes(3);
    page.resolve({ status: 'available', documents: [{ href: '/memory-reader/last', title: 'Последняя', type: 'Fact', namespace: 'wiki', preview: 'Последняя', tags: [] }], namespaces: [], tags: [], nextToken: null });
    await vi.waitFor(() => expect(document.querySelectorAll('.memory-reader-catalog__item')).toHaveLength(1));
  });

  it('allows retrying a failed block continuation', async () => {
    await mountTwoCardCatalog();
    const content = { ...readerDocument('first', 'Страница'), nextBlockToken: 'blocks-next' };
    vi.mocked(load).mockResolvedValueOnce(content).mockResolvedValueOnce({ ...content, status: 'unavailable' });
    document.querySelector<HTMLAnchorElement>('a[href="/memory-reader/first"]')!.click();
    await vi.waitFor(() => expect(document.querySelector('#reader-more-blocks')).not.toBeNull());
    document.querySelector<HTMLButtonElement>('#reader-more-blocks')!.click();
    await vi.waitFor(() => expect(document.querySelector<HTMLButtonElement>('#reader-more-blocks')?.disabled).toBe(false));
    expect(document.querySelector('#reader-more-blocks')?.textContent).toBe('Повторить загрузку результата');
  });

  it('keeps an unfinished search and its cursor when background tree paging rerenders the catalog', async () => {
    await mountTwoCardCatalog('tree-next');
    const input = document.querySelector<HTMLInputElement>('#reader-search-input')!;
    input.value = 'ещё набираю'; input.focus(); input.setSelectionRange(4, 4);
    vi.mocked(loadTree).mockResolvedValue({ status: 'available', roots: [treeRow('third')], nextToken: null });
    document.querySelector<HTMLButtonElement>('#reader-more-tree')!.click();
    await vi.waitFor(() => expect(document.querySelector('a[href="/memory-reader/third"]')).not.toBeNull());
    const replacement = document.querySelector<HTMLInputElement>('#reader-search-input')!;
    expect(replacement.value).toBe('ещё набираю');
    expect(replacement.selectionStart).toBe(4);
    expect(document.activeElement).toBe(replacement);
  });

  it('renders every server-bounded tree row instead of hiding the remainder without a cursor', async () => {
    await mountTwoCardCatalog();
    vi.mocked(loadTree).mockResolvedValue({ status: 'available', roots: Array.from({ length: 40 }, (_, i) => treeRow(`row-${i}`)), nextToken: null });
    await mountMemoryReaderPage('');
    expect(document.querySelectorAll('.memory-reader-tree__link')).toHaveLength(40);
  });

  it('synchronizes the selected area with a shareable URL and reloads the same area', async () => {
    history.replaceState(null, '', '/memory-reader?area=engineering');
    await mountTwoCardCatalog();
    vi.mocked(loadAreas).mockResolvedValue([{ id: 'engineering', label: 'Engineering', isDefault: true }, { id: 'product', label: 'Product', isDefault: false }]);
    await mountMemoryReaderPage('');
    const selector = document.querySelector<HTMLSelectElement>('#reader-area')!;
    selector.value = 'product'; selector.dispatchEvent(new Event('change', { bubbles: true }));
    await vi.waitFor(() => expect(location.search).toBe('?area=product'));
    await mountMemoryReaderPage('');
    expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, null, 'product');
    expect(JSON.stringify(history.state)).not.toContain('search');
  });

  it('returns to the filtered catalog and rail scroll through a memory-only history snapshot', async () => {
    await mountTwoCardCatalog();
    const input = document.querySelector<HTMLInputElement>('#reader-search-input')!;
    input.value = 'private raw query';
    document.querySelector<HTMLFormElement>('#reader-search')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, 'private raw query', null));
    const tree = document.querySelector<HTMLElement>('.memory-reader-tree')!;
    tree.scrollTop = 71;
    vi.mocked(load).mockResolvedValue(readerDocument('first', 'Page'));
    document.querySelector<HTMLAnchorElement>('.memory-reader-catalog a')!.click();
    await vi.waitFor(() => expect(location.pathname).toBe('/memory-reader/first'));
    history.back();
    await vi.waitFor(() => expect(location.pathname).toBe('/memory-reader'));
    await mountMemoryReaderPage('');
    expect(document.querySelector<HTMLInputElement>('#reader-search-input')?.value).toBe('private raw query');
    expect(document.querySelector<HTMLElement>('.memory-reader-tree')?.scrollTop).toBe(71);
    expect(document.querySelectorAll('.memory-reader-catalog__item')).toHaveLength(2);
    expect(JSON.stringify(history.state)).not.toContain('private raw query');
    expect(JSON.stringify(localStorage)).not.toContain('private raw query');
    expect(JSON.stringify(sessionStorage)).not.toContain('private raw query');
  });

  it('shows each stream while another is pending and distinguishes an initial failure from empty memory', async () => {
    await mountTwoCardCatalog();
    const catalog = deferred<Awaited<ReturnType<typeof loadCatalog>>>();
    vi.mocked(loadCatalog).mockReturnValueOnce(catalog.promise);
    const mounting = mountMemoryReaderPage('');
    await vi.waitFor(() => expect(document.querySelector('.memory-reader-tree__link')).not.toBeNull());
    expect(document.querySelector('#reader-results')?.getAttribute('aria-busy')).toBe('true');
    catalog.resolve({ status: 'unavailable', documents: [], tags: [], namespaces: [], nextToken: null });
    await mounting;
    expect(document.querySelector('#reader-results')?.textContent).toContain('Не удалось загрузить');
    expect(document.querySelector('#reader-results')?.textContent).not.toContain('Память пока пуста');
    vi.mocked(loadCatalog).mockResolvedValue({ status: 'available', documents: [], tags: [], namespaces: [], nextToken: null });
    document.querySelector<HTMLButtonElement>('#reader-more-catalog')!.click();
    await vi.waitFor(() => expect(document.querySelector('#reader-results')?.textContent).toContain('Память пока пуста'));
  });

  it('refreshes a changed catalog stream without appending a new generation into old rows', async () => {
    await mountTwoCardCatalog();
    vi.mocked(loadCatalog).mockResolvedValueOnce({ status: 'available', documents: [{ href: '/memory-reader/old', title: 'Old', type: 'Fact', namespace: 'wiki', preview: 'Old', tags: [] }], tags: [], namespaces: [], nextToken: 'old-cursor' });
    await mountMemoryReaderPage('');
    vi.mocked(loadCatalog).mockResolvedValueOnce({ status: 'changed', documents: [], tags: [], namespaces: [], nextToken: null });
    document.querySelector<HTMLButtonElement>('#reader-more-catalog')!.click();
    await vi.waitFor(() => expect(document.querySelector('#reader-results')?.textContent).toContain('Память изменилась'));
    expect(document.querySelector('.memory-reader-catalog')?.textContent).toContain('Old');
    vi.mocked(loadCatalog).mockResolvedValueOnce({ status: 'available', documents: [{ href: '/memory-reader/fresh', title: 'Fresh', type: 'Fact', namespace: 'wiki', preview: 'Fresh', tags: [] }], tags: [], namespaces: [], nextToken: null });
    document.querySelector<HTMLButtonElement>('#reader-more-catalog')!.click();
    await vi.waitFor(() => expect(document.querySelector('.memory-reader-catalog')?.textContent).toContain('Fresh'));
    expect(document.querySelector('.memory-reader-catalog')?.textContent).not.toContain('Old');
    expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, null, null);
  });

  it('retains focus on the exact tag locator through selection and independent tree paging', async () => {
    await mountTwoCardCatalog('tree-next');
    const tag = { locator: 'tag/' + 'a'.repeat(64), label: 'Label', count: 2 };
    vi.mocked(loadTags).mockResolvedValue({ status: 'available', tags: [tag], nextToken: null });
    await mountMemoryReaderPage('');
    const button = document.querySelector<HTMLButtonElement>('[data-reader-tag]')!;
    button.focus(); button.click();
    await vi.waitFor(() => expect(document.querySelector('[data-reader-tag]')?.getAttribute('aria-pressed')).toBe('true'));
    expect((document.activeElement as HTMLElement).dataset.readerTag).toBe(tag.locator);
    const input = document.querySelector('#reader-search-input');
    vi.mocked(loadTree).mockResolvedValueOnce({ status: 'available', roots: [treeRow('next')], nextToken: null });
    document.querySelector<HTMLButtonElement>('#reader-more-tree')!.click();
    await vi.waitFor(() => expect(document.querySelector('a[href="/memory-reader/next"]')).not.toBeNull());
    expect((document.activeElement as HTMLElement).dataset.readerTag).toBe(tag.locator);
    expect(document.querySelector('#reader-search-input')).toBe(input);
  });

  it('renders native collapsible hierarchy and reveals the current document without replacing its body', async () => {
    await mountTwoCardCatalog();
    const parent = { ...treeRow('folder'), kind: 'namespace', href: null, nodeKey: 'parent' };
    const child = { ...treeRow('first'), parentKey: 'parent', depth: 1 };
    vi.mocked(loadTree).mockResolvedValueOnce({ status: 'available', roots: [parent, child], nextToken: null });
    await mountMemoryReaderPage('');
    const branch = document.querySelector<HTMLDetailsElement>('[data-reader-branch="parent"]')!;
    expect(branch.querySelector('ul .memory-reader-tree__link')?.textContent).toContain('first');
    branch.open = false; branch.dispatchEvent(new Event('toggle'));
    vi.mocked(load).mockResolvedValueOnce(readerDocument('first', 'Open page'));
    document.querySelector<HTMLAnchorElement>('.memory-reader-catalog a')!.click();
    await vi.waitFor(() => expect(document.querySelector('.memory-reader-document')).not.toBeNull());
    expect(document.querySelector<HTMLDetailsElement>('[data-reader-branch="parent"]')?.open).toBe(true);
    const article = document.querySelector('.memory-reader-document');
    document.querySelector<HTMLButtonElement>('#reader-reveal-current')!.click();
    expect(document.querySelector('.memory-reader-document')).toBe(article);
    expect(document.querySelector('[role="tree"]')).toBeNull();
  });

  it('reports a failed document tree and refreshes it without discarding loaded document blocks', async () => {
    await mountTwoCardCatalog();
    vi.mocked(load).mockResolvedValueOnce(readerDocument('first', 'Retained page'));
    vi.mocked(loadTree).mockResolvedValueOnce({ status: 'unavailable', roots: [], nextToken: null });
    await mountMemoryReaderPage('first');
    expect(document.querySelector('#reader-tree-status')?.textContent).toContain('Не удалось загрузить');
    const article = document.querySelector('.memory-reader-document');
    vi.mocked(loadTree).mockResolvedValueOnce({ status: 'available', roots: [treeRow('first')], nextToken: null });
    document.querySelector<HTMLButtonElement>('#reader-more-tree')!.click();
    await vi.waitFor(() => expect(document.querySelector('.memory-reader-tree__link')).not.toBeNull());
    expect(document.querySelector('.memory-reader-document')).toBe(article);
  });

  it('appends result cards without replacing previously loaded card DOM or the search field', async () => {
    await mountTwoCardCatalog();
    vi.mocked(loadCatalog).mockResolvedValueOnce({ status: 'available', documents: [{ href: '/memory-reader/first', title: 'First', type: 'Fact', namespace: 'wiki', preview: 'First', tags: [] }], tags: [], namespaces: [], nextToken: 'next' });
    await mountMemoryReaderPage('');
    const firstCard = document.querySelector('.memory-reader-catalog__item');
    const input = document.querySelector('#reader-search-input');
    vi.mocked(loadCatalog).mockResolvedValueOnce({ status: 'available', documents: [{ href: '/memory-reader/second', title: 'Second', type: 'Fact', namespace: 'wiki', preview: 'Second', tags: [] }], tags: [], namespaces: [], nextToken: null });
    document.querySelector<HTMLButtonElement>('#reader-more-catalog')!.click();
    await vi.waitFor(() => expect(document.querySelectorAll('.memory-reader-catalog__item')).toHaveLength(2));
    expect(document.querySelector('.memory-reader-catalog__item')).toBe(firstCard);
    expect(document.querySelector('#reader-search-input')).toBe(input);
  });

  it('starts with a closed navigation disclosure on narrow screens', async () => {
    const original = window.matchMedia;
    window.matchMedia = vi.fn().mockReturnValue({ matches: true });
    try {
      await mountTwoCardCatalog();
      expect(document.querySelector<HTMLDetailsElement>('#reader-tree-panel')?.open).toBe(false);
      expect(document.querySelector('#reader-tree-panel > summary')?.textContent).toBe('Дерево страниц');
    } finally { window.matchMedia = original; }
  });

  it('restores both area history entries and their own filters through Back and Forward', async () => {
    await mountTwoCardCatalog();
    vi.mocked(loadAreas).mockResolvedValue([{ id: 'engineering', label: 'Engineering', isDefault: true }, { id: 'product', label: 'Product', isDefault: false }]);
    await mountMemoryReaderPage('');
    expect(location.search).toBe('?area=engineering');
    let input = document.querySelector<HTMLInputElement>('#reader-search-input')!;
    input.value = 'engineering phrase';
    document.querySelector<HTMLFormElement>('#reader-search')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, 'engineering phrase', 'engineering'));
    const selector = document.querySelector<HTMLSelectElement>('#reader-area')!;
    selector.value = 'product'; selector.dispatchEvent(new Event('change', { bubbles: true }));
    await vi.waitFor(() => expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, null, 'product'));
    input = document.querySelector<HTMLInputElement>('#reader-search-input')!;
    input.value = 'product phrase';
    document.querySelector<HTMLFormElement>('#reader-search')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, 'product phrase', 'product'));
    history.back();
    await vi.waitFor(() => expect(location.search).toBe('?area=engineering'));
    await mountMemoryReaderPage('');
    expect(document.querySelector<HTMLInputElement>('#reader-search-input')?.value).toBe('engineering phrase');
    history.forward();
    await vi.waitFor(() => expect(location.search).toBe('?area=product'));
    await mountMemoryReaderPage('');
    expect(document.querySelector<HTMLInputElement>('#reader-search-input')?.value).toBe('product phrase');
    expect(JSON.stringify(history.state)).not.toContain('phrase');
  });

  it('ignores a late old-area response after switching the URL and every stream to a new area', async () => {
    await mountTwoCardCatalog();
    vi.mocked(loadAreas).mockResolvedValue([{ id: 'engineering', label: 'Engineering', isDefault: true }, { id: 'product', label: 'Product', isDefault: false }]);
    const old = deferred<Awaited<ReturnType<typeof loadCatalog>>>();
    vi.mocked(loadCatalog).mockReturnValueOnce(old.promise);
    const mounting = mountMemoryReaderPage('');
    await vi.waitFor(() => expect(document.querySelector('#reader-area')).not.toBeNull());
    const selector = document.querySelector<HTMLSelectElement>('#reader-area')!;
    selector.value = 'product'; selector.dispatchEvent(new Event('change', { bubbles: true }));
    await vi.waitFor(() => expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, null, 'product'));
    old.resolve({ status: 'available', documents: [{ href: '/memory-reader/old', title: 'Wrong area', type: 'Fact', namespace: 'wiki', preview: 'Wrong area', tags: [] }], tags: [], namespaces: [], nextToken: 'old' });
    await mounting;
    expect(location.search).toBe('?area=product');
    expect(document.querySelector('.memory-reader-catalog')?.textContent).not.toContain('Wrong area');
  });


  it('restarts a discarded initial tree request when search creates a new catalog epoch', async () => {
    vi.stubGlobal('IntersectionObserver', TestObserver);
    vi.mocked(loadAreas).mockResolvedValue([]);
    vi.mocked(loadCatalog).mockResolvedValue({ status: 'available', documents: [], namespaces: [], tags: [], nextToken: null });
    vi.mocked(loadTags).mockResolvedValue({ status: 'available', tags: [], nextToken: null });
    const old = deferred<Awaited<ReturnType<typeof loadTree>>>();
    vi.mocked(loadTree).mockReturnValueOnce(old.promise).mockResolvedValueOnce({ status: 'available', roots: [treeRow('current-tree')], nextToken: null });
    document.body.innerHTML = '<div id="memory-reader-root"></div>';
    const mounting = mountMemoryReaderPage('');
    await vi.waitFor(() => expect(loadTree).toHaveBeenCalledTimes(1));
    const input = document.querySelector<HTMLInputElement>('#reader-search-input')!;
    input.value = 'search during tree loading'; input.focus();
    document.querySelector<HTMLFormElement>('#reader-search')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(document.querySelector('.memory-reader-tree')?.textContent).toContain('current-tree'));
    expect(loadTree).toHaveBeenCalledTimes(2);
    expect(loadTree).toHaveBeenLastCalledWith(null, null);
    old.resolve({ status: 'available', roots: [treeRow('discarded-tree')], nextToken: 'discarded-cursor' });
    await mounting;
    expect(document.querySelector('.memory-reader-tree')?.textContent).not.toContain('discarded-tree');
    expect(document.querySelector('#reader-tree-status')?.textContent).not.toContain('Загрузка');
    expect(document.querySelector<HTMLInputElement>('#reader-search-input')?.value).toBe('search during tree loading');
    expect(document.activeElement?.id).toBe('reader-search-input');
  });

  it('reissues only a pending tree continuation after filtering and retains already loaded rows', async () => {
    await mountTwoCardCatalog('tree-next');
    const old = deferred<Awaited<ReturnType<typeof loadTree>>>();
    const current = deferred<Awaited<ReturnType<typeof loadTree>>>();
    vi.mocked(loadTree).mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise);
    document.querySelector<HTMLButtonElement>('#reader-more-tree')!.click();
    document.querySelector<HTMLInputElement>('#reader-search-input')!.value = 'new filter';
    document.querySelector<HTMLFormElement>('#reader-search')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(loadTree).toHaveBeenCalledTimes(3));
    expect(vi.mocked(loadTree).mock.calls.slice(-2)).toEqual([['tree-next', null], ['tree-next', null]]);
    current.resolve({ status: 'available', roots: [treeRow('current-page')], nextToken: null });
    await vi.waitFor(() => expect(document.querySelector('.memory-reader-tree')?.textContent).toContain('current-page'));
    old.resolve({ status: 'available', roots: [treeRow('discarded-page')], nextToken: null });
    await old.promise;
    expect(document.querySelector('.memory-reader-tree')?.textContent).toContain('first');
    expect(document.querySelector('.memory-reader-tree')?.textContent).toContain('second');
    expect(document.querySelectorAll('.memory-reader-tree__link')).toHaveLength(3);
    expect(document.querySelector('.memory-reader-tree')?.textContent).not.toContain('discarded-page');
    expect(document.querySelector('#reader-tree-status')?.textContent).not.toContain('Загрузка');
  });

  it('makes a restarted tree failure explicitly recoverable after the discarded response finishes', async () => {
    await mountTwoCardCatalog('tree-next');
    const old = deferred<Awaited<ReturnType<typeof loadTree>>>();
    vi.mocked(loadTree).mockReturnValueOnce(old.promise).mockResolvedValueOnce({ status: 'unavailable', roots: [], nextToken: null });
    document.querySelector<HTMLButtonElement>('#reader-more-tree')!.click();
    document.querySelector<HTMLFormElement>('#reader-search')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(document.querySelector('#reader-tree-status')?.textContent).toContain('Не удалось загрузить'));
    old.resolve({ status: 'available', roots: [treeRow('discarded-page')], nextToken: null });
    await old.promise;
    const retry = document.querySelector<HTMLButtonElement>('#reader-more-tree')!;
    expect(retry.disabled).toBe(false);
    expect(retry.textContent).toBe('Повторить загрузку дерева');
    vi.mocked(loadTree).mockResolvedValueOnce({ status: 'available', roots: [treeRow('recovered-page')], nextToken: null });
    retry.click();
    await vi.waitFor(() => expect(document.querySelector('.memory-reader-tree')?.textContent).toContain('recovered-page'));
    expect(document.querySelectorAll('.memory-reader-tree__link')).toHaveLength(3);
  });

});


/** Provides controllable response ordering for navigation and cursor regression scenarios. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(complete => { resolve = complete; });
  return { promise, resolve };
}

function readerDocument(key: string, title: string): Awaited<ReturnType<typeof load>> {
  return { status: 'available', routeKey: key, title, namespace: 'wiki', tags: [], children: [], related: [], backlinks: [], blocks: [], nextBlockToken: null };
}

function treeRow(key: string): Awaited<ReturnType<typeof loadTree>>['roots'][number] {
  return { kind: 'document', label: key, locator: null, href: `/memory-reader/${key}`, linkWeight: 1, itemCount: null, children: [], nodeKey: key, parentKey: null, depth: 0 };
}

/** Uses two navigable cards and an optional continuation without depending on real storage. */
async function mountTwoCardCatalog(treeToken: string | null = null): Promise<void> {
  vi.stubGlobal('IntersectionObserver', TestObserver);
  vi.mocked(loadAreas).mockResolvedValue([]);
  vi.mocked(loadCatalog).mockResolvedValue({ status: 'available', documents: ['first', 'second'].map(key => ({ href: `/memory-reader/${key}`, title: key, type: 'Fact', namespace: 'wiki', preview: key, tags: [] })), namespaces: [], tags: [], nextToken: null });
  vi.mocked(loadTags).mockResolvedValue({ status: 'available', tags: [], nextToken: null });
  vi.mocked(loadTree).mockResolvedValue({ status: 'available', roots: [treeRow('first'), treeRow('second')], nextToken: treeToken });
  document.body.innerHTML = '<div id="memory-reader-root"></div>';
  await mountMemoryReaderPage('');
}
