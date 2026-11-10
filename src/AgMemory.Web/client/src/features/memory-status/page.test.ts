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

  it('renders storage diagnostics without exposing memory text', async () => {
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
    expect(document.body.textContent).not.toContain('Yuki');
    expect(document.body.textContent).toContain('Обозреватель и граф используют одну область памяти.');
    expect(document.body.textContent).toContain('Страница не показывает текст записей, scope, actor или постоянные идентификаторы.');
    expect(document.body.textContent).not.toContain('local-development');
    expect(document.body.textContent).not.toContain('.lancedb');
  });
});
