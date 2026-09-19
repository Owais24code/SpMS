import { Component, ChangeDetectionStrategy } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { MESSAGE_RULES } from '../../core/data/workspace-data';

@Component({
  selector: 'app-messaging',
  standalone: true,
  imports: [PageHeader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Guests"
      title="Messaging rules"
      subtitle="Consent and quiet hours are checked again at send time, not just when the rule is saved."
    >
      <button type="button" class="btn btn--secondary">Send test</button>
      <button type="button" class="btn btn--primary">New rule</button>
    </app-page-header>

    <div class="grid grid--split">
      <div class="panel">
        <div class="panel__head"><span class="panel__title">Active rules</span></div>
        <div class="panel__body panel__body--flush">
          <div class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th scope="col">Rule</th>
                  <th scope="col">Trigger</th>
                  <th scope="col">Offset</th>
                  <th scope="col">Channel</th>
                  <th scope="col">Last sent</th>
                  <th scope="col">State</th>
                </tr>
              </thead>
              <tbody>
                @for (r of rules; track r.id) {
                  <tr>
                    <td>{{ r.name }}</td>
                    <td>{{ r.trigger }}</td>
                    <td class="numeric">{{ r.offset }}</td>
                    <td>{{ r.channel }}</td>
                    <td class="numeric">{{ r.lastSent }}</td>
                    <td>
                      <span class="badge" [class.badge--ok]="r.active" [class.badge--neutral]="!r.active">
                        {{ r.active ? 'On' : 'Paused' }}
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
          <div class="panel__head">
            <span class="panel__title">Preview</span>
            <span class="panel__hint">Sends 3:00pm property local</span>
          </div>
          <div class="panel__body">
            <div class="preview">
              <p class="preview__subject">Your visit to Riverside Spa tomorrow</p>
              <p>Hello <span class="tok">first name</span>,</p>
              <p>
                Your <span class="tok">service name</span> is booked for
                <span class="tok">date</span> at <span class="tok">time</span> with
                <span class="tok">provider</span>. Please arrive 20 minutes early.
              </p>
              <p>Riverside Spa · 716-992-3999</p>
            </div>
            <p class="subtle" style="margin-top: var(--space-4)">
              Company, service, date, time and location cannot be removed from this template.
              Health and employment fields are not offered as variables.
            </p>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">Consent and quiet hours</span></div>
          <div class="panel__body">
            <dl class="dl">
              <dt>Quiet hours</dt><dd class="numeric">9:00pm – 8:00am</dd>
              <dt>Marketing consent</dt><dd>Required for promotional rules</dd>
              <dt>Opt-outs</dt><dd>Propagated to the loyalty platform within a minute</dd>
              <dt>Test recipient</dt><dd>Synthetic address only</dd>
            </dl>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }

    .preview {
      padding: var(--space-5);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-md);
      background: var(--bg-subtle);
      font-size: var(--text-sm);
      line-height: var(--leading-loose);
      display: flex;
      flex-direction: column;
      gap: var(--space-3);

      &__subject { font-weight: var(--weight-bold); font-size: var(--text-base); }
    }

    .tok {
      padding: 1px 6px;
      border-radius: var(--radius-sm);
      background: var(--status-info-bg);
      color: var(--status-info-fg);
      font-size: var(--text-xs);
      font-weight: var(--weight-bold);
    }
  `],
})
export class Messaging {
  protected readonly rules = MESSAGE_RULES;
}
