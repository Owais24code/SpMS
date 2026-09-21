import { Injectable, signal } from '@angular/core';

export interface ConfirmRequest {
  readonly title: string;
  /** Must name the consequence, not just ask "are you sure?". */
  readonly consequence: string;
  readonly confirmLabel: string;
  readonly cancelLabel?: string;
  readonly tone?: 'default' | 'danger';
  /** When set, the user must type this exactly before confirming. */
  readonly typeToConfirm?: string;
}

@Injectable({ providedIn: 'root' })
export class ConfirmService {
  readonly request = signal<ConfirmRequest | null>(null);
  private resolver: ((ok: boolean) => void) | null = null;

  ask(req: ConfirmRequest): Promise<boolean> {
    this.request.set(req);
    return new Promise<boolean>((resolve) => { this.resolver = resolve; });
  }

  settle(ok: boolean): void {
    this.request.set(null);
    this.resolver?.(ok);
    this.resolver = null;
  }
}
