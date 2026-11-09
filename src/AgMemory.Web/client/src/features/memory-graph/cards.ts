import type { GraphNode } from './sanitize.js';

export type ReaderCard = { title: string; preview: string; version: number };

/** Loads current reader text for at most25 already-issued routes in the graph's exact matched area. */
export async function loadCards(nodes: GraphNode[], area: string, signal: AbortSignal): Promise<Map<string, ReaderCard> | null> {
  if (!/^[a-z][a-z0-9-]{0,63}$/.test(area) || nodes.length > 25) return null;
  const routes = new Map<string, string>();
  for (const node of nodes) {
    const match = /^\/memory-reader\/([a-zA-Z0-9_-]{1,128})\?area=([a-z][a-z0-9-]{0,63})$/.exec(node.href);
    if (match && match[2] === area) routes.set(match[1], node.href);
  }
  if (!routes.size) return new Map();
  try {
    const parameters = new URLSearchParams({ area, routes: [...routes.keys()].join(',') });
    const response = await fetch(`/api/memory-reader/cards?${parameters}`, { credentials: 'same-origin', cache: 'no-store', signal });
    if (!response.ok) return null;
    const body = await response.json();
    if (body?.status !== 'available' || body.areaId !== area || !Array.isArray(body.cards) || body.cards.length > 25) return null;
    const result = new Map<string, ReaderCard>();
    for (const card of body.cards) {
      const href = routes.get(card?.routeKey);
      if (!href || result.has(href) || typeof card.title !== 'string' || !card.title.trim() || card.title.length > 160 ||
          typeof card.preview !== 'string' || card.preview.length > 320 || !Number.isSafeInteger(card.version) || card.version < 1) return null;
      result.set(href, { title: card.title, preview: card.preview, version: card.version });
    }
    return result;
  } catch { return null; }
}
