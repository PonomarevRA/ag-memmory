import { startRouter } from './app/router.js';
import { applyPreferences, getPreferences } from './features/navigation/browser-state.js';
applyPreferences(getPreferences());
startRouter(document.querySelector<HTMLElement>('#app')!);
