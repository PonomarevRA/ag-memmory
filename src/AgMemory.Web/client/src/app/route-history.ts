/** Track only SPA entries created in this window; native/browser buttons never infer an unknown history. */
const session = Math.random().toString(36).slice(2);
let position = 0, maximum = 0;
function mark(): void { history.replaceState({...history.state, agNavigation:{session,position}}, ''); }
mark();
window.addEventListener('popstate', () => {
  const value = history.state?.agNavigation;
  if (value?.session === session && Number.isInteger(value.position)) position = value.position;
  else { position = 0; maximum = 0; mark(); }
});
/** Preserve other history state while truncating the forward branch after a new navigation. */
export function pushRoute(path: string): void {
  if (path === location.pathname + location.search) return;
  position++; maximum = position;
  history.pushState({agNavigation:{session,position}}, '', path);
}
export function routeHistory(): {back:boolean; forward:boolean} { return {back:position > 0,forward:position < maximum}; }
