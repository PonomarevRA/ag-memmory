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
});
