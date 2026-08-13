import { describe, expect, it, vi } from 'vitest';
vi.mock('./chat-stream.js', () => ({ begin: vi.fn(), stop: vi.fn() }));
vi.mock('../navigation/browser-state.js', () => ({ loadThread: vi.fn().mockReturnValue([]), saveThread: vi.fn(), sanitizeThreadId: vi.fn().mockReturnValue('a'.repeat(32)) }));
import { chatPage } from './page.js';
import { loadThread } from '../navigation/browser-state.js';
describe('chat page', () => {
  it('renders labeled composer with the server prompt limit', () => {
    document.body.innerHTML = chatPage('a'.repeat(32));
    expect(document.querySelector('#chat-prompt')?.getAttribute('maxlength')).toBe('4000');
    expect(document.querySelector('#composer')).not.toBeNull();
    expect(document.querySelector('.chat-layout')).not.toBeNull();
  });

  it('escapes stored user content before string rendering', () => {
    vi.mocked(loadThread).mockReturnValueOnce([{ id: '1', role: 'user', content: '<img src=x onerror=alert(1)>', createdAt: '2026-01-01T00:00:00Z' }]);
    document.body.innerHTML = chatPage('a'.repeat(32));
    expect(document.querySelector('.message img')).toBeNull();
    expect(document.querySelector('.message__content')?.textContent).toContain('<img src=x onerror=alert(1)>');
  });
});
