import { escapeHtml } from '../../shared/html.js';
import { ActivityStream } from './stream.js';
import { readPreferences, savePreferences, reduceEvents, type ActivityEvent } from './model.js';

type PositionRequest = { ids: string[]; positions?: Array<{ id: string; x: number; y: number }> };

/** Mounts one app-wide corner panel. Its independent consumers are both disabled on a fresh profile. */
export function mountAgentActivity(): () => void {
  const preferences = readPreferences();
  const host = document.createElement('aside'); host.className = 'agent-activity'; host.setAttribute('aria-label', 'Активность агентов');
  host.innerHTML = `<section class="agent-activity__panel" hidden><header><span class="agent-activity__window-controls"><button type="button" data-action="hide" aria-label="Закрыть журнал" title="Закрыть">×</button><button type="button" data-action="minimize" aria-label="Свернуть журнал" title="Свернуть">−</button><button type="button" data-action="expand" aria-label="Расширить журнал" title="Расширить">↗</button></span><strong>Агенты</strong><button type="button" data-action="filters" aria-controls="agent-activity-details" aria-expanded="false" aria-label="Поиск и фильтры" title="Поиск и фильтры">⌕</button></header><p class="agent-activity__state" role="status"></p><div id="agent-activity-details" class="agent-activity__details" hidden><p class="small-copy">Наблюдаемые MCP операции. Поисковые запросы показаны ограниченно; тела памяти не сохраняются. Имя обозначает сессию, а не подтверждённого субагента.</p><form class="agent-activity__filters"><input name="q" maxlength="128" placeholder="Поиск операции" aria-label="Поиск по операциям"><select name="session" aria-label="Фильтр по сессии"><option value="">Все сессии</option></select><button type="submit">Найти</button></form></div><div class="agent-activity__rows" tabindex="0" aria-label="Последние операции"></div><footer><button type="button" data-action="older">Ранее</button><button type="button" data-action="follow">К новым</button><button type="button" data-action="clear">Очистить</button></footer></section><div class="agent-activity__launcher"><button type="button" data-action="restore" hidden aria-label="Открыть свёрнутый журнал">Журнал ↑</button><label data-launcher="chat" title="Журнал агентов"><input type="checkbox" data-setting="chat" aria-label="Журнал агентов"><span aria-hidden="true">☷</span></label><label title="Показывать работу агентов на карте"><input type="checkbox" data-setting="markers" aria-label="Показывать работу агентов на карте"><span class="agent-activity__blue-dot" aria-hidden="true"></span></label></div>`;
  document.body.append(host);
  const panel = host.querySelector<HTMLElement>('.agent-activity__panel')!;
  const rows = host.querySelector<HTMLElement>('.agent-activity__rows')!;
  const status = host.querySelector<HTMLElement>('.agent-activity__state')!;
  const restore = host.querySelector<HTMLButtonElement>('[data-action="restore"]')!;
  const markers = document.createElement('div'); markers.className = 'agent-activity-markers'; markers.setAttribute('aria-hidden', 'true'); document.body.append(markers);
  const chatToggle = host.querySelector<HTMLInputElement>('[data-setting="chat"]')!;
  const markToggle = host.querySelector<HTMLInputElement>('[data-setting="markers"]')!;
  chatToggle.checked = preferences.chatEnabled; markToggle.checked = preferences.markersEnabled;
  let minimized = false; let follow = true; let historyMode = false; let history: ActivityEvent[] = []; let current: ActivityEvent[] = []; let query = ''; let session = '';
  let markerTimer: ReturnType<typeof setInterval> | undefined; let requestVersion = 0; let disposed = false;
  const stream = new ActivityStream((events, state, reset) => { if (reset) { requestVersion++; history = []; historyMode = false; } current = events; status.textContent = historyMode ? 'История · новые события доступны по кнопке «К новым»' : state; renderRows(); updateMarkers(); });

  /** Renders a capped list without stealing scroll or focus while old entries are being read. */
  function renderRows(): void {
    const events = historyMode ? history : current;
    const sessionSelect = host.querySelector<HTMLSelectElement>('select[name="session"]')!;
    const sessions = [...new Set([...history, ...current].map(event => event.sessionId))].slice(-64);
    if (session && !sessions.includes(session)) sessions.unshift(session);
    sessionSelect.innerHTML = '<option value="">Все сессии</option>' + sessions.map(id => `<option value="${escapeHtml(id)}">${escapeHtml(id.slice(0, 8))}</option>`).join('');
    sessionSelect.value = session;
    const filtered = events.filter(event => (!query || event.operation.toLowerCase().includes(query.toLowerCase()) || event.query?.preview?.toLowerCase().includes(query.toLowerCase())) && (!session || event.sessionId === session));
    const scroll = rows.scrollTop;
    const pairs = new Map<string, { first: ActivityEvent; last: ActivityEvent }>();
    for (const event of filtered) { const pair = pairs.get(event.operationId); if (pair) pair.last = event; else pairs.set(event.operationId, { first: event, last: event }); }
    rows.innerHTML = [...pairs.values()].slice(-100).map(({ first, last: event }) => {
      const time = Number.isFinite(Date.parse(event.occurredAt)) ? new Date(event.occurredAt).toLocaleTimeString() : '';
      const target = event.target?.status === 'available' ? ' · объект карты' : ' · положение недоступно';
      const detail = event.phase === 'started' ? 'начато' : event.outcome === 'succeeded' ? 'готово' : event.outcome === 'cancelled' ? 'отменено' : event.outcome === 'failed' ? 'ошибка' : 'статус неизвестен';
      const count = event.resultCount === undefined ? '' : ` · ${event.resultCount} результатов`;
      return `<article><small>${escapeHtml(time)} · ${escapeHtml(event.clientKind)} · сессия ${escapeHtml(event.sessionId.slice(0, 8))}</small><p>${escapeHtml(event.operation)} — ${detail}${count}${target}</p><p><b>Запрос:</b> ${escapeHtml(first.request?.summary ?? event.request?.summary ?? event.operation)}${event.query?.preview || first.query?.preview ? ` · «${escapeHtml(event.query?.preview ?? first.query?.preview ?? '')}»${event.query?.truncated || first.query?.truncated ? ' (сокращено)' : ''}` : ' · текст запроса не сохраняется'}</p><p><b>Ответ:</b> ${escapeHtml(event.response?.summary ?? (event.phase === 'started' ? 'Ожидание ответа…' : detail))}${event.response?.truncated ? ' (сокращено)' : ''}</p></article>`;
    }).join('') || '<p class="small-copy">Пока нет наблюдаемых операций.</p>';
    rows.scrollTop = follow ? rows.scrollHeight : scroll;
  }

  /** Requests actual renderer coordinates only for current, recent authorized node keys. Missing positions remain unplaced. */
  function updateMarkers(): void {
    markers.replaceChildren();
    if (!preferences.markersEnabled) return;
    const bySession = new Map<string, ActivityEvent>();
    for (const event of current) bySession.set(event.sessionId, event);
    const candidates = [...bySession.values()].filter(event => Date.now() - Date.parse(event.occurredAt) < 60_000 && event.target?.status === 'available').slice(-32);
    const request: PositionRequest = { ids: candidates.map(event => event.target!.nodeKey!) };
    window.dispatchEvent(new CustomEvent('agent-activity:positions', { detail: request }));
    for (const event of candidates) {
      const position = request.positions?.find(point => point.id === event.target?.nodeKey);
      if (!position) continue;
      const marker = document.createElement('span'); marker.className = 'agent-activity-marker';
      marker.textContent = `${event.clientKind} · ${event.operation}`; marker.style.transform = `translate(${position.x}px, ${position.y}px)`;
      markers.append(marker);
    }
  }

  function updateVisibility(): void {
    panel.hidden = !preferences.chatEnabled || minimized;
    panel.classList.toggle('agent-activity__panel--expanded', preferences.expanded);
    restore.hidden = !preferences.chatEnabled || !minimized;
    host.querySelector<HTMLElement>('[data-launcher="chat"]')!.hidden = preferences.chatEnabled && !minimized;
    const expand = host.querySelector<HTMLButtonElement>('[data-action="expand"]')!;
    expand.textContent = preferences.expanded ? '↙' : '↗'; expand.setAttribute('aria-label', preferences.expanded ? 'Вернуть компактный размер' : 'Расширить журнал');
    stream.setActive(preferences.markersEnabled || preferences.chatEnabled && !minimized);
    clearInterval(markerTimer); markerTimer = preferences.markersEnabled ? setInterval(updateMarkers, 150) : undefined;
    savePreferences(preferences); updateMarkers();
  }

  /** Reads only a user-requested bounded history page, protected from stale filter responses. */
  async function loadOlder(reset = false): Promise<void> {
    const version = ++requestVersion;
    const params = new URLSearchParams({ limit: '100' });
    if (query) params.set('q', query); if (session) params.set('sessionId', session);
    const oldest = (historyMode ? history : current)[0];
    if (!reset && oldest) params.set('beforeSequence', oldest.sequence);
    try {
      const response = await fetch(`/api/agent-activity/history?${params}`, { credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok) throw new Error('unavailable');
      const body = await response.json();
      if (disposed || version !== requestVersion || !Array.isArray(body.events)) return;
      history = reduceEvents([], body.events); historyMode = true; follow = false; renderRows();
      if (!body.hasMore) status.textContent = 'Начало доступной истории';
    } catch { if (!disposed && version === requestVersion) status.textContent = 'История недоступна'; }
  }

  async function clear(): Promise<void> {
    try {
      const tokenResponse = await fetch('/api/antiforgery', { credentials: 'same-origin', cache: 'no-store' });
      const token = await tokenResponse.json();
      const response = await fetch('/api/agent-activity/clear', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token.requestToken }, body: '{}' });
      if (!response.ok) throw new Error('unavailable');
      history = []; current = []; historyMode = false; stream.reset(); renderRows(); updateMarkers();
    } catch { if (!disposed) status.textContent = 'Не удалось очистить журнал'; }
  }

  chatToggle.addEventListener('change', () => { preferences.chatEnabled = chatToggle.checked; minimized = false; updateVisibility(); });
  markToggle.addEventListener('change', () => { preferences.markersEnabled = markToggle.checked; updateVisibility(); });
  rows.addEventListener('scroll', () => { follow = rows.scrollHeight - rows.scrollTop - rows.clientHeight < 24; });
  host.querySelector('form')!.addEventListener('submit', event => { event.preventDefault(); const data = new FormData(event.target as HTMLFormElement); query = String(data.get('q') ?? ''); session = String(data.get('session') ?? ''); history = []; renderRows(); void loadOlder(true); });
  host.addEventListener('click', event => {
    const action = (event.target as Element).closest<HTMLElement>('[data-action]')?.dataset.action;
    if (action === 'filters') { const details = host.querySelector<HTMLElement>('.agent-activity__details')!; details.hidden = !details.hidden; host.querySelector('[data-action="filters"]')!.setAttribute('aria-expanded', String(!details.hidden)); }
    if (action === 'expand') { preferences.expanded = !preferences.expanded; updateVisibility(); }
    if (action === 'minimize') { minimized = true; updateVisibility(); restore.focus(); }
    if (action === 'restore') { minimized = false; updateVisibility(); host.querySelector<HTMLButtonElement>('[data-action="hide"]')!.focus(); }
    if (action === 'hide') { preferences.chatEnabled = false; chatToggle.checked = false; minimized = false; updateVisibility(); chatToggle.focus(); }
    if (action === 'older') void loadOlder();
    if (action === 'follow') { historyMode = false; history = []; follow = true; renderRows(); }
    if (action === 'clear') void clear();
  });
  updateVisibility();
  return () => { disposed = true; requestVersion++; stream.dispose(); clearInterval(markerTimer); host.remove(); markers.remove(); };
}
