import { Component, ChangeDetectionStrategy } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatCard } from '../../shared/components/stat-card/stat-card';
import { STOCK } from '../../core/data/workspace-data';

@Component({
  selector: 'app-inventory',
  standalone: true,
  imports: [PageHeader, StatCard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Operations"
      title="Inventory and readiness"
      subtitle="Forecast demand shows the assumptions behind it, so you can judge whether to trust the number."
    >
      <button type="button" class="btn btn--secondary">Adjust stock</button>
      <button type="button" class="btn btn--primary">Request wash</button>
    </app-page-header>

    <div class="stack">
      <div class="grid grid--kpi">
        <app-stat-card label="Rooms ready" value="9 / 12" delta="3 in turnover" hint="two blocked for maintenance" />
        <app-stat-card label="Clean linen" value="626" delta="-12%" trend="down" tone="negative" hint="against today's forecast" />
        <app-stat-card label="In wash" value="285" hint="next cycle back 4:40pm" />
        <app-stat-card label="Shortfall risk" value="1 line" delta="Hand towels" trend="up" tone="negative" hint="low confidence forecast" />
      </div>

      <div class="panel">
        <div class="panel__head">
          <span class="panel__title">Stock and forecast</span>
          <span class="panel__hint">Horizon: next 24 hours · refreshed 08:40</span>
        </div>
        <div class="panel__body panel__body--flush">
          <div class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th scope="col">Item</th>
                  <th scope="col">Clean</th>
                  <th scope="col">Soiled</th>
                  <th scope="col">In wash</th>
                  <th scope="col">Reserved</th>
                  <th scope="col">Forecast need</th>
                  <th scope="col">Cover</th>
                  <th scope="col">Confidence</th>
                </tr>
              </thead>
              <tbody>
                @for (s of stock; track s.item) {
                  <tr>
                    <td>{{ s.item }}</td>
                    <td class="numeric">{{ s.clean }}</td>
                    <td class="numeric">{{ s.soiled }}</td>
                    <td class="numeric">{{ s.inWash }}</td>
                    <td class="numeric">{{ s.reserved }}</td>
                    <td class="numeric">{{ s.forecast }}</td>
                    <td style="min-width: 140px">
                      <div class="meter"
                           [class.meter--warn]="cover(s) < 100 && cover(s) >= 70"
                           [class.meter--danger]="cover(s) < 70">
                        <span [style.width.%]="cover(s) > 100 ? 100 : cover(s)"></span>
                      </div>
                      <span class="subtle numeric">{{ cover(s) }}%</span>
                    </td>
                    <td>
                      <span class="badge"
                            [class.badge--ok]="s.confidence === 'high'"
                            [class.badge--warn]="s.confidence === 'medium'"
                            [class.badge--danger]="s.confidence === 'low'">
                        {{ s.confidence }}
                      </span>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      </div>

      <div class="grid grid--halves">
        <div class="panel">
          <div class="panel__head"><span class="panel__title">Recommended action</span></div>
          <div class="panel__body stack">
            <p><strong>Move 120 hand towels into the 2:10pm wash cycle.</strong></p>
            <p class="subtle">
              Based on 86 booked treatments, average consumption of 3.4 per treatment, and a 95-minute
              cycle time. Confidence is low because Saturday consumption has varied ±18% recently.
            </p>
            <div class="row">
              <button type="button" class="btn btn--primary">Approve wash</button>
              <button type="button" class="btn btn--ghost">Transfer from Lakeside</button>
            </div>
            <p class="subtle">A person approves every action. Nothing is ordered automatically.</p>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">Room readiness</span></div>
          <div class="panel__body">
            <ul class="rooms">
              @for (r of rooms; track r.name) {
                <li>
                  <span class="rooms__name">{{ r.name }}</span>
                  <span class="badge"
                        [class.badge--ok]="r.state === 'Ready'"
                        [class.badge--warn]="r.state === 'Turnover'"
                        [class.badge--neutral]="r.state === 'Blocked'">{{ r.state }}</span>
                  <span class="subtle">{{ r.note }}</span>
                </li>
              }
            </ul>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }

    .rooms {
      list-style: none;
      display: flex;
      flex-direction: column;

      li {
        display: flex;
        align-items: center;
        gap: var(--space-3);
        padding: var(--space-3) 0;
        border-bottom: 1px solid var(--border-subtle);

        &:last-child { border-bottom: 0; }
      }

      &__name { font-weight: var(--weight-bold); font-size: var(--text-sm); width: 84px; }

      .subtle { margin-inline-start: auto; }
    }
  `],
})
export class Inventory {
  protected readonly stock = STOCK;

  protected readonly rooms = [
    { name: 'Suite 1', state: 'Ready',    note: 'checked 08:12' },
    { name: 'Suite 2', state: 'Blocked',  note: 'plumbing — until 2pm' },
    { name: 'Suite 3', state: 'Ready',    note: 'checked 08:31' },
    { name: 'Room 2',  state: 'Turnover', note: 'linen swap in progress' },
    { name: 'Room 4',  state: 'Ready',    note: 'checked 09:02' },
  ];

  protected cover(s: { clean: number; inWash: number; forecast: number }): number {
    return Math.round(((s.clean + s.inWash) / s.forecast) * 100);
  }
}
