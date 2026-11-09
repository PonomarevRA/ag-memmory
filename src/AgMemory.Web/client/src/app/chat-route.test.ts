import { expect, it, vi } from 'vitest';

vi.mock('./shell.js', () => ({ shell: (content: string) => `<main id="main" tabindex="-1">${content}</main>` }));
vi.mock('../features/memory-graph/page.js', () => ({ mountMemoryGraphPage: vi.fn(), disposeMemoryGraph: vi.fn() }));
vi.mock('../features/chat/chat-stream.js', () => ({ begin: vi.fn(), stop: vi.fn(), dispose: vi.fn() }));
vi.mock('../features/memory-reader/page-controller.js', () => ({ dispose: vi.fn(), mountMemoryReaderPage: vi.fn() }));
vi.mock('../features/navigation/index.js', () => ({ trackVisit: vi.fn() }));
vi.mock('../features/app-version/api.js', () => ({ loadAppVersion: vi.fn().mockResolvedValue(null) }));

import { startRouter } from './router.js';
import { begin } from '../features/chat/chat-stream.js';

/** Exercises actual async renderPage → route commit → mount rather than injecting markup before a queued bind. */
it('mounts the real chat only after async route commit and prevents native form navigation', async () => {
  history.replaceState(null, '', '/chat/' + 'd'.repeat(32));
  const root = document.createElement('div'); document.body.append(root);
  try {
    startRouter(root);
    await vi.waitFor(() => expect(root.querySelector('#composer')).not.toBeNull());
    root.querySelector<HTMLTextAreaElement>('#chat-prompt')!.value = 'Synthetic route lifecycle question';
    const submit = new Event('submit', { bubbles: true, cancelable: true });
    root.querySelector<HTMLFormElement>('#composer')!.dispatchEvent(submit);
    expect(submit.defaultPrevented).toBe(true);
    expect(begin).toHaveBeenCalledTimes(1);
    expect(root.querySelector('.message--user')?.textContent).toContain('Synthetic route lifecycle question');
    const receiver = vi.mocked(begin).mock.calls[0][1];
    await receiver.invokeMethodAsync('OnChatEvent', { kind: 0, text: 'Actual mounted reply' });
    expect(root.querySelector('.message--assistant')?.textContent).toContain('Actual mounted reply');
  } finally { root.remove(); localStorage.clear(); }
});
