import { afterEach, describe, expect, it, vi } from 'vitest';
import { DEFAULTS, reduceEvents, safeEvent, compareSequence } from './model.js';
import { mountAgentActivity } from './panel.js';
import { ActivityStream } from './stream.js';

class Socket {
  static all: Socket[] = [];
  sent: string[] = []; onopen?: () => void; onmessage?: (event: { data: string }) => void; onclose?: () => void; onerror?: () => void;
  constructor(public url: string) { Socket.all.push(this); }
  send(value: string) { this.sent.push(value); }
  close() { this.onclose?.(); }
}
const event = (sequence = '1', phase = 'finished') => ({ schemaVersion: 'agent-activity/v1', eventId: sequence, journalEpoch: 'epoch', sequence,
  occurredAt: new Date().toISOString(), sessionId: 'session', operationId: 'operation', operation: 'RecallAsync', phase, clientKind: 'codex', outcome: 'succeeded',
  request: { tool: 'RecallAsync', summary: 'Поиск памяти' }, query: { mode: 'preview', preview: '<script>example</script>' }, response: phase === 'finished' ? { summary: 'Найдено 3 записи' } : null });
let dispose: (() => void) | undefined;
afterEach(() => { dispose?.(); dispose = undefined; localStorage.clear(); document.body.replaceChildren(); vi.useRealTimers(); vi.unstubAllGlobals(); Socket.all = []; });

describe('portable activity consumers', () => {
  it('starts both independent controls OFF, opens one socket, minimizes and closes without changing markers', () => {
    vi.stubGlobal('WebSocket', Socket); dispose = mountAgentActivity();
    expect(DEFAULTS).toEqual({ chatEnabled: false, markersEnabled: false, expanded: false });
    const chat = document.querySelector<HTMLInputElement>('[data-setting="chat"]')!;
    const markers = document.querySelector<HTMLInputElement>('[data-setting="markers"]')!;
    expect(chat.checked).toBe(false); expect(markers.checked).toBe(false); expect(Socket.all).toHaveLength(0);
    chat.click(); expect(Socket.all).toHaveLength(1); expect(markers.checked).toBe(false);
    markers.click(); expect(Socket.all).toHaveLength(1);
    document.querySelector<HTMLButtonElement>('[data-action="expand"]')!.click();
    expect(document.querySelector('.agent-activity__panel--expanded')).not.toBeNull();
    document.querySelector<HTMLButtonElement>('[data-action="hide"]')!.click();
    expect(chat.checked).toBe(false); expect(markers.checked).toBe(true);
    expect(document.querySelector<HTMLElement>('.agent-activity__panel')!.hidden).toBe(true);
    markers.click(); expect(document.querySelector('.agent-activity-markers')!.childElementCount).toBe(0);
  });

  it('ACKs after bounded application and pairs actual request and response with escaped query text', () => {
    vi.stubGlobal('WebSocket', Socket); dispose = mountAgentActivity();
    document.querySelector<HTMLInputElement>('[data-setting="chat"]')!.click();
    const socket = Socket.all[0]; socket.onopen?.();
    socket.onmessage?.({ data: JSON.stringify({ type: 'snapshot', messageId: 'message', journalEpoch: 'epoch', cursor: { journalEpoch: 'epoch', sequence: '2' }, events: [event('1', 'started'), event('2')] }) });
    expect(JSON.parse(socket.sent.at(-1)!)).toEqual({ type: 'ack', messageId: 'message', cursor: { journalEpoch: 'epoch', sequence: '2' } });
    const rows = document.querySelector('.agent-activity__rows')!;
    expect(rows.querySelectorAll('article')).toHaveLength(1); expect(rows.querySelector('script')).toBeNull();
    expect(rows.textContent).toContain('Запрос:'); expect(rows.textContent).toContain('Найдено 3 записи'); expect(rows.textContent).toContain('<script>example</script>');
    expect(localStorage.getItem('agmemory.activity.v1')).not.toContain('example');
  });

  it('resets unavailable epochs and stops reconnecting when the last consumer closes', () => {
    vi.useFakeTimers(); vi.stubGlobal('WebSocket', Socket);
    const stream = new ActivityStream(vi.fn()); stream.setActive(true);
    Socket.all[0].onmessage?.({ data: JSON.stringify({ type: 'resetRequired' }) });
    vi.advanceTimersByTime(1500); expect(Socket.all).toHaveLength(2);
    stream.dispose(); vi.advanceTimersByTime(10_000); expect(Socket.all).toHaveLength(2);
  });

  it('ignores an old history response after an external epoch reset', async () => {
    vi.stubGlobal('WebSocket', Socket);
    let resolve!: (value: unknown) => void;
    vi.stubGlobal('fetch', vi.fn(() => new Promise(value => { resolve = value; })));
    dispose = mountAgentActivity(); document.querySelector<HTMLInputElement>('[data-setting="chat"]')!.click();
    const socket = Socket.all[0]; socket.onopen?.();
    socket.onmessage?.({ data: JSON.stringify({ type: 'snapshot', messageId: 'initial', journalEpoch: 'epoch', cursor: { journalEpoch: 'epoch', sequence: '2' }, events: [event('2')] }) });
    document.querySelector<HTMLButtonElement>('[data-action="older"]')!.click();
    socket.onmessage?.({ data: JSON.stringify({ type: 'resetRequired' }) });
    resolve({ ok: true, json: async () => ({ events: [{ ...event('1'), operation: 'OldHistoryMustDisappear' }], hasMore: false }) });
    await Promise.resolve(); await Promise.resolve(); await Promise.resolve();
    expect(document.querySelector('.agent-activity__rows')!.textContent).not.toContain('OldHistoryMustDisappear');
  });

  it('rejects private unexpected fields, bounds replay memory and orders decimal sequence exactly', () => {
    const clean = safeEvent({ ...event(), actorId: 'private-actor', scope: 'private-scope', content: 'private-body' });
    expect(JSON.stringify(clean)).not.toContain('private-');
    const values = Array.from({ length: 800 }, (_, index) => ({ ...event(String(index + 1)), operationId: String(index) }));
    expect(reduceEvents([], values)).toHaveLength(500);
    expect(reduceEvents([], reduceEvents([], values))).toHaveLength(500);
    expect(compareSequence('9007199254740993', '9007199254740992')).toBeGreaterThan(0);
    expect(safeEvent({ ...event(), sequence: '01' })).toBeNull();
  });
});


it('moves focus to visible controls when the activity panel closes or minimizes', () => {
  vi.stubGlobal('WebSocket', Socket); dispose = mountAgentActivity();
  const chat = document.querySelector<HTMLInputElement>('[data-setting="chat"]')!;
  chat.click();
  const minimize = document.querySelector<HTMLButtonElement>('[data-action="minimize"]')!;
  minimize.focus(); minimize.click();
  const restore = document.querySelector<HTMLButtonElement>('[data-action="restore"]')!;
  expect(document.activeElement).toBe(restore); expect(restore.hidden).toBe(false);
  restore.click();
  const close = document.querySelector<HTMLButtonElement>('[data-action="hide"]')!;
  expect(document.activeElement).toBe(close);
  const filters = document.querySelector<HTMLButtonElement>('[data-action="filters"]')!;
  filters.click(); expect(filters.getAttribute('aria-expanded')).toBe('true');
  expect(document.getElementById(filters.getAttribute('aria-controls')!)?.hidden).toBe(false);
  close.click(); expect(document.activeElement).toBe(chat);
  expect(chat.closest<HTMLElement>('[data-launcher]')!.hidden).toBe(false);
});
