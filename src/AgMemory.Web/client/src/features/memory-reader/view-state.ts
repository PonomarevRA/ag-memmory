/** Reader statuses distinguish empty results, transient failures and invalidated snapshots. */
export type StreamStatus = 'loading' | 'available' | 'empty' | 'unavailable' | 'changed';

/** Normalizes server statuses without hiding a changed generation behind a generic error. */
export function streamStatus(status: string, count: number): StreamStatus {
  if (status === 'available') return count ? 'available' : 'empty';
  if (status === 'changed' || status === 'stale') return 'changed';
  return 'unavailable';
}

/** Keeps reader history private to this running tab; History receives only a random lookup key. */
export class ReaderHistory<T> {
  private readonly entries = new Map<string, T>();
  constructor(private readonly capacity = 12) {}

  /** Replaces the current entry's bounded in-memory snapshot; never writes queries or cursors to storage. */
  remember(snapshot: T): void {
    let key = history.state?.readerView;
    if (typeof key !== 'string') {
      key = crypto.randomUUID();
      history.replaceState({ ...history.state, readerView: key }, '', location.href);
    }
    this.entries.delete(key);
    this.entries.set(key, snapshot);
    while (this.entries.size > this.capacity) this.entries.delete(this.entries.keys().next().value!);
  }

  /** Returns an entry only for the current browser history position; missing entries require fresh loading. */
  current(): T | undefined { return this.entries.get(history.state?.readerView); }
}
