import { createCatalogSearchControls, type CatalogSearchRequest } from '@ag-design/components';
import { createI18n } from '@ag-design/components/i18n';
import { escapeHtml } from '../../shared/html.js';
import { load, type Knowledge, type Procedure, type Source } from './api.js';

const classLabels: Record<string, string> = {L11:'Знания · L11',L12:'Процедуры · L12',L13:'Опыт · L13'};

const labels: Record<string, string> = { Helpful: 'Полезно', Harmful: 'Вредно', Irrelevant: 'Не относится к задаче', Outdated: 'Устарело',
  Incorrect: 'Ошибка в содержании', Applied: 'Агент сообщил о применении', ReportedSuccess: 'Успех со слов агента', VerifiedSuccess: 'Успех с зафиксированным подтверждением',
  Partial: 'Частичный результат', Failed: 'Неудача', NotApplicable: 'Не применимо', Unknown: 'Результат неизвестен',
  reported_content_issue: 'Сообщения об ошибке или устаревании', negative_agent_feedback_review_only: 'Негативные оценки агентов: нужна проверка' };

/** Presents the actionable steps first and preserves the stored line breaks. */
function renderProcedure(item: Procedure): string {
  return `<article class="ui-card" data-knowledge-item="L12" data-ag-width="fill">
    <details class="knowledge-procedure">
      <summary><strong>${escapeHtml(item.goal)}</strong><span class="muted">Версия ${item.revision}</span></summary>
      <div data-ag-width="content">
        <p>${escapeHtml(item.applicability)}</p>
        <h3>Предусловия</h3><p class="procedure-text">${escapeHtml(item.preconditions)}</p>
        <h3>Порядок действий</h3><p class="procedure-text">${escapeHtml(item.steps)}</p>
        <h3>Проверка результата</h3><p class="procedure-text">${escapeHtml(item.verification)}</p>
      </div>
    </details>
  </article>`;
}

/** Separates the source preview from observations about its reuse. */
function renderSource(item: Source): string {
  const windows = item.windows.map(window => {
    const utility = window.utility === null ? 'неизвестна' : `${Math.round(window.utility * 100)}%`;
    return `<li>${window.days} дн. · показано ${window.presented}, применено со слов агента ${window.applied}.
      Оценок ${window.ratingSampleSize}, меток задач ${window.selfReportedTasks}. Полезность ${utility}.</li>`;
  }).join('') || '<li>Пока нет наблюдений.</li>';
  return `<article class="ui-card" data-knowledge-item="${escapeHtml(item.class)}" data-ag-width="fill">
    <h3>${escapeHtml(classLabels[item.class] ?? item.class)} · версия ${item.sourceRevision}</h3>
    <p data-ag-width="content">${escapeHtml(item.preview)}</p>
    <details data-knowledge-observations><summary>Использование и оценки</summary>
      <p>${item.warmExcluded ? 'Исключено из тёплого набора' : 'Допускается в тёплый набор'}</p>
      <ul>${windows}</ul>
    </details>
  </article>`;
}

/** Renders safe text and independent observations without turning agent reports into authoritative verification. */
export function renderKnowledge(state: Knowledge, selectedClass = 'All'): string {
  const header = '<header class="page-header"><p class="eyebrow">Проверяемый опыт</p><h1>Знания и процедуры</h1></header>';
  if (state.status !== 'available') return `<section>${header}<div class="status-banner status-banner--warning"><h2>Каталог знаний недоступен</h2><p>Проверьте соединение с локальным сервисом. <a href="/memory-status">Состояние памяти</a> поможет уточнить причину.</p><a class="ui-button" href="/knowledge?class=${encodeURIComponent(selectedClass)}">Повторить загрузку</a></div></section>`;
  const metric = (label: string, count: number) => `<article class="memory-status-metric" data-ag-width="content" data-ag-width-min-em="4"><p>${label}</p><strong>${count}</strong></article>`;
  const counters = (values: Record<string, number>) => Object.entries(values).map(([key, count]) => `<li>${escapeHtml(labels[key] ?? key)}: ${count}</li>`).join('') || '<li>Пока нет наблюдений.</li>';
  const procedures = state.procedures.map(item => `<div class="knowledge-card-slot">${renderProcedure(item)}</div>`).join('');
  const sources = state.sources.map(item => `<div class="knowledge-card-slot">${renderSource(item)}</div>`).join('');
  return `<section class="knowledge-page" data-ag-width="fill">${header}<p data-ag-width="content">Поиск работает по загруженным карточкам. Здесь до 50 последних процедур и до 50 блоков памяти. Для отдельного поиска по записям откройте <a href="/memory-reader">обозреватель памяти</a>.</p>
    <section class="knowledge-catalog" aria-label="Каталог знаний" data-knowledge-catalog data-initial-class="${escapeHtml(selectedClass)}" data-ag-width="fill">
      <div data-knowledge-controls data-ag-width="fill"></div>
      <p data-knowledge-empty hidden>Среди загруженных карточек совпадений нет. Измените запрос или сбросьте фильтр.</p>
      <section data-knowledge-group data-ag-width="fill"><h2>Каталог процедур</h2><div class="feature-grid">${procedures}</div></section>
      <section data-knowledge-group data-ag-width="fill"><h2>Текущие блоки памяти</h2><div class="feature-grid">${sources}</div></section>
    </section>
    <details class="knowledge-observations"><summary>Состояние знаний и наблюдения</summary>
      <div class="memory-status-grid">${metric('Классифицировано', state.classifiedCount)}${metric('Процедуры', state.procedureCount)}${metric('Случаи проблем', state.occurrenceCount)}${metric('Предъявления контекста', state.presentationCount)}${metric('Записи тёплого набора (TTL)', state.activeWarmCount)}</div>
      <p>Рабочий контекст: ${state.workingSetActive ? 'активен' : 'отсутствует или истёк'}.</p>
      <p>Оценки и метки задач сообщают агенты; их независимость не подтверждена.</p>
      <section class="feature-grid"><article class="ui-card" data-ag-width="content"><h2>Оценки полезности</h2><ul>${counters(state.feedback)}</ul></article><article class="ui-card" data-ag-width="content"><h2>Результаты применения</h2><ul>${counters(state.applications)}</ul></article><article class="ui-card" data-ag-width="content"><h2>Очередь проверки</h2><ul>${counters(state.reviewReasons)}</ul></article></section>
    </details>
    <details class="memory-layers"><summary>Что означают уровни памяти?</summary><dl><dt>L1 · Личный контекст агента</dt><dd>Хранится у клиента.</dd><dt>L2 · Рабочий контекст</dt><dd>Ограниченный по времени контекст задачи.</dd><dt>L3 · Тёплый набор</dt><dd>Актуальные записи с TTL.</dd><dt>L10 · Сохранённые записи</dt><dd>Основное хранилище.</dd><dt>L11 / L12 / L13</dt><dd>Знания, процедуры и опыт. Ноль здесь не означает, что основная память пуста.</dd></dl></details></section>`;
}

/** Fetches the local read-only catalogue for the current route. */
export async function knowledgePage(): Promise<string> {
  const requested = new URLSearchParams(location.search).get('class') ?? 'All';
  const selectedClass = ['L11', 'L12', 'L13'].includes(requested) ? requested : 'All';
  return renderKnowledge(await load(), selectedClass);
}

/** Mounts the shared local search control over the current bounded catalogue and releases its listeners on navigation. */
export function mountKnowledgePage(root: HTMLElement): (() => void) | undefined {
  const catalog = root.querySelector<HTMLElement>('[data-knowledge-catalog]');
  const host = catalog?.querySelector<HTMLElement>('[data-knowledge-controls]');
  if (!catalog || !host) return undefined;
  const groups = Array.from(catalog.querySelectorAll<HTMLElement>('[data-knowledge-group]')).map(element => ({
    element,
    entries: Array.from(element.querySelectorAll<HTMLElement>('[data-knowledge-item]')).map(card => ({
      card, searchText: knowledgeSearchText(card),
    })),
  }));
  const empty = catalog.querySelector<HTMLElement>('[data-knowledge-empty]');
  const initial = catalog.dataset.initialClass ?? 'All';
  const controls = createCatalogSearchControls({
    document: host.ownerDocument, i18n: createI18n({ locale: 'ru' }),
    label: 'Поиск по загруженным карточкам', mode: 'instant',
    facets: [{ id: 'class', label: 'Класс', options: Object.entries(classLabels).map(([value, label]) => ({ value, label })) }],
    filters: initial in classLabels ? { class: [initial] } : {},
    onChange: applySearch,
  });
  controls.element.dataset.agWidth = 'fill';
  host.replaceChildren(controls.element);
  applySearch({ ...controls.state.applied, revision: controls.state.revision });
  return () => controls.dispose();

  /** Filters only the loaded catalogue. The host keeps ranking, data and URL ownership. */
  function applySearch(request: CatalogSearchRequest): void {
    const terms = request.query.trim().toLocaleLowerCase('ru').split(/\s+/).filter(Boolean);
    const classes = request.filters.class ?? [];
    let total = 0;
    for (const group of groups) {
      let visible = 0;
      for (const { card, searchText } of group.entries) {
        const matches = (!classes.length || classes.includes(card.dataset.knowledgeItem ?? ''))
          && terms.every(term => searchText.includes(term));
        card.hidden = !matches;
        card.closest<HTMLElement>('.knowledge-card-slot')?.toggleAttribute('hidden', !matches);
        if (matches) visible++;
      }
      group.element.hidden = visible === 0;
      total += visible;
    }
    if (empty) empty.hidden = total !== 0;
    controls.report({ revision: request.revision, status: 'ready', total });
  }
}


/** Indexes the card's text without expanding or searching reuse diagnostics. */
function knowledgeSearchText(card: HTMLElement): string {
  const content = card.cloneNode(true) as HTMLElement;
  content.querySelector('[data-knowledge-observations]')?.remove();
  return (content.textContent ?? '').toLocaleLowerCase('ru');
}
