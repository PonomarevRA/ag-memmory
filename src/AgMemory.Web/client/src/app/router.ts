import { matchRoute, type Route } from './routes.js';
import { shell } from './shell.js';
import { renderPage } from '../pages/index.js';
import { mountMemoryGraphPage } from '../features/memory-graph/page.js';
import { dispose as disposeMemoryGraph } from '../features/memory-graph/page-controller.js';
import { dispose as disposeChat } from '../features/chat/chat-stream.js';
import { trackVisit } from '../features/navigation/index.js';

let app: HTMLElement;
export function startRouter(target: HTMLElement): void {
  app = target;
  window.addEventListener('popstate', () => render());
  document.addEventListener('click', event => {
    const link = (event.target as Element).closest<HTMLAnchorElement>('a[href]');
    if (!link || link.target || event.metaKey || link.origin !== location.origin || link.href.includes('#')) return;
    event.preventDefault(); navigate(link.pathname + link.search);
  });
  render();
}
export function navigate(path: string): void { history.pushState({}, '', path); render(); }
export async function render(): Promise<void> {
  // Render replaces the route subtree. Release canvas and stream resources first.
  disposeMemoryGraph();
  disposeChat();
  const route = matchRoute(); document.title = route.title;
  app.innerHTML = shell(await renderPage(route), route.path);
  if (route.name === 'graph') mountMemoryGraphPage();
  app.querySelector<HTMLElement>('main')?.focus();
  trackVisit(route.path, route.title);
}
