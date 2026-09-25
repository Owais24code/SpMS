import { Injectable, signal, inject, DestroyRef } from '@angular/core';
import { Router, NavigationStart } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';

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

  constructor() {
    // A confirm left open across a navigation would sit over the next screen
    // with its scrim swallowing every click, and its promise would never
    // settle. Navigating away is a decline.
    inject(Router).events
      .pipe(filter((e) => e instanceof NavigationStart), takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => { if (this.request()) this.settle(false); });
  }

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
