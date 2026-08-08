const CHAT_EVENT_METHOD = 'OnChatEvent';
const CHAT_COMPLETED_METHOD = 'OnChatCompleted';
const CHAT_ROUTE = '/api/chat';
const ANTIFORGERY_FIELD = '__RequestVerificationToken';
const ANTIFORGERY_HEADER = 'RequestVerificationToken';

let controller;

async function publish(receiver, item) {
    try {
        await receiver.invokeMethodAsync(CHAT_EVENT_METHOD, item);
    } catch {
        // A SignalR circuit can close while a fetch stream is finishing.
    }
}

async function complete(receiver) {
    try {
        await receiver.invokeMethodAsync(CHAT_COMPLETED_METHOD);
    } catch {
        // The .NET receiver may already be disposed.
    }
}

function errorCodeFor(response) {
    if (response.status === 400) return 'InvalidInput';
    if (response.status === 401 || response.status === 403) return 'Unauthorized';
    if (response.status === 429) return 'RateLimited';
    if (response.status === 408 || response.status === 504) return 'Timeout';
    return 'Unavailable';
}

async function readStream(response, receiver) {
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

async function run(request, receiver, activeController) {
    try {
        const response = await fetch(CHAT_ROUTE, {
            method: 'POST',
            headers: requestHeaders(),
            body: JSON.stringify(request),
            signal: activeController.signal,
            credentials: 'same-origin'
        });
        if (!response.ok) {
            await publish(receiver, { kind: 1, errorCode: errorCodeFor(response) });
            return;
        }
        await readStream(response, receiver);
    } catch (error) {
        const errorCode = error?.name === 'AbortError' ? 'Interrupted' : 'Unavailable';
        await publish(receiver, { kind: 1, errorCode });
    } finally {
        if (controller === activeController) controller = undefined;
        await complete(receiver);
    }
}

function requestHeaders() {
    const token = document.querySelector(`input[name="${ANTIFORGERY_FIELD}"]`)?.value;
    return token
        ? { 'Content-Type': 'application/json', [ANTIFORGERY_HEADER]: token }
        : { 'Content-Type': 'application/json' };
}

export function begin(request, receiver) {
    if (controller) controller.abort();
    const activeController = new AbortController();
    controller = activeController;
    run(request, receiver, activeController);
}

export function stop() {
    if (controller) controller.abort();
}

export function dispose() {
    stop();
}
