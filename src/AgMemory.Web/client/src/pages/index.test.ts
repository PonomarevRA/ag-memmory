import { describe, expect, it } from 'vitest';
import { renderPage } from './index.js';

const route = (name: string) => ({ name, path: `/${name}`, title: name, params: {} });

describe('informational pages', () => {
  it('explains the local and non-production boundaries on the about page', async () => {
    document.body.innerHTML = await renderPage(route('about'));

    expect(document.body.textContent).toContain('Локальная память для агентов');
    expect(document.body.textContent).toContain('не является моделью производственной авторизации');
    expect(document.body.textContent).toContain('безопасные проекции');
    expect(document.body.textContent).toContain('Wiki может показать безопасную проекцию записи');
  });

  it('documents the process-fixed MCP flow without exposing configuration', async () => {
    document.body.innerHTML = await renderPage(route('connect'));

    expect(document.body.textContent).toContain('memory_status');
    expect(document.body.textContent).toContain('memory_remember');
    expect(document.body.textContent).toContain('memory_recall');
    expect(document.body.textContent).toContain('конфигурацию процесса MCP');
    expect(document.body.textContent).toContain('отдельное происхождение');
    expect(document.body.textContent).toContain('Codex, Cursor или Claude');
    expect(document.body.textContent).toContain('AGMEMORY_STORAGE_PATH=YOUR_LOCAL_STORAGE_PATH');
    expect(document.body.textContent).toContain('AGMEMORY_CLIENT_ID=YOUR_CLIENT_ID');
    expect(document.querySelectorAll('ol li')).toHaveLength(3);
    expect(document.body.textContent).not.toContain('local-development');
    expect(document.body.textContent).not.toContain('.lancedb');
    expect(document.body.textContent).not.toContain('cursor-local');
    expect(document.body.textContent).not.toContain('127.0.0.1');
    expect(document.body.textContent).not.toContain('/absolute/path');
  });
});
