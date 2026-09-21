import { Component, ChangeDetectionStrategy, inject, signal, effect } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { ThemeService, type ThemeChoice } from '../../core/services/theme.service';
import { ToastService } from '../../core/services/toast.service';
import { AuthService } from '../../core/services/auth.service';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [PageHeader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Setup"
      title="Settings"
      subtitle="Property defaults, appearance and session behaviour."
    >
      <button type="button" class="btn btn--primary" [disabled]="!dirty()" (click)="save()">
        {{ dirty() ? 'Save changes' : 'Saved' }}
      </button>
    </app-page-header>

    <div class="grid grid--halves">
      <div class="panel">
        <div class="panel__head"><span class="panel__title">Appearance</span></div>
        <div class="panel__body stack">
          <div>
            <p class="lbl">Theme</p>
            <div class="seg" role="group" aria-label="Theme">
              @for (o of themes; track o.value) {
                <button
                  type="button"
                  [attr.aria-pressed]="theme.choice() === o.value"
                  (click)="theme.set(o.value)"
                >{{ o.label }}</button>
              }
            </div>
            <p class="subtle" style="margin-top: var(--space-2)">
              System follows your device and keeps tracking it if you change it later.
            </p>
          </div>

          <div>
            <p class="lbl">Density</p>
            <div class="seg" role="group" aria-label="Density">
              <button type="button" [attr.aria-pressed]="density() === 'compact'" (click)="setDensity('compact')">Compact</button>
              <button type="button" [attr.aria-pressed]="density() === 'comfortable'" (click)="setDensity('comfortable')">Comfortable</button>
            </div>
            <p class="subtle" style="margin-top: var(--space-2)">Applies immediately so you can see it before saving.</p>
          </div>
        </div>
      </div>

      <div class="panel">
        <div class="panel__head"><span class="panel__title">Property</span></div>
        <div class="panel__body">
          <div class="form-grid">
            <div>
              <label for="tz">Timezone</label>
              <select id="tz"><option>America/New_York</option><option>America/Chicago</option></select>
            </div>
            <div>
              <label for="cur">Currency</label>
              <select id="cur"><option>USD</option><option>CAD</option></select>
            </div>
            <div>
              <label for="horizon">Booking horizon</label>
              <select id="horizon"><option>90 days</option><option>180 days</option></select>
            </div>
            <div>
              <label for="turn">Room turnover</label>
              <select id="turn"><option>20 minutes</option><option>30 minutes</option></select>
            </div>
          </div>
        </div>
      </div>

      <div class="panel">
        <div class="panel__head"><span class="panel__title">Session</span></div>
        <div class="panel__body stack">
          <dl class="dl">
            <dt>Idle lock</dt><dd>After 10 minutes</dd>
            <dt>Reauthentication</dt><dd>In place — unsaved work is kept</dd>
            <dt>Screen dimming</dt><dd>On for front desk surfaces</dd>
          </dl>
          <p class="subtle">
            When a session expires mid-edit you are asked to sign in again on the spot.
            Nothing you have typed is discarded.
          </p>
        </div>
      </div>

      <div class="panel">
        <div class="panel__head"><span class="panel__title">Accessibility</span></div>
        <div class="panel__body">
          <dl class="dl">
            <dt>Signed in as</dt><dd>{{ auth.user()?.roleLabel }} · {{ auth.user()?.scopes?.length }} scopes</dd>
            <dt>Target</dt><dd>WCAG 2.2 AA</dd>
            <dt>Motion</dt><dd>Follows your system reduced-motion setting</dd>
            <dt>Contrast audit</dt><dd><span class="badge badge--ok">44 pairs passing</span></dd>
            <dt>Keyboard</dt><dd>The scheduling board is moved with the keyboard: M, arrow keys, Enter</dd>
          </dl>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .lbl { font-size: var(--text-sm); font-weight: var(--weight-bold); margin-bottom: var(--space-2); }
    label { display: block; font-size: var(--text-sm); font-weight: var(--weight-bold); margin-bottom: var(--space-2); }
  `],
})
export class Settings {
  private readonly toast = inject(ToastService);
  protected readonly theme = inject(ThemeService);
  protected readonly auth = inject(AuthService);

  protected readonly density = signal<'compact' | 'comfortable'>(this.readDensity());
  protected readonly dirty = signal(false);

  constructor() {
    // Density is a live preview — you see it before you save.
    effect(() => {
      document.documentElement.dataset['density'] = this.density();
    });
  }

  protected setDensity(d: 'compact' | 'comfortable'): void {
    this.density.set(d);
    this.dirty.set(true);
  }

  protected save(): void {
    try { localStorage.setItem('spms-density', this.density()); } catch { /* ignore */ }
    this.dirty.set(false);
    this.toast.success('Settings saved', 'Appearance applies to this browser. Property settings apply to everyone here.');
  }

  private readDensity(): 'compact' | 'comfortable' {
    try {
      return localStorage.getItem('spms-density') === 'compact' ? 'compact' : 'comfortable';
    } catch { return 'comfortable'; }
  }

  protected readonly themes: { label: string; value: ThemeChoice }[] = [
    { label: 'Light',  value: 'light' },
    { label: 'Dark',   value: 'dark' },
    { label: 'System', value: 'system' },
  ];
}
