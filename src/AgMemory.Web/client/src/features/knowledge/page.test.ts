import { describe, expect, it } from 'vitest';
import { safeResponse, unavailable } from './api.js';
import { mountKnowledgePage, renderKnowledge } from './page.js';
import { matchRoute } from '../../app/routes.js';

describe('knowledge catalogue', () => {
  it('escapes procedure content and keeps reported results distinct', () => {
    const payload = { ...unavailable(), status: 'available', sourceId: 'secret-source', profileKey: 'secret-profile',
      applications: { ReportedSuccess: 2, VerifiedSuccess: 1 }, feedback: { Helpful: 3 },
      procedures: [{ goal: '<img src=x onerror=alert(1)>', revision: 2, steps: '<script>alert(2)</script>',
        applicability: 'local', preconditions: 'ready', verification: 'check' }] };
    const projected = safeResponse(payload);
    expect(JSON.stringify(projected)).not.toContain('secret-source');
    expect(JSON.stringify(projected)).not.toContain('secret-profile');
    document.body.innerHTML = renderKnowledge(projected);
    expect(document.querySelector('script')).toBeNull();
    expect(document.querySelector('img')).toBeNull();
    expect(document.body.textContent).toContain('<script>alert(2)</script>');
    expect(document.body.textContent).toContain('Успех со слов агента: 2');
    expect(document.body.textContent).toContain('их независимость не подтверждена');
    expect(document.querySelector('button, form, input')).toBeNull();
  });

  it('bounds rows, allows only known counters and clamps malformed numbers', () => {
    const payload = { ...unavailable(), status: 'available', classifiedCount: -10, procedureCount: Infinity,
      procedures: Array.from({ length: 60 }, () => ({ goal: 'x'.repeat(2000), revision: 1 })),
      feedback: { Helpful: 2, secretSource: 99 }, reviewReasons: { unknown: 30 } };
    const state = safeResponse(payload);
    expect(state.procedures).toHaveLength(50);
    expect(state.procedures[0].goal).toHaveLength(1000);
    expect(state.feedback).toEqual({ Helpful: 2 });
    expect(state.reviewReasons).toEqual({});
    expect(state.classifiedCount).toBe(0);
    expect(state.procedureCount).toBe(0);
  });

  it('has explicit unavailable and unknown-route states', () => {
    expect(renderKnowledge(safeResponse(null))).toContain('Каталог знаний недоступен');
    expect(matchRoute('/knowledge').name).toBe('knowledge');
    expect(matchRoute('/knowledge/unknown').name).toBe('not-found');
  });

  it('filters current-source cards and distinguishes an unknown rating from zero', () => {
    const state = safeResponse({ ...unavailable(), status: 'available', sources: [
      { class: 'L11', preview: 'knowledge preview', sourceRevision: 1, windows: [{ days: 1, utility: null, ratingSampleSize: 0 }] },
      { class: 'L12', preview: 'procedure preview', sourceRevision: 2, windows: [{ days: 7, utility: 0, ratingSampleSize: 3 }] }
    ] });
    document.body.innerHTML = renderKnowledge(state, 'L11');
    expect(document.body.textContent).toContain('knowledge preview');
    const dispose = mountKnowledgePage(document.body);
    expect(document.querySelector('[data-knowledge-item="L12"]')?.hasAttribute('hidden')).toBe(true);
    dispose?.();
    expect(document.body.textContent).toContain('Полезность неизвестна');
    document.body.innerHTML = renderKnowledge(state, 'L12');
    expect(document.body.textContent).toContain('Полезность 0%');
    expect(document.body.textContent).toContain('Оценок 3');
  });
});


it('explains empty classifications separately from saved memory and names human-readable classes', () => {
  document.body.innerHTML = renderKnowledge({...unavailable(),status:'available'});
  expect(document.querySelector('.memory-layers summary')?.textContent).toContain('уровни памяти');
  expect(document.querySelector('a[href="/memory-reader"]')).not.toBeNull();
  expect(document.body.textContent).toContain('Ноль здесь не означает');
  const dispose = mountKnowledgePage(document.body);
  expect(document.body.textContent).toContain('Процедуры · L12');
  dispose?.();
  expect(document.body.textContent).toContain('L1 · Личный контекст агента');
});


it('unavailable knowledge offers a read-only retry preserving the class and a real status link',()=>{
  document.body.innerHTML=renderKnowledge(unavailable(),'L12');
  const retry=document.querySelector<HTMLAnchorElement>('a.ui-button')!;
  expect(retry.getAttribute('href')).toBe('/knowledge?class=L12');
  expect(document.querySelector('a[href="/memory-status"]')).not.toBeNull();
  expect(document.body.textContent).not.toContain('Нет доступных текущих записей');
  expect(document.querySelector('form')).toBeNull();
});
