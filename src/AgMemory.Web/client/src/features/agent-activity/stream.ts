import { reduceEvents, safeEvent, compareSequence, type ActivityEvent, type Cursor } from './model.js';

/** Owns exactly one connection and bounded reducer; ACK follows synchronous application of a valid packet. */
export class ActivityStream {
  events: ActivityEvent[] = [];
  private socket?: WebSocket;
  private timer?: ReturnType<typeof setTimeout>;
  private cursor?: Cursor;
  private active = false;
  constructor(private readonly changed: (events: ActivityEvent[], state: string, reset?: boolean) => void) {}
  setActive(active: boolean): void {
    if (this.active === active) return;
    this.active = active;
    if (active) this.connect();
    else { clearTimeout(this.timer); this.timer = undefined; this.socket?.close(); this.socket = undefined; this.changed(this.events, 'Отключено'); }
  }
  reset(): void { this.cursor = undefined; this.events = []; this.changed(this.events, 'История очищена', true); this.socket?.close(); }
  dispose(): void { this.setActive(false); }
  private connect(): void {
    if (!this.active || this.socket) return;
    this.changed(this.events, 'Подключение…');
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const socket = new WebSocket(`${scheme}//${location.host}/api/agent-activity/stream`);
    this.socket = socket;
    socket.onopen = () => socket.send(JSON.stringify({ type: 'subscribe', cursor: this.cursor }));
    socket.onmessage = message => {
      if (this.socket !== socket || typeof message.data !== 'string' || new TextEncoder().encode(message.data).byteLength > 65_536) return;
      try {
        const packet = JSON.parse(message.data);
        if (packet.type === 'resetRequired') { this.cursor = undefined; this.events = []; this.changed(this.events, 'История обновлена', true); socket.close(); return; }
        if (!['snapshot', 'events'].includes(packet.type) || typeof packet.messageId !== 'string' || !Array.isArray(packet.events) || packet.events.length > 128 ||
            !validCursor(packet.cursor) || packet.journalEpoch !== packet.cursor.journalEpoch) { socket.close(); return; }
        if (packet.events.some((value: unknown) => { const event = safeEvent(value); return !event || event.journalEpoch !== packet.journalEpoch || compareSequence(event.sequence, packet.cursor.sequence) > 0; }) ||
            packet.type === 'events' && this.cursor?.journalEpoch === packet.journalEpoch && compareSequence(packet.cursor.sequence, this.cursor!.sequence) < 0) { socket.close(); return; }
        const reset = packet.type === 'snapshot' && this.cursor?.journalEpoch !== packet.journalEpoch;
        if (reset) this.events = [];
        this.events = reduceEvents(this.events, packet.events);
        this.cursor = packet.cursor;
        const health = packet.health?.status === 'degraded' || typeof packet.health?.dropped === 'number' && packet.health.dropped > 0 ? 'Журнал работает с ограничениями' : 'В реальном времени';
        this.changed(this.events, packet.omittedCount ? `${health} · часть истории доступна ниже` : health, reset);
        socket.send(JSON.stringify({ type: 'ack', messageId: packet.messageId, cursor: this.cursor }));
      } catch { socket.close(); }
    };
    socket.onclose = () => {
      if (this.socket !== socket) return;
      this.socket = undefined;
      if (this.active) { this.changed(this.events, 'Переподключение…'); this.timer = setTimeout(() => this.connect(), 1500); }
    };
    socket.onerror = () => socket.close();
  }
}

function validCursor(value: unknown): value is Cursor {
  const cursor = value as Cursor | null;
  return !!cursor && typeof cursor.journalEpoch === 'string' && cursor.journalEpoch.length >= 1 && cursor.journalEpoch.length <= 128 &&
    typeof cursor.sequence === 'string' && /^(0|[1-9][0-9]{0,19})$/.test(cursor.sequence);
}
