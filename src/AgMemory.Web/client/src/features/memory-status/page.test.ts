import { describe, expect, it, vi } from 'vitest';
vi.mock('./api.js', () => ({ load: vi.fn().mockResolvedValue({ status: 'unavailable' }) }));
import { memoryStatusPage } from './page.js';
describe('memory status page', () => { it('renders the safe unavailable state', async () => { document.body.innerHTML = await memoryStatusPage(); expect(document.body.textContent).toContain('Состояние памяти недоступно'); }); });
