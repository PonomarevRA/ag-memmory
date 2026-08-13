const ANTIFORGERY_FIELD = '__RequestVerificationToken';
const ANTIFORGERY_HEADER = 'RequestVerificationToken';

/** Reads the antiforgery token rendered by the Blazor host into the page. */
export function readAntiforgeryToken(): string | null {
  if (typeof document === 'undefined') return null;
  const token = document.querySelector<HTMLInputElement>(`input[name="${ANTIFORGERY_FIELD}"]`)?.value;
  return token && token.length > 0 ? token : null;
}

/** Headers required for same-origin POST endpoints protected by antiforgery middleware. */
export function antiforgeryJsonHeaders(): HeadersInit {
  const token = readAntiforgeryToken();
  return token
    ? { 'Content-Type': 'application/json', [ANTIFORGERY_HEADER]: token }
    : { 'Content-Type': 'application/json' };
}
