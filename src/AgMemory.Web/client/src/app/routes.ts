export type Route = { name: string; path: string; title: string; params: Record<string, string> };

const known = [
  ['home', /^\/$/, 'AgMemory'],
  ['status', /^\/memory-status$/, 'Состояние памяти · AgMemory'], ['reader', /^\/memory-reader(?:\/([^/]+))?$/, 'Читать память · AgMemory'],
  ['graph', /^\/memory-graph$/, 'Граф памяти · AgMemory'], ['connect', /^\/connect$/, 'Подключение · AgMemory'],
  ['about', /^\/about$/, 'О проекте · AgMemory'], ['history', /^\/history$/, 'История · AgMemory'],
  ['settings', /^\/settings$/, 'Настройки · AgMemory'],
  ['knowledge', /^\/knowledge$/, 'Знания и процедуры · AgMemory']
] as const;

/** Opens memory from retired chat bookmarks without carrying over chat parameters. */
export function canonicalMemoryPath(path: string): string {
  return /^\/chat(?:[/?#]|$)/i.test(path) ? '/memory-reader' : path;
}

export function matchRoute(pathname = location.pathname): Route {
  for (const [name, expression, title] of known) {
    const result = expression.exec(pathname);
    if (result) return { name, path: pathname, title, params: { id: result[1] ?? '' } };
  }
  return { name: 'not-found', path: pathname, title: 'Страница не найдена · AgMemory', params: {} };
}
