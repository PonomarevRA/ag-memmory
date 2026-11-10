import { normalizedVisits, isStorageAvailable } from '../navigation/browser-state.js';
import { canonicalMemoryPath } from '../../app/routes.js';
import { escapeHtml } from '../../shared/html.js';

/** Resolves stored paths using the browser's URL rules before creating any link. */
function safeVisitPath(route: string): string | undefined {
  try {
    const { origin, pathname } = new URL(route, location.origin);
    if (origin === location.origin && !pathname.startsWith('//') && canonicalMemoryPath(pathname) === pathname) return pathname;
  } catch {
    // Malformed entries can be left in storage but must never become links.
  }
}

export function historyPage(): string {
  const rows: string[] = [];
  for (const item of normalizedVisits()) {
    const path = safeVisitPath(item.route);
    if (!path) continue;
    rows.push(`<li><a href="${escapeHtml(path)}">${escapeHtml(item.title)}</a> <small>${escapeHtml(item.visitedAt)}</small></li>`);
  }
  const storageNotice = isStorageAvailable() ? '' : '<p>Локальное хранилище недоступно.</p>';
  return `<section class="page-header"><p class="eyebrow">Локальная история</p><h1>История посещений</h1>${storageNotice}<ul>${rows.join('') || '<li>История пока пуста.</li>'}</ul></section>`;
}
