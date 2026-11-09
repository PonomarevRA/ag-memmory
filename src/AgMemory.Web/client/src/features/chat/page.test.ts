import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
vi.mock('./chat-stream.js', () => ({ begin: vi.fn(), stop: vi.fn() }));
vi.mock('../navigation/browser-state.js', () => ({ loadThread: vi.fn().mockReturnValue([]), saveThread: vi.fn(), sanitizeThreadId: vi.fn().mockReturnValue('a'.repeat(32)) }));
import { chatPage, mountChatPage } from './page.js';
import { begin, stop } from './chat-stream.js';
import { loadThread, saveThread } from '../navigation/browser-state.js';
describe('chat page', () => {
  afterEach(() => { document.body.innerHTML = ''; vi.clearAllMocks(); });
  it('renders labeled composer with the server prompt limit', () => {
    document.body.innerHTML = chatPage('a'.repeat(32)); mountChatPage(document.body);
    expect(document.querySelector('#chat-prompt')?.getAttribute('maxlength')).toBe('4000');
    expect(document.querySelector('#composer')).not.toBeNull();
    expect(document.querySelector('.chat-layout')).not.toBeNull();
  });

  it('escapes stored user content before string rendering', () => {
    vi.mocked(loadThread).mockReturnValueOnce([{ id: '1', role: 'user', content: '<img src=x onerror=alert(1)>', createdAt: '2026-01-01T00:00:00Z' }]);
    document.body.innerHTML = chatPage('a'.repeat(32)); mountChatPage(document.body);
    expect(document.querySelector('.message img')).toBeNull();
    expect(document.querySelector('.message__content')?.textContent).toContain('<img src=x onerror=alert(1)>');
  });

  it('retains original response evidence after the next question and passes it to local persistence', async () => {
    document.body.innerHTML = chatPage('a'.repeat(32)); mountChatPage(document.body);
    await Promise.resolve();
    const input = document.querySelector<HTMLTextAreaElement>('#chat-prompt')!;
    input.value = 'First';
    document.querySelector<HTMLFormElement>('#composer')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    const receiver = vi.mocked(begin).mock.calls.at(-1)![1];
    await receiver.invokeMethodAsync('OnChatEvent', { kind: 2, memory: { type: 'Fact', title: '<Proof>', preview: '<img src=x onerror=bad>', href: '/api/chat-memory/sources/safe-token', revision: 2 } });
    await receiver.invokeMethodAsync('OnChatEvent', { kind: 0, text: 'Answer' });
    await receiver.invokeMethodAsync('OnChatCompleted');
    input.value = 'Second';
    document.querySelector<HTMLFormElement>('#composer')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    expect(document.querySelector('.message__memory')?.textContent).toContain('<Proof>');
    expect(document.querySelector('.message__memory img')).toBeNull();
    const link = document.querySelector<HTMLAnchorElement>('.message__memory a')!;
    expect(link.getAttribute('href')).toBe('/api/chat-memory/sources/safe-token');
    expect(link.target).toBe('_blank');
    expect(link.rel).toBe('noopener');
    expect(vi.mocked(saveThread).mock.calls[0][1]).toEqual(expect.arrayContaining([expect.objectContaining({ role: 'assistant', memory: [expect.objectContaining({ title: '<Proof>', revision: 2 })] })]));
  });

  it('renders stored evidence with an explicit fallback when a source link is unavailable', () => {
    vi.mocked(loadThread).mockReturnValueOnce([{ id: 'answer', role: 'assistant', content: 'Answer', createdAt: 'today', memory: [{ type: 'Fact', title: 'Old proof', preview: 'Evidence', sourceState: 'unavailable' }] }]);
    document.body.innerHTML = chatPage('a'.repeat(32)); mountChatPage(document.body);
    expect(document.querySelector('.message__memory')?.textContent).toContain('Old proof');
    expect(document.querySelector('.message__memory')?.textContent).toContain('Ссылка на источник недоступна');
    expect(document.querySelector('.message__memory a')).toBeNull();
  });
});


describe('chat interruption and request ownership', () => {
  beforeEach(() => { vi.mocked(loadThread).mockReturnValue([]); });
  afterEach(() => { document.body.replaceChildren(); vi.clearAllMocks(); });

  /** Exercises the actual page receiver with deferred host events, without a model. */
  async function submit(prompt: string) {
    const input = document.querySelector<HTMLTextAreaElement>('#chat-prompt')!;
    input.value = prompt;
    document.querySelector<HTMLFormElement>('#composer')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    return vi.mocked(begin).mock.calls.at(-1)![1];
  }

  it('retains partial text and evidence when interrupted and persists a separate status', async () => {
    document.body.innerHTML = chatPage('a'.repeat(32)); mountChatPage(document.body); await Promise.resolve();
    const receiver = await submit('Question');
    await receiver.invokeMethodAsync('OnChatEvent', { kind: 0, text: 'Partial answer' });
    await receiver.invokeMethodAsync('OnChatEvent', { kind: 2, memory: { type: 'Fact', title: 'Proof', preview: 'Evidence' } });
    document.querySelector<HTMLButtonElement>('#stop')!.click();
    await receiver.invokeMethodAsync('OnChatEvent', { kind: 1, errorCode: 'Interrupted' });
    await receiver.invokeMethodAsync('OnChatCompleted');
    expect(document.querySelector('.message--assistant .message__content')?.textContent).toBe('Partial answer');
    expect(document.querySelector('.message__status')?.textContent).toBe('Ответ остановлен.');
    expect(document.querySelector('.message__memory')?.textContent).toContain('Proof');
    expect(vi.mocked(saveThread).mock.calls.at(-1)![1]).toEqual(expect.arrayContaining([expect.objectContaining({ content: 'Partial answer', errorCode: 'Interrupted' })]));
  });

  it('ignores late events and completion from a stopped request after a newer submission', async () => {
    document.body.innerHTML = chatPage('a'.repeat(32)); mountChatPage(document.body); await Promise.resolve();
    const old = await submit('First');
    await old.invokeMethodAsync('OnChatEvent', { kind: 0, text: 'First partial' });
    document.querySelector<HTMLButtonElement>('#stop')!.click();
    const current = await submit('Second');
    await current.invokeMethodAsync('OnChatEvent', { kind: 0, text: 'Current answer' });
    await old.invokeMethodAsync('OnChatEvent', { kind: 1, errorCode: 'Interrupted' });
    await old.invokeMethodAsync('OnChatCompleted');
    expect(document.querySelectorAll('.message--assistant')[0].textContent).toContain('First partial');
    expect(document.querySelectorAll('.message--assistant')[1].textContent).toContain('Current answer');
    expect(document.querySelector<HTMLButtonElement>('#send')!.disabled).toBe(true);
    await current.invokeMethodAsync('OnChatCompleted');
    expect(document.querySelector<HTMLButtonElement>('#send')!.disabled).toBe(false);
  });

  it('keeps a transport error separate from received text and uses a visible field label', async () => {
    document.body.innerHTML = chatPage('a'.repeat(32)); mountChatPage(document.body); await Promise.resolve();
    const receiver = await submit('Question');
    await receiver.invokeMethodAsync('OnChatEvent', { kind: 0, text: 'Keep this text' });
    await receiver.invokeMethodAsync('OnChatEvent', { kind: 1, errorCode: 'Unavailable' });
    expect(document.querySelector('.message--assistant .message__content')?.textContent).toBe('Keep this text');
    expect(document.querySelector('.message__status')?.textContent).toContain('Ответ недоступен');
    expect(document.querySelector('label[for="chat-prompt"]')?.classList.contains('visually-hidden')).toBe(false);
  });
});


describe('chat route cleanup ownership', () => {
  beforeEach(() => { vi.mocked(loadThread).mockReturnValue([]); });
  afterEach(() => { document.body.replaceChildren(); vi.clearAllMocks(); vi.mocked(saveThread).mockReset(); vi.mocked(loadThread).mockReturnValue([]); });

  /** Dispatches the real composer while controlling the gateway receiver entirely in memory. */
  function submit(prompt: string) {
    document.querySelector<HTMLTextAreaElement>('#chat-prompt')!.value = prompt;
    document.querySelector<HTMLFormElement>('#composer')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    return vi.mocked(begin).mock.calls.at(-1)![1];
  }

  it('saves partial content before remount and fences the old receiver from a newer same-thread history', async () => {
    let stored: ReturnType<typeof loadThread> = [];
    vi.mocked(loadThread).mockImplementation(() => structuredClone(stored));
    vi.mocked(saveThread).mockImplementation((_id, messages) => { stored = structuredClone(messages as ReturnType<typeof loadThread>); });
    document.body.innerHTML = chatPage('a'.repeat(32));
    const disposeOld = mountChatPage(document.body);
    const oldForm = document.querySelector<HTMLFormElement>('#composer')!;
    const old = submit('First question');
    await old.invokeMethodAsync('OnChatEvent', { kind: 0, text: 'First partial' });
    await old.invokeMethodAsync('OnChatEvent', { kind: 2, memory: { type: 'Fact', title: 'Proof', preview: 'Evidence' } });
    disposeOld();
    expect(stop).toHaveBeenCalledTimes(1);
    expect(stored).toHaveLength(2);
    expect(stored[1]).toMatchObject({ content: 'First partial', errorCode: 'Interrupted', memory: [expect.objectContaining({ title: 'Proof' })] });
    oldForm.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    expect(begin).toHaveBeenCalledTimes(1);

    document.body.innerHTML = chatPage('a'.repeat(32));
    const disposeCurrent = mountChatPage(document.body);
    const current = submit('Second question');
    await current.invokeMethodAsync('OnChatEvent', { kind: 0, text: 'New answer' });
    disposeOld();
    expect(stop).toHaveBeenCalledTimes(1);
    await old.invokeMethodAsync('OnChatEvent', { kind: 0, text: 'Late text' });
    await old.invokeMethodAsync('OnChatCompleted');
    expect(document.querySelector<HTMLButtonElement>('#send')!.disabled).toBe(true);
    await current.invokeMethodAsync('OnChatCompleted');
    const finalHistory = structuredClone(stored);
    await old.invokeMethodAsync('OnChatCompleted');
    expect(stored).toEqual(finalHistory);
    expect(stored).toHaveLength(4);
    expect(stored.at(-1)?.content).toBe('New answer');
    expect(stored[1].content).toBe('First partial');
    expect(document.querySelector<HTMLButtonElement>('#send')!.disabled).toBe(false);
    disposeCurrent();
    expect(stop).toHaveBeenCalledTimes(1);
  });

  it('cleanup after a completed reply neither rewrites its status nor aborts a stream', async () => {
    document.body.innerHTML = chatPage('a'.repeat(32));
    const cleanup = mountChatPage(document.body);
    const receiver = submit('Question');
    await receiver.invokeMethodAsync('OnChatEvent', { kind: 0, text: 'Complete answer' });
    await receiver.invokeMethodAsync('OnChatCompleted');
    const savedCount = vi.mocked(saveThread).mock.calls.length;
    cleanup(); cleanup();
    await receiver.invokeMethodAsync('OnChatCompleted');
    expect(saveThread).toHaveBeenCalledTimes(savedCount);
    expect(stop).not.toHaveBeenCalled();
    expect(document.querySelector('.message__status')).toBeNull();
  });
});
