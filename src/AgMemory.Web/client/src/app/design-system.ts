import { bindNavigation, createButton, createEmptyState, createNotice, createStatus } from '@ag-design/components';

/** Adapts existing controls under root without replacing nodes or their handlers.
 * Returns cleanup for shared navigation keys; the router still owns selection and activation.
 */
export function enhancePage(root: HTMLElement): () => void {
  root.classList.add('ag-app');
  for (const control of root.querySelectorAll<HTMLElement>('.ui-button, .ui-icon-button')) {
    // Preserve semantic variants: a dangerous legacy action must never become a primary action.
    let variant = 'primary';
    if (control.classList.contains('ui-button--danger')) variant = 'danger';
    else if (control.classList.contains('ui-button--secondary')) variant = 'secondary';
    else if (control.matches('.ui-icon-button, .ui-button--ghost')) variant = 'quiet';
    control.classList.add('ag-button', `ag-button--${variant}`);
    if (control.classList.contains('ui-icon-button')) control.classList.add('ag-icon-button');
  }
  for (const input of root.querySelectorAll<HTMLElement>('.ui-input, .ui-select, textarea, select, input[type="text"], input[type="search"], input[type="number"], input[type="url"], input[type="password"]')) {
    input.classList.add('ag-input');
  }
  for (const empty of root.querySelectorAll('.empty-state')) empty.classList.add('ag-empty');
  const nav = root.querySelector<HTMLElement>('#primary-navigation');
  if (!nav) return () => {};
  nav.classList.add('ag-nav');
  for (const link of nav.querySelectorAll('a[href]')) link.classList.add('ag-button', 'ag-button--quiet');
  return bindNavigation(nav, { selector: 'a[href]' });
}

/** Announces a pending route and temporarily blocks stale content while leaving navigation available. */
export function beginRouteLoading(root: HTMLElement): void {
  root.classList.add('ag-app');
  root.setAttribute('aria-busy', 'true');
  root.querySelector('main')?.setAttribute('inert', '');
  root.querySelector('[data-route-loading]')?.remove();
  const status = createStatus({ document: root.ownerDocument, label: 'Загрузка страницы…' });
  status.dataset.routeLoading = '';
  status.classList.add('ag-route-loading');
  status.setAttribute('role', 'status');
  status.setAttribute('aria-live', 'polite');
  root.append(status);
}

/** Releases only the current route's busy state after success or a rendered failure. */
export function endRouteLoading(root: HTMLElement): void {
  root.removeAttribute('aria-busy');
  root.querySelector('main')?.removeAttribute('inert');
  root.querySelector('[data-route-loading]')?.remove();
}

/** Renders a safe retryable failure without exposing exceptions, service paths or untrusted HTML. */
export function showRouteFailure(root: HTMLElement, retry: () => void): void {
  const document = root.ownerDocument;
  const action = createButton({ document, label: 'Повторить', onClick: retry });
  const empty = createEmptyState({ document, title: 'Не удалось открыть страницу', action });
  const notice = createNotice({ document, text: 'Попробуйте ещё раз. Остальные разделы доступны через навигацию.', tone: 'danger' });
  empty.insertBefore(notice, action);
  root.querySelector('main')?.replaceChildren(empty);
}
