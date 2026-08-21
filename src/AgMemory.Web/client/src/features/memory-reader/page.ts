export function memoryReaderPage(id: string): string {
  return `<section class="memory-reader-page" data-memory-reader-route="${encodeURIComponent(id)}"><header class="page-header"><p class="eyebrow">Wiki</p><h1>${id ? 'Загрузка страницы…' : 'Читать память'}</h1></header><div id="memory-reader-root" class="memory-reader-wiki" aria-live="polite"><p class="muted">Загрузка…</p></div></section>`;
}
