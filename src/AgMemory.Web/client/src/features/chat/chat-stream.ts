import { antiforgeryJsonHeaders } from '@shared/antiforgery.js';

const CHAT_EVENT_METHOD = 'OnChatEvent';
const CHAT_COMPLETED_METHOD = 'OnChatCompleted';
export const CHAT_ROUTE = '/api/chat';

export type ChatReceiver = {
  invokeMethodAsync(method: string, item: unknown): Promise<void>;
};

let controller: AbortController | undefined;

async function publish(receiver: ChatReceiver, item: unknown): Promise<void> {
  try {
    await receiver.invokeMethodAsync(CHAT_EVENT_METHOD, item);
  } catch {
    // SignalR circuit may close while the stream finishes.
  }
}

async function complete(receiver: ChatReceiver): Promise<void> {
  try {
    await receiver.invokeMethodAsync(CHAT_COMPLETED_METHOD);
  } catch {
    // .NET receiver may already be disposed.
  }
}

function errorCodeFor(status: number): string {
  if (status === 400) return 'InvalidInput';
  if (status === 401 || status === 403) return 'Unauthorized';
  if (status === 429) return 'RateLimited';
  if (status === 408 || status === 504) return 'Timeout';
  return 'Unavailable';
}

async function readStream(response: Response, receiver: ChatReceiver): Promise<void> {
  const reader = response.body?.getReader();
  if (!reader) {
    await publish(receiver, { kind: 1, errorCode: 'Unavailable' });
    return;
  }

  const decoder = new TextDecoder();
  let buffer = '';
  for (;;) {
    const next = await reader.read();
    if (next.done) break;
    buffer += decoder.decode(next.value, { stream: true });
    const lines = buffer.split('\n');
    buffer = lines.pop() ?? '';
    for (const line of lines) {
      if (!line.trim()) continue;
      try {
        await publish(receiver, JSON.parse(line));
      } catch {
        await publish(receiver, { kind: 1, errorCode: 'Unavailable' });
      }
    }
  }
}

async function run(request: unknown, receiver: ChatReceiver, activeController: AbortController): Promise<void> {
  try {
    const response = await fetch(CHAT_ROUTE, {
      method: 'POST',
      headers: antiforgeryJsonHeaders(),
      body: JSON.stringify(request),
      signal: activeController.signal,
      credentials: 'same-origin'
    });
    if (!response.ok) {
      await publish(receiver, { kind: 1, errorCode: errorCodeFor(response.status) });
      return;
    }
    await readStream(response, receiver);
  } catch (error) {
    const errorCode = (error as Error)?.name === 'AbortError' ? 'Interrupted' : 'Unavailable';
    await publish(receiver, { kind: 1, errorCode });
  } finally {
    if (controller === activeController) controller = undefined;
    await complete(receiver);
  }
}

/** Starts a streaming chat request against the local Yuki-compatible gateway. */
export function begin(request: unknown, receiver: ChatReceiver): void {
  controller?.abort();
  const activeController = new AbortController();
  controller = activeController;
  void run(request, receiver, activeController);
}

export function stop(): void {
  controller?.abort();
}

export function dispose(): void {
  stop();
}
