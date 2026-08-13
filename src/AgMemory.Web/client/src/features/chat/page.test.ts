import { describe, expect, it, vi } from 'vitest';
vi.mock('./chat-stream.js', () => ({ begin: vi.fn(), stop: vi.fn() }));
vi.mock('../navigation/browser-state.js', () => ({ loadThread: vi.fn().mockReturnValue([]), saveThread: vi.fn(), sanitizeThreadId: vi.fn().mockReturnValue('a'.repeat(32)) }));
import { chatPage } from './page.js';
describe('chat page', () => { it('renders labeled composer and local thread shell', () => { document.body.innerHTML = chatPage('a'.repeat(32)); expect(document.querySelector('#chat-prompt')?.getAttribute('maxlength')).toBe('16000'); expect(document.querySelector('#composer')).not.toBeNull(); }); });
