import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./api.js', () => ({
  load: vi.fn(),
  loadCatalog: vi.fn(),
  loadTree: vi.fn()
}));

import { load, loadCatalog, loadTree } from './api.js';
import { memoryReaderPage } from './page.js';

describe('memory reader page', () => {
  beforeEach(() => {
    vi.mocked(load).mockReset();
    vi.mocked(loadCatalog).mockReset();
    vi.mocked(loadTree).mockReset();
  });
  it('renders a document with relations and does not fetch the tree', async () => {
    vi.mocked(load).mockResolvedValue({
      status: 'available',
      title: 'Card',
      namespace: 'project',
      children: [{ href: '/memory-reader/child', kind: 'child', label: 'Child', title: 'Child page', namespace: 'project' }],
      related: [],
      backlinks: [],
      blocks: [{ heading: 'Part', content: [{ text: 'Safe text' }] }]
    } as never);
    document.body.innerHTML = await memoryReaderPage('card');
    expect(loadTree).not.toHaveBeenCalled();
    expect(loadCatalog).not.toHaveBeenCalled();
    expect(document.body.textContent).toContain('Safe text');
    expect(document.body.textContent).toContain('Дочерние страницы');
    expect(document.querySelector('.memory-reader-relations a')?.getAttribute('href')).toBe('/memory-reader/child');
    expect(document.querySelector('.memory-reader-tree')).toBeNull();
  });

  it('renders the catalog without fetching the tree', async () => {
    vi.mocked(loadCatalog).mockResolvedValue({
      status: 'available',
      documents: [{ href: '/memory-reader/card', title: 'Card', preview: 'Preview' }]
    } as never);
    document.body.innerHTML = await memoryReaderPage('');
    expect(loadTree).not.toHaveBeenCalled();
    expect(load).not.toHaveBeenCalled();
    expect(document.querySelector('.memory-reader-catalog a')?.getAttribute('href')).toBe('/memory-reader/card');
    expect(document.querySelector('.memory-reader-tree')).toBeNull();
  });
});
