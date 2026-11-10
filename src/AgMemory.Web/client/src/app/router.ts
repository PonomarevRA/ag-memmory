import {pushRoute} from './route-history.js';
import { beginRouteLoading, endRouteLoading, enhancePage, showRouteFailure } from './design-system.js';
import { mountSettingsPage } from '../features/settings/page.js';
import { canonicalMemoryPath, matchRoute } from './routes.js';
import { shell } from './shell.js';
import { renderPage } from '../pages/index.js';
import { mountKnowledgePage } from '../features/knowledge/page.js';
import { dispose as disposeMemoryReader, mountMemoryReaderPage } from '../features/memory-reader/page-controller.js';
import { beginNavigation, trackVisit } from '../features/navigation/index.js';
import { loadAppVersion } from '../features/app-version/api.js';

let app: HTMLElement;
let disposeKnowledge: (() => void) | undefined;
let renderEpoch = 0;
let disposeSettings: (() => void) | undefined;
let disposePageDesign: (() => void) | undefined;
let graphPage: typeof import('../features/memory-graph/page.js') | undefined;
/** Installs same-origin navigation while preserving browser gestures and native links. */
export function startRouter(target: HTMLElement): void {
  app = target;
  window.addEventListener('popstate', () => render());
  document.addEventListener('click', event => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    const link = event.target instanceof Element ? event.target.closest<HTMLAnchorElement>('a[href]') : null;
    if (!link || link.target || link.hasAttribute('download') || link.origin !== location.origin || link.href.includes('#')) return;
    event.preventDefault(); navigate(link.pathname + link.search);
  });
  render();
}
/** Start a new navigation at the page heading; history restoration remains owned by popstate rendering. */
export function navigate(path: string): void {
  const destination = canonicalMemoryPath(path);
  beginNavigation(destination);
  pushRoute(destination);
  window.scrollTo({left:0, top:0, behavior:'instant'});
  void render();
}
/** Renders only the latest route so an older asynchronous response cannot replace it. */
export async function render(): Promise<void> {
  const epoch = ++renderEpoch;
  // Release controllers before replacing the route subtree.
  graphPage?.disposeMemoryGraph();
  disposeKnowledge?.();
  disposeKnowledge = undefined;
  disposeMemoryReader();
  disposeSettings?.();
  disposeSettings = undefined;
  const canonical = canonicalMemoryPath(location.pathname);
  if (canonical !== location.pathname) history.replaceState(history.state, '', canonical);
  const route = matchRoute(); document.title = route.title;
  beginRouteLoading(app);
  try {
    const version = await loadAppVersion();
    if (epoch !== renderEpoch) return;
    const markup = await renderPage(route);
    if (epoch !== renderEpoch) return;
    // Text routes keep the graph renderer lazy; a late import still cannot replace a newer route.
    if (route.name === 'graph') {
      const loadedGraphPage = await import('../features/memory-graph/page.js');
      if (epoch !== renderEpoch) return;
      graphPage = loadedGraphPage;
    }
    disposePageDesign?.();
    app.innerHTML = shell(markup, route.path, version);
    disposePageDesign = enhancePage(app);
    if (route.name === 'graph') graphPage?.mountMemoryGraphPage();
    if (route.name === 'knowledge') disposeKnowledge = mountKnowledgePage(app);
    if (route.name === 'settings') disposeSettings = mountSettingsPage(app);
    if (route.name === 'reader') void mountMemoryReaderPage(route.params.id);
    app.querySelector<HTMLElement>('main')?.focus({preventScroll:true});
    trackVisit(route.path, route.title);
  } catch {
    if (epoch !== renderEpoch) return;
    disposePageDesign?.();
    app.innerHTML = shell('', route.path);
    showRouteFailure(app, () => { void render(); });
    disposePageDesign = enhancePage(app);
    app.querySelector<HTMLElement>('main')?.focus({preventScroll:true});
  } finally {
    // An older pending request must not clear the busy state of the newest route.
    if (epoch === renderEpoch) endRouteLoading(app);
  }
}
