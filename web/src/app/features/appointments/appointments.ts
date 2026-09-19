import { Component, ChangeDetectionStrategy } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { DurationPipe } from '../../shared/pipes/duration.pipe';
import { APPOINTMENTS } from '../../core/data/workspace-data';

@Component({
  selector: 'app-appointments',
  standalone: true,
  imports: [PageHeader, DurationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Guests"
      title="Appointments"
      subtitle="Changing a booking may affect price, deposit and the intake you already completed. Those effects are shown before you confirm."
    >
      <button type="button" class="btn btn--secondary">Download receipt</button>
      <button type="button" class="btn btn--primary">Change booking</button>
    </app-page-header>

    <div class="grid grid--split">
      <div class="panel">
        <div class="panel__head">
          <span class="panel__title">All appointments</span>
          <div class="panel__actions">
            <button type="button" class="chip" aria-pressed="true">Upcoming</button>
            <button type="button" class="chip">Past</button>
          </div>
        </div>
        <div class="panel__body panel__body--flush">
          <div class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th scope="col">Ref</th>
                  <th scope="col">Start</th>
                  <th scope="col">Service</th>
                  <th scope="col">Length</th>
                  <th scope="col">Provider</th>
                  <th scope="col">Status</th>
                </tr>
              </thead>
              <tbody>
                @for (a of rows; track a.id) {
                  <tr>
                    <td class="numeric">{{ a.id }}</td>
                    <td class="numeric">{{ a.start }}</td>
                    <td>{{ a.service }}</td>
                    <td class="numeric">{{ a.durationMin | duration }}</td>
                    <td>{{ a.provider }}</td>
                    <td>
                      <span class="badge"
                            [class.badge--ok]="a.state === 'complete'"
                            [class.badge--info]="a.state === 'booked'"
                            [class.badge--warn]="a.state === 'conflict'"
                            [class.badge--neutral]="a.state === 'in-progress'">
                        {{ a.state }}
                      </span>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      </div>

      <div class="stack">
        <div class="panel">
          <div class="panel__head"><span class="panel__title">Cancellation policy</span></div>
          <div class="panel__body stack">
            <dl class="dl">
              <dt>Free until</dt><dd class="numeric">17 Sep, 1:00pm</dd>
              <dt>After that</dt><dd class="numeric">50% of $240.00</dd>
              <dt>Deposit held</dt><dd class="numeric">$60.00</dd>
            </dl>
            <button type="button" class="btn btn--secondary">Cancel appointment</button>
            <p class="subtle">You will be asked to confirm, and we will email a receipt either way.</p>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">Forms and waivers</span></div>
          <div class="panel__body">
            <ul class="timeline">
              <li class="is-done">
                <p class="timeline__what">Health intake</p>
                <p class="timeline__note">Completed 12 Sep · still valid</p>
              </li>
              <li class="is-active">
                <p class="timeline__what">Hot stone waiver</p>
                <p class="timeline__note">Needed again — your service changed</p>
              </li>
              <li>
                <p class="timeline__what">Arrival guidance</p>
                <p class="timeline__note">Sends 3 hours before</p>
              </li>
            </ul>
          </div>
        </div>
      </div>
    </div>
  `,
})
export class Appointments {
  protected readonly rows = APPOINTMENTS;
}
