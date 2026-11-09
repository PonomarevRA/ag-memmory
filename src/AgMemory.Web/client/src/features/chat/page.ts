import { begin, stop, type ChatReceiver } from './chat-stream.js';
import { loadThread, saveThread, sanitizeThreadId, type ThreadMessage } from '../navigation/browser-state.js';
import { navigate } from '../../app/router.js';
import { escapeHtml } from '../../shared/html.js';
import { safeMemoryComponent, safeMemoryComponents, MAX_MEMORY_COMPONENTS, type MemoryComponent } from './memory-component.js';

const MAXIMUM_PROMPT_LENGTH = 4_000;

/** Renders a browser-local thread with sanitized evidence attached to its original response. */
export function chatPage(routeId: string): string {
  const id = sanitizeThreadId(routeId) ?? crypto.randomUUID().replaceAll('-', '');
  if (!routeId) queueMicrotask(() => navigate(`/chat/${id}`));
  const messages = loadThread(id);
  return `<section class="chat-page" data-thread-id="${id}"><header class="page-header page-header--chat"><div><p class="eyebrow">Локальный диалог</p><h1>Чат с моделью</h1><p>История остаётся в браузере; доступ к модели происходит через серверный gateway.</p></div><span class="scope-badge">До 4 000 символов</span></header><section class="chat-layout"><aside class="thread-rail"><strong>Диалог</strong><span class="scope-badge">Только этот браузер</span><p class="small-copy">Новая переписка открывается по ссылке «Чат» в меню.</p></aside><section class="chat-panel"><div id="messages" class="message-list" aria-live="polite">${messages.map(message => messageMarkup(message)).join('') || '<p class="muted">Начните диалог.</p>'}</div><form id="composer" class="composer"><label for="chat-prompt">Сообщение модели</label><textarea id="chat-prompt" class="composer__input" maxlength="${MAXIMUM_PROMPT_LENGTH}" placeholder="Спросите модель…" aria-describedby="chat-hint"></textarea><div class="composer__footer"><span id="chat-hint" class="small-copy">Ответ можно остановить в любой момент.</span><div class="button-row"><button id="send" class="ui-button" type="submit">Отправить</button><button id="stop" class="ui-button ui-button--danger" type="button" disabled>Остановить</button></div></div></form></section></section></section>`;
}
/** Renders only the server-issued evidence link; missing or unavailable sources remain explicit. */
function evidenceMarkup(item: MemoryComponent): string {
  let link = '<span class="small-copy">Ссылка на источник недоступна.</span>';
  if (item.href && item.sourceState !== 'unavailable') {
    link = `<a href="${escapeHtml(item.href)}" target="_blank" rel="noopener">Открыть источник в Wiki</a>`;
  }
  const revision = item.revision ? `<span class="small-copy">Версия ${item.revision}</span>` : '';
  const capturedAt = item.capturedAt ? `<time class="small-copy" datetime="${escapeHtml(item.capturedAt)}">${escapeHtml(item.capturedAt)}</time>` : '';
  return `<li><strong>${escapeHtml(item.title)}</strong><span class="scope-badge">${escapeHtml(item.type)}</span><p>${escapeHtml(item.preview)}</p>${link}${revision}${capturedAt}</li>`;
}

/** Escapes message content and evidence independently, including histories loaded from local storage. */
function messageMarkup(message: ThreadMessage): string {
  const rows: string[] = [];
  for (const memory of safeMemoryComponents(message.memory)) rows.push(evidenceMarkup(memory));
  const memoryBlock = rows.length ? `<ul class="message__memory" aria-label="Использованная память">${rows.join('')}</ul>` : '';
  const role = message.role === 'user' ? 'user' : 'assistant';
  const status = message.errorCode ? `<p class="message__status" role="status">${escapeHtml(errorMessage(message.errorCode))}</p>` : '';
  return `<article data-message-id="${escapeHtml(message.id)}" class="message message--${role}"><p class="message__label">${role === 'user' ? 'Вы' : 'Модель'}</p>${memoryBlock}<p class="message__content">${escapeHtml(message.content)}</p>${status}</article>`;
}

/** Binds a committed route; its returned cleanup saves partial content before the route is replaced. */
export function mountChatPage(root: HTMLElement): () => void {
  const page = root.querySelector<HTMLElement>('.chat-page');
  const id = sanitizeThreadId(page?.dataset.threadId);
  return page && id ? wire(page, id, loadThread(id)) : () => {};
}

/** Streams one reply while retaining evidence on that reply across subsequent messages and reloads. */
function wire(root: HTMLElement, id: string, messages: ThreadMessage[]): () => void {
  const form = root.querySelector<HTMLFormElement>('#composer');
  const input = root.querySelector<HTMLTextAreaElement>('#chat-prompt');
  const list = root.querySelector<HTMLElement>('#messages');
  const stopButton = root.querySelector<HTMLButtonElement>('#stop');
  const sendButton = root.querySelector<HTMLButtonElement>('#send');
  const setPending = (pending: boolean) => { if (stopButton) stopButton.disabled = !pending; if (sendButton) sendButton.disabled = pending; };
  let generation = 0, disposed = false;
  let cancelCurrent: (() => void) | undefined;
  const onStop = () => { stop(); cancelCurrent?.(); };
  const onSubmit = (event: SubmitEvent) => {
    event.preventDefault();
    const prompt = input?.value.trim();
    if (disposed || !prompt || prompt.length > MAXIMUM_PROMPT_LENGTH || !input || !list) return;
    const requestGeneration = ++generation;
    let cancelled = false;
    const user = { id: crypto.randomUUID(), role: 'user' as const, content: prompt, createdAt: new Date().toISOString() };
    const assistant: ThreadMessage = { id: crypto.randomUUID(), role: 'assistant', content: '', createdAt: new Date().toISOString(), memory: [] };
    messages.push(user, assistant); input.value = ''; list.innerHTML = messages.map(message => messageMarkup(message)).join(''); setPending(true);
    // A response owns its node; a late callback must never target a newer reply.
    const renderResponse = () => {
      const response = list.querySelector<HTMLElement>(`[data-message-id="${assistant.id}"]`);
      if (response) response.outerHTML = messageMarkup(assistant);
    };
    cancelCurrent = () => {
      cancelled = true;
      assistant.errorCode = 'Interrupted';
      renderResponse();
      saveThread(id, messages);
      setPending(false);
    };
    const receiver: ChatReceiver = { invokeMethodAsync: async (method, item) => {
      if (disposed || requestGeneration !== generation) return;
      if (!cancelled && method === 'OnChatEvent' && typeof item === 'object' && item) {
        const streamEvent = item as { kind?: number; text?: string; content?: string; errorCode?: string; memory?: unknown };
        if (streamEvent.kind === 2 && assistant.memory!.length < MAX_MEMORY_COMPONENTS) {
          const memory = safeMemoryComponent(streamEvent.memory);
          if (memory) assistant.memory!.push(memory);
        }
        if (streamEvent.kind === 0) {
          const chunk = typeof streamEvent.text === 'string' ? streamEvent.text : streamEvent.content;
          if (typeof chunk === 'string') assistant.content += chunk;
        }
        if (streamEvent.kind === 1) assistant.errorCode = streamEvent.errorCode ?? 'Unavailable';
        // Streaming touches only this response; earlier evidence links retain their DOM and focus.
        renderResponse();
      }
      if (method === 'OnChatCompleted') { cancelCurrent = undefined; setPending(false); saveThread(id, messages); }
    }};
    begin({ threadId: id, prompt }, receiver);
  };
  stopButton?.addEventListener('click', onStop);
  form?.addEventListener('submit', onSubmit);
  /** Retain partial content before disposal, then revoke every callback belonging to this mount. */
  return () => {
    if (disposed) return;
    const pending = Boolean(cancelCurrent);
    cancelCurrent?.();
    disposed = true;
    generation++;
    cancelCurrent = undefined;
    stopButton?.removeEventListener('click', onStop);
    form?.removeEventListener('submit', onSubmit);
    if (pending) stop();
  };
}

function errorMessage(errorCode?: string): string {
  if (errorCode === 'RateLimited') return 'Слишком много запросов. Подождите немного и повторите попытку.';
  if (errorCode === 'Interrupted') return 'Ответ остановлен.';
  if (errorCode === 'InvalidInput') return 'Сообщение не прошло проверку. Сократите или измените текст.';
  return 'Ответ недоступен. Повторите попытку позже.';
}
