import { Injectable, inject, signal } from '@angular/core';
import { environment } from '../../../environments/environment';
import { AuthService } from './auth.service';

export interface BoardChangeEvent { readonly kind: 'appointment' | 'room' | 'visit' | 'order' | string; readonly id: string; readonly occurredUtc: string; }

/**
 * The live board's connection (GET /board/stream, server-sent events).
 * Read with fetch rather than EventSource because EventSource cannot send
 * the Authorization or dev-login headers. A dropped connection reconnects
 * with backoff; a 401/403 means this role has no board, and it stops.
 * The events carry no content — only "something of this kind changed" —
 * and the store reloads what it shows through the ordinary reads.
 */
@Injectable({ providedIn: 'root' })
export class BoardStream {
  private readonly auth = inject(AuthService);
  private controller: AbortController | null = null;

  readonly connected = signal(false);

  start(onChange: (e: BoardChangeEvent) => void): void {
    this.stop();
    const controller = new AbortController();
    this.controller = controller;
    void this.run(onChange, controller.signal);
  }

  stop(): void {
    this.controller?.abort();
    this.controller = null;
    this.connected.set(false);
  }

  private async run(onChange: (e: BoardChangeEvent) => void, signal: AbortSignal): Promise<void> {
    let backoff = 1000;
    while (!signal.aborted) {
      try {
        const headers = await this.auth.requestHeaders();
        const res = await fetch(`${environment.apiBaseUrl}/board/stream`, { headers, signal, cache: 'no-store' });
        if (res.status === 401 || res.status === 403) { this.connected.set(false); return; }
        if (!res.ok || !res.body) throw new Error(`board stream ${res.status}`);
        this.connected.set(true);
        backoff = 1000;
        const reader = res.body.pipeThrough(new TextDecoderStream()).getReader();
        let buffer = '';
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buffer += value;
          let end: number;
          while ((end = buffer.indexOf('\n\n')) >= 0) {
            const frame = buffer.slice(0, end);
            buffer = buffer.slice(end + 2);
            const event = /^event: (.*)$/m.exec(frame)?.[1];
            const data = /^data: (.*)$/m.exec(frame)?.[1];
            if (event === 'change' && data) {
              try { onChange(JSON.parse(data) as BoardChangeEvent); } catch { /* a malformed frame is skipped */ }
            }
          }
        }
      } catch {
        if (signal.aborted) return;
      }
      this.connected.set(false);
      await new Promise((r) => setTimeout(r, backoff));
      backoff = Math.min(backoff * 2, 30_000);
    }
  }
}
