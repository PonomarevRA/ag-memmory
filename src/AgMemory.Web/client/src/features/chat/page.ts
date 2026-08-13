import { begin, stop, type ChatReceiver } from './chat-stream.js';
import { loadThread, saveThread, sanitizeThreadId, type ThreadMessage } from '../navigation/browser-state.js';
import { navigate, render } from '../../app/router.js';
import { escapeHtml } from '../../shared/html.js';

const MAXIMUM_PROMPT_LENGTH = 4_000;

export function chatPage(routeId: string): string {
  const id = sanitizeThreadId(routeId) ?? crypto.randomUUID().replaceAll('-', '');
  if (!routeId) queueMicrotask(() => navigate(`/chat/${id}`));
  const messages = loadThread(id);
  queueMicrotask(() => wire(id, messages));
  return `<section class="chat-page"><header class="page-header page-header--chat"><div><p class="eyebrow">Локальный диалог</p><h1>Чат с моделью</h1><p>История остаётся в браузере; доступ к модели происходит через серверный gateway.</p></div><span class="scope-badge">До 4 000 символов</span></header><section class="chat-layout"><aside class="thread-rail"><strong>Диалог</strong><span class="scope-badge">Только этот браузер</span><p class="small-copy">Новая переписка открывается по ссылке «Чат» в меню.</p></aside><section class="chat-panel"><div id="messages" class="message-list" aria-live="polite">${messages.map(message => messageMarkup(message)).join('') || '<p class="muted">Начните диалог.</p>'}</div><form id="composer" class="composer"><label class="visually-hidden" for="chat-prompt">Сообщение модели</label><textarea id="chat-prompt" class="composer__input" maxlength="${MAXIMUM_PROMPT_LENGTH}" placeholder="Спросите модель…" aria-describedby="chat-hint"></textarea><div class="composer__footer"><span id="chat-hint" class="small-copy">Ответ можно остановить в любой момент.</span><div class="button-row"><button id="send" class="ui-button" type="submit">Отправить</button><button id="stop" class="ui-button ui-button--danger" type="button" disabled>Остановить</button></div></div></form></section></section></section>`;
}
function messageMarkup(message: ThreadMessage): string { return `<article class="message message--${message.role}"><p class="message__label">${message.role === 'user' ? 'Вы' : 'Модель'}</p><p class="message__content">${escapeHtml(message.content)}</p></article>`; }
function wire(id: string, messages: ThreadMessage[]): void {
  const form = document.querySelector<HTMLFormElement>('#composer');
  const input = document.querySelector<HTMLTextAreaElement>('#chat-prompt');
  const list = document.querySelector<HTMLElement>('#messages');
  const stopButton = document.querySelector<HTMLButtonElement>('#stop');
  const sendButton = document.querySelector<HTMLButtonElement>('#send');
  const setPending = (pending: boolean) => { if (stopButton) stopButton.disabled = !pending; if (sendButton) sendButton.disabled = pending; };
  stopButton?.addEventListener('click', () => { stop(); setPending(false); });
  form?.addEventListener('submit', event => {
    event.preventDefault();
    const prompt = input?.value.trim();
    if (!prompt || prompt.length > MAXIMUM_PROMPT_LENGTH || !input || !list) return;
    const user = { id: crypto.randomUUID(), role: 'user' as const, content: prompt, createdAt: new Date().toISOString() };
    const assistant = { id: crypto.randomUUID(), role: 'assistant' as const, content: '', createdAt: new Date().toISOString() };
    messages.push(user, assistant); input.value = ''; list.innerHTML = messages.map(messageMarkup).join(''); setPending(true);
    const receiver: ChatReceiver = { invokeMethodAsync: async (method, item) => {
      if (method === 'OnChatEvent' && typeof item === 'object' && item) {
        const streamEvent = item as { kind?: number; content?: string; errorCode?: string };
        if (streamEvent.kind === 0 && typeof streamEvent.content === 'string') assistant.content += streamEvent.content;
        if (streamEvent.kind === 1) assistant.content = errorMessage(streamEvent.errorCode);
        list.innerHTML = messages.map(messageMarkup).join('');
      }
      if (method === 'OnChatCompleted') { setPending(false); saveThread(id, messages); }
    }};
    begin({ threadId: id, prompt }, receiver);
  });
}

function errorMessage(errorCode?: string): string {
  if (errorCode === 'RateLimited') return 'Слишком много запросов. Подождите немного и повторите попытку.';
  if (errorCode === 'Interrupted') return 'Ответ остановлен.';
  if (errorCode === 'InvalidInput') return 'Сообщение не прошло проверку. Сократите или измените текст.';
  return 'Ответ недоступен. Повторите попытку позже.';
}
