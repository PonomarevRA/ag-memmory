const ANTIFORGERY_FIELD = '__RequestVerificationToken';
const ANTIFORGERY_HEADER = 'RequestVerificationToken';

let currentToken: string | null = null;

/** Obtains the token and cookie from the same-origin server before a protected request. */
export async function getAntiforgeryToken(): Promise<string | null> {
  if (currentToken) return currentToken;
  try {
    const response = await fetch('/api/antiforgery', { credentials: 'same-origin', cache: 'no-store' });
    const body = await response.json() as { requestToken?: unknown };
    currentToken = response.ok && typeof body.requestToken === 'string' && body.requestToken.length > 0 ? body.requestToken : null;
  } catch { currentToken = null; }
  return currentToken;
}

/** Headers required for same-origin POST endpoints protected by antiforgery middleware. */
export function antiforgeryJsonHeaders(token: string | null): HeadersInit {
  return token
    ? { 'Content-Type': 'application/json', [ANTIFORGERY_HEADER]: token }
    : { 'Content-Type': 'application/json' };
}
