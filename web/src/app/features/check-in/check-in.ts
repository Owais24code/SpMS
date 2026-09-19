import { Component, ChangeDetectionStrategy, signal, computed } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { HighlightDirective } from '../../shared/directives/highlight.directive';
import { ARRIVALS } from '../../core/data/workspace-data';

@Component({
  selector: 'app-check-in',
  standalone: true,
  imports: [PageHeader, StatePanel, HighlightDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Front desk"
      title="Arrivals and check-in"
      subtitle="Readiness shows completion status only. Intake answers are never served to this screen."
    >
      <button type="button" class="btn btn--secondary">Scan credential</button>
      <button type="button" class="btn btn--primary">Walk-in</button>
    </app-page-header>

    <div class="stack">
      <div class="toolbar">
        <input
          class="input" type="search" placeholder="Guest alias or booking code"
          aria-label="Search arrivals"
          [value]="term()" (input)="term.set($any($event.target).value)"
        />
        <button type="button" class="chip" aria-pressed="true">Next 2 hours</button>
        <button type="button" class="chip">Not ready</button>
        <span class="row row--end subtle">Screen dimmed after 60s to protect guest detail</span>
      </div>

      @if (rows().length === 0) {
        <app-state-panel state="empty" />
      } @else {
        <div class="panel">
          <div class="panel__body panel__body--flush">
            <div class="table-wrap">
              <table class="table">
                <thead>
                  <tr>
                    <th scope="col">Time</th>
                    <th scope="col">Guest</th>
                    <th scope="col">Service</th>
                    <th scope="col">Readiness</th>
                    <th scope="col">Locker</th>
                    <th scope="col">Pager</th>
                    <th scope="col"><span class="visually-hidden">Action</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (a of rows(); track a.id) {
                    <tr>
                      <td class="numeric">{{ a.time }}</td>
                      <td><span [appHighlight]="term()">{{ a.guestAlias }}</span></td>
                      <td>{{ a.service }}</td>
                      <td>
                        <span class="row">
                          <span class="badge" [class.badge--ok]="a.formsComplete" [class.badge--warn]="!a.formsComplete">Forms</span>
                          <span class="badge" [class.badge--ok]="a.depositSettled" [class.badge--warn]="!a.depositSettled">Deposit</span>
                          <span class="badge" [class.badge--ok]="a.roomReady" [class.badge--neutral]="!a.roomReady">Room</span>
                        </span>
                      </td>
                      <td class="numeric">{{ a.locker ?? '—' }}</td>
                      <td class="numeric">{{ a.pager ?? '—' }}</td>
                      <td>
                        <button type="button" class="btn btn--ghost" [disabled]="!ready(a)">
                          {{ ready(a) ? 'Check in' : 'Blocked' }}
                        </button>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </div>
        </div>
      }

      <div class="grid grid--halves">
        <div class="panel">
          <div class="panel__head"><span class="panel__title">Robe and slipper preferences</span></div>
          <div class="panel__body">
            <dl class="dl">
              <dt>Robe size</dt><dd>Medium — in stock</dd>
              <dt>Slipper size</dt><dd>41 — substitute offered</dd>
              <dt>Consent to substitute</dt><dd><span class="badge badge--ok">Given</span></dd>
            </dl>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">Late arrival</span></div>
          <div class="panel__body">
            <app-state-panel
              state="conflict"
              title="Guest 4824 is 18 minutes late"
              body="The safe minimum for Hot stone 60 is 50 minutes. Offer the 45-minute variant rather than shortening the booked service."
            />
          </div>
        </div>
      </div>
    </div>
  `,
})
export class CheckIn {
  protected readonly term = signal('');

  protected readonly rows = computed(() => {
    const t = this.term().trim().toLowerCase();
    if (!t) return ARRIVALS;
    return ARRIVALS.filter((a) =>
      a.guestAlias.toLowerCase().includes(t) || a.service.toLowerCase().includes(t),
    );
  });

  protected ready(a: { formsComplete: boolean; depositSettled: boolean; roomReady: boolean }): boolean {
    return a.formsComplete && a.depositSettled && a.roomReady;
  }
}
