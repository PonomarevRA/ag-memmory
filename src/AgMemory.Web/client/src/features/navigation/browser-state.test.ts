import { describe, expect, it } from 'vitest';
import { sanitizeRoute, sanitizeThreadId, normalizedVisits } from './browser-state.js';

describe('navigation browser-state', () => {
  it('sanitizes routes', () => {
    expect(sanitizeRoute('chat')).toBe('/chat');
    expect(sanitizeRoute('/memory-reader?x=1')).toBe('/memory-reader');
  });

  it('accepts canonical thread ids only', () => {
    expect(sanitizeThreadId('abc')).toBeNull();
    expect(sanitizeThreadId('a'.repeat(32))).toHaveLength(32);
  });

  it('returns empty visits when storage is unreadable', () => {
    expect(normalizedVisits()).toEqual([]);
  });
});

describe('per-response chat evidence persistence', () => {
  it('survives a reload while discarding unknown storage fields and unsafe source links', async () => {
    const { saveThread, loadThread } = await import('./browser-state.js');
    const threadId = 'b'.repeat(32);
    const memory = { type: 'Fact', title: 'Proof', preview: 'Text', href: '/api/chat-memory/sources/token', revision: 7, sourceState: 'available' };
    saveThread(threadId, [{ id: 'answer', role: 'assistant', content: 'Answer', createdAt: '2026-10-21T10:00:00Z', memory: [memory], scope: 'never-store' }]);
    expect(loadThread(threadId)[0].memory).toEqual([memory]);
    expect(localStorage.getItem('agmemory.ui.thread.v1.' + threadId)).not.toContain('never-store');
    localStorage.setItem('agmemory.ui.thread.v1.' + threadId, JSON.stringify([{ id: 'answer', role: 'assistant', content: 'Answer', createdAt: 'today', memory: [{ ...memory, href: 'javascript:alert(1)', apiKey: 'secret' }], authority: 'secret' }]));
    const reloaded = loadThread(threadId)[0];
    expect(reloaded.memory?.[0]).not.toHaveProperty('href');
    expect(JSON.stringify(reloaded)).not.toContain('secret');
    localStorage.removeItem('agmemory.ui.thread.v1.' + threadId);
  });
});


it('persists only recognized response error codes alongside the original content', async () => {
  const { saveThread, loadThread } = await import('./browser-state.js');
  const id = 'c'.repeat(32);
  saveThread(id, [{ id: 'reply', role: 'assistant', content: 'Partial', createdAt: 'today', errorCode: 'Interrupted' }]);
  expect(loadThread(id)[0]).toMatchObject({ content: 'Partial', errorCode: 'Interrupted' });
  saveThread(id, [{ id: 'reply', role: 'assistant', content: 'Text', createdAt: 'today', errorCode: '<untrusted>' }]);
  expect(loadThread(id)[0]).not.toHaveProperty('errorCode');
  localStorage.removeItem('agmemory.ui.thread.v1.' + id);
});
