import { loadCatalog, load } from './api.js';
import type { MemoryReaderDocument, MemoryReaderRelation } from './sanitize.js';

export async function memoryReaderPage(id: string): Promise<string> {
  if (id) {
    const content = await load(id);
    if ('blocks' in content && content.status === 'available') return renderDocument(content);
  }
  const catalog = await loadCatalog();
  const items = catalog.status === 'available' && 'documents' in catalog
    ? catalog.documents.map(doc => `<article class="memory-reader-catalog__item"><h2><a href="${doc.href}">${doc.title}</a></h2><p>${doc.preview}</p></article>`).join('')
    : '<p>Память недоступна или пока пуста.</p>';
  return `<section class="memory-reader-page"><header class="page-header"><p class="eyebrow">Wiki</p><h1>Читать память</h1></header><div class="memory-reader-main"><section class="memory-reader-catalog">${items}</section></div></section>`;
}

function renderDocument(content: MemoryReaderDocument): string {
  const blocks = content.blocks.map(block => `<section class="memory-reader-block"><h2>${block.heading ?? ''}</h2><p class="memory-reader-block__content">${block.content.map(run => run.text).join('')}</p></section>`).join('');
  return `<section class="memory-reader-page"><article class="memory-reader-document"><header><p class="eyebrow">${content.namespace}</p><h1>${content.title}</h1></header>${blocks}${renderRelations('Дочерние страницы', content.children)}${renderRelations('Связанные страницы', content.related)}${renderRelations('Обратные ссылки', content.backlinks)}</article></section>`;
}

function renderRelations(heading: string, relations: MemoryReaderRelation[]): string {
  if (relations.length === 0) return '';
  const items = relations.map(relation => `<li><a href="${relation.href}">${relation.title}</a></li>`).join('');
  return `<nav class="memory-reader-relations"><h2>${heading}</h2><ul>${items}</ul></nav>`;
}
