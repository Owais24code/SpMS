import { Injectable, signal } from '@angular/core';

export type ToastTone = 'success' | 'info' | 'warning' | 'danger';

export interface Toast {
  readonly id: number;
  readonly tone: ToastTone;
  readonly title: string;
  readonly body?: string;
  /** Stable code, shown so support can be given something searchable. */
  readonly code?: string;
  readonly undo?: () => void;
}

let seq = 0;

@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly toasts = signal<readonly Toast[]>([]);

  show(t: Omit<Toast, 'id'>, ms = 4200): number {
    const id = ++seq;
    this.toasts.update((list) => [...list, { ...t, id }]);
    if (ms > 0) setTimeout(() => this.dismiss(id), ms);
    return id;
  }

  success(title: string, body?: string, undo?: () => void) { return this.show({ tone: 'success', title, body, undo }); }
  info(title: string, body?: string)                        { return this.show({ tone: 'info', title, body }); }
  warn(title: string, body?: string, code?: string)         { return this.show({ tone: 'warning', title, body, code }, 6000); }
  error(title: string, body?: string, code?: string)        { return this.show({ tone: 'danger', title, body, code }, 7000); }

  dismiss(id: number): void {
    this.toasts.update((list) => list.filter((t) => t.id !== id));
  }
}
