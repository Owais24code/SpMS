import { Injectable, signal, effect, computed } from '@angular/core';

export type ThemeChoice = 'light' | 'dark' | 'system';

const STORAGE_KEY = 'spms-theme';

/**
 * Owns the light/dark choice for the whole app.
 *
 * Three states, not two: an explicit `light`/`dark` pins the theme, while
 * `system` defers to the OS and keeps tracking it if the user changes it
 * mid-session. Storage is best-effort — a blocked or cleared store simply
 * falls back to `system` rather than breaking the toggle.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly media =
    typeof window !== 'undefined' && window.matchMedia
      ? window.matchMedia('(prefers-color-scheme: dark)')
      : null;

  private readonly systemPrefersDark = signal(this.media?.matches ?? false);

  readonly choice = signal<ThemeChoice>(this.readStored());

  /** What is actually on screen right now. */
  readonly resolved = computed<'light' | 'dark'>(() => {
    const c = this.choice();
    return c === 'system' ? (this.systemPrefersDark() ? 'dark' : 'light') : c;
  });

  constructor() {
    this.media?.addEventListener('change', (e) => this.systemPrefersDark.set(e.matches));

    effect(() => {
      const choice = this.choice();
      const resolved = this.resolved();
      if (typeof document === 'undefined') return;

      if (choice === 'system') {
        document.documentElement.removeAttribute('data-theme');
      } else {
        document.documentElement.setAttribute('data-theme', choice);
      }
      document.documentElement.style.colorScheme = resolved;

      try {
        if (choice === 'system') localStorage.removeItem(STORAGE_KEY);
        else localStorage.setItem(STORAGE_KEY, choice);
      } catch {
        /* storage unavailable — theme still applies for this session */
      }
    });
  }

  /** Flips to the opposite of what is currently shown. */
  toggle(): void {
    this.choice.set(this.resolved() === 'dark' ? 'light' : 'dark');
  }

  set(choice: ThemeChoice): void {
    this.choice.set(choice);
  }

  private readStored(): ThemeChoice {
    try {
      const v = localStorage.getItem(STORAGE_KEY);
      if (v === 'light' || v === 'dark') return v;
    } catch {
      /* ignore */
    }
    return 'system';
  }
}
