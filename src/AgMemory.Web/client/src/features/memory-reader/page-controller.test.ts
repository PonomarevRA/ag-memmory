import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('./api.js', () => ({ load: vi.fn(), loadCatalog: vi.fn(), loadTags: vi.fn(), loadTree: vi.fn() }));

import { loadCatalog, loadTags, loadTree } from './api.js';
import { dispose, mountMemoryReaderPage } from './page-controller.js';

class TestObserver {
  constructor(_: IntersectionObserverCallback) {}
  observe() {}
  disconnect() {}
}

describe('memory reader page controller', () => {
  afterEach(() => { dispose(); document.body.innerHTML = ''; vi.clearAllMocks(); });

  it('resets the independent tag stream on search and fetches its next page from the network', async () => {
    vi.stubGlobal('IntersectionObserver', TestObserver);
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
    expect(loadCatalog).toHaveBeenLastCalledWith(null, null, null, 'новый запрос');
    expect(document.body.textContent).toContain('После поиска');
    expect(document.body.textContent).not.toContain('Первый');

    document.querySelector<HTMLButtonElement>('#reader-more-tags')!.click();
    await vi.waitFor(() => expect(loadTags).toHaveBeenCalledWith('tags-3'));
    expect(document.body.textContent).toContain('Следующая страница');
  });
});
