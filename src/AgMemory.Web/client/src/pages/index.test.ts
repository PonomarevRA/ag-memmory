import { describe, expect, it } from 'vitest';
import { renderPage } from './index.js';

const route = (name: string) => ({ name, path: `/${name}`, title: name, params: {} });

describe('informational pages', () => {
  it('explains the local and non-production boundaries on the about page', async () => {
    document.body.innerHTML = await renderPage(route('about'));

    expect(document.body.textContent).toContain('Локальная память для агентов');
    expect(document.body.textContent).toContain('не является моделью производственной авторизации');
    expect(document.body.textContent).toContain('безопасные проекции');
    expect(document.body.textContent).toContain('Обозреватель, граф и каталог знаний');
  });

  it('documents the process-fixed MCP flow without exposing configuration', async () => {
    document.body.innerHTML = await renderPage(route('connect'));

    expect(document.body.textContent).toContain('memory_capabilities');
    expect(document.body.textContent).toContain('memory_skill');
    expect(document.body.textContent).toContain('L2');
    expect(document.body.textContent).toContain('Codex и Cursor');
    expect(document.body.textContent).toContain('задаёт оператор');
    expect(document.querySelector('a[download]')?.getAttribute('href')).toBe('/api/agent-setup/skill.zip');
    expect(document.querySelector('a[href="/agent-setup.md"]')?.getAttribute('target')).toBe('_blank');
    expect(document.querySelector('a[href="/api/agent-setup"]')).not.toBeNull();
    expect(document.querySelectorAll('ol li')).toHaveLength(4);
    expect(document.body.textContent).not.toContain('local-development');
    expect(document.body.textContent).not.toContain('.lancedb');
    expect(document.body.textContent).not.toContain('cursor-local');
    expect(document.body.textContent).not.toContain('127.0.0.1');
    expect(document.body.textContent).not.toContain('/absolute/path');
  });
});


it('home starts with reading real memory and exposes three clearly named destinations',async()=>{
  document.body.innerHTML=await renderPage(route('home'));
  expect(document.querySelector('.hero .ui-button')?.getAttribute('href')).toBe('/memory-reader');
  expect(document.querySelector('.feature-grid')?.closest('section')?.getAttribute('aria-labelledby')).toBe('workspace-heading');
  expect(document.querySelectorAll('.home-features .feature-card')).toHaveLength(3);
  expect(document.querySelector('.feature-card a[href="/knowledge"]')).not.toBeNull();
});
