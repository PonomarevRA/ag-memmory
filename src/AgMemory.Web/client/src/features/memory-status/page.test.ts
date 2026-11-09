import { describe, expect, it, vi } from 'vitest';

vi.mock('./api.js', () => ({ load: vi.fn() }));

import { load } from './api.js';
import { memoryStatusPage } from './page.js';

describe('memory status page', () => {
  it('renders the safe unavailable state', async () => {
    vi.mocked(load).mockResolvedValue({ status: 'unavailable' } as never);
    document.body.innerHTML = await memoryStatusPage();
    expect(document.body.textContent).toContain('Состояние памяти недоступно');
  });

  it('renders inject diagnostics without exposing memory text', async () => {
    vi.mocked(load).mockResolvedValue({
      status: 'available',
      totalMemoryCount: 4,
      activeMemoryCount: 3,
      expiredMemoryCount: 0,
      inactiveMemoryCount: 1,
      lastInjectHitCount: 2,
      readerAligned: true
    } as never);
    document.body.innerHTML = await memoryStatusPage();
    expect(document.body.textContent).toContain('В последнем запросе Yuki');
    expect(document.body.textContent).toContain('800');
    expect(document.body.textContent).toContain('Wiki читает то же локальное хранилище, что и чат.');
    expect(document.body.textContent).toContain('Страница не показывает текст записей, scope, actor или постоянные идентификаторы.');
    expect(document.body.textContent).not.toContain('local-development');
    expect(document.body.textContent).not.toContain('.lancedb');
  });
});
