import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { OpsApi } from '../../core/api/ops-api';
import { ToastService } from '../../core/services/toast.service';
import { AuthService } from '../../core/services/auth.service';
import type { MessageDto, TemplateDto } from '../../core/models/ops';
import type { ApiProblem } from '../../core/models/api-problem';

const VARIABLES = ['guestName', 'serviceName', 'startLocal', 'propertyName', 'confirmationNumber'];

/**
 * Guest messaging against the API (SEC-010, DEC-008). Templates are versioned
 * and approved by someone other than their author; a template may use only
 * the listed variables, so no message can carry intake answers, notes or
 * card details. The queue shows every message by channel and status — never
 * the address it went to.
 */
@Component({
  selector: 'app-messaging-live',
  standalone: true,
  imports: [PageHeader, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Guests" title="Messaging"
      subtitle="Confirmations, reminders and intake requests. Quiet hours and consent are applied when a message is scheduled." />

    <div class="grid grid--halves">
      <section class="panel">
        <div class="panel__head"><span class="panel__title">Templates</span></div>
        <div class="panel__body stack">
          <table class="table" data-testid="templates">
            <tbody>
              @for (t of templates(); track t.templateId) {
                <tr>
                  <td>{{ t.templateCode }} <span class="subtle">v{{ t.versionNumber }} · {{ t.channel }}</span></td>
                  <td class="subtle">{{ t.triggerEvent ? t.triggerEvent + ' ' + offset(t.offsetMinutes) : 'sent on demand' }}</td>
                  <td><span class="badge" [class.badge--ok]="t.status === 'Active'" [class.badge--warn]="t.status === 'Draft'">{{ t.status }}</span></td>
                  <td class="row row--end">
                    @if (t.status === 'Draft') { <button type="button" class="btn btn--ghost" [disabled]="t.authoredBy === me()" (click)="approve(t)">Approve</button> }
                    @if (t.status === 'Active' || t.status === 'Draft') { <button type="button" class="btn btn--ghost" (click)="retire(t)">Retire</button> }
                  </td>
                </tr>
              }
            </tbody>
          </table>
          <h3 class="h-sub">New template or version</h3>
          <div class="form-grid">
            <label>Code <input [(ngModel)]="draft.templateCode" name="tc" placeholder="booking-confirmed" /></label>
            <label>Channel <select [(ngModel)]="draft.channel" name="ch"><option>Email</option><option>Sms</option><option>WhatsApp</option></select></label>
            <label>Purpose
              <select [(ngModel)]="draft.purpose" name="pu">
                @for (p of purposes; track p) { <option [value]="p">{{ p }}</option> }
              </select>
            </label>
            <label>Subject <input [(ngModel)]="draft.subject" name="su" /></label>
            <label class="span-2">Body <textarea rows="3" [(ngModel)]="draft.bodyTemplate" name="bo" aria-label="Template body"></textarea></label>
          </div>
          <p class="subtle">Variables: @for (v of variables; track v) { <code>{{ v }}</code> }</p>
          <button type="button" class="btn btn--secondary" (click)="save()">Save draft</button>
        </div>
      </section>

      <section class="panel">
        <div class="panel__head"><span class="panel__title">Queue</span>
          <select [ngModel]="status()" (ngModelChange)="status.set($event); load()" name="st" aria-label="Status filter">
            <option value="">All</option><option>Scheduled</option><option>Sent</option><option>Delivered</option><option>Failed</option><option>Bounced</option>
          </select>
        </div>
        <div class="panel__body panel__body--flush">
          <table class="table" data-testid="queue">
            <tbody>
              @for (m of messages(); track m.messageId) {
                <tr>
                  <td>{{ templateName(m.templateId) }}</td><td>{{ m.channel }}</td>
                  <td class="numeric subtle">{{ local(m.sentUtc ?? m.sendAfterUtc) }}</td>
                  <td><span class="badge" [class.badge--ok]="m.status === 'Sent' || m.status === 'Delivered'" [class.badge--danger]="m.status === 'Failed' || m.status === 'Bounced'">{{ m.status }}</span>
                    @if (m.failureCode) { <span class="subtle">{{ m.failureCode }}</span> }</td>
                  <td>@if (m.status === 'Scheduled') { <button type="button" class="btn btn--ghost" (click)="cancel(m)">Cancel</button> }</td>
                </tr>
              } @empty { <tr><td class="subtle">Nothing in the queue.</td></tr> }
            </tbody>
          </table>
        </div>
      </section>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .grid--halves { display: grid; gap: var(--space-4); grid-template-columns: 1fr 1fr; }
    @media (max-width: 1000px) { .grid--halves { grid-template-columns: 1fr; } }
    label { display: grid; gap: var(--space-1); font-size: var(--text-sm); }
    .h-sub { font-size: var(--text-sm); margin: 0; }
    code { margin-right: var(--space-2); }
  `],
})
export class MessagingLive implements OnInit {
  private readonly api = inject(OpsApi);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);

  /** Shown as the placeholders an author types. */
  protected readonly variables = VARIABLES.map((v) => '{' + '{' + v + '}' + '}');
  protected readonly purposes = ['Confirmation', 'Reminder', 'Cancellation', 'IntakeRequest', 'Receipt', 'Waitlist', 'Marketing', 'Transactional'];
  protected readonly templates = signal<readonly TemplateDto[]>([]);
  protected readonly messages = signal<readonly MessageDto[]>([]);
  protected readonly status = signal('');
  protected readonly me = computed(() => this.auth.user()?.principalId ?? null);
  protected draft = { templateCode: '', channel: 'Email', purpose: 'Transactional', subject: '', bodyTemplate: '' };

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> {
    try {
      const [t, m] = await Promise.all([this.api.templates(), this.api.messages(this.status() || undefined)]);
      this.templates.set(t); this.messages.set(m);
    } catch (e) { this.fail(e); }
  }

  protected offset(m: number | null): string {
    if (m === null) return '';
    const h = Math.abs(m) / 60;
    return m === 0 ? 'at once' : `${h >= 1 ? `${h}h` : `${Math.abs(m)}m`} ${m < 0 ? 'before' : 'after'}`;
  }
  protected local(utc: string): string { return new Date(utc).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }); }
  protected templateName(id: string): string { return this.templates().find((t) => t.templateId === id)?.templateCode ?? '—'; }

  protected async save(): Promise<void> {
    await this.run(async () => {
      const t = await this.api.draftTemplate({ ...this.draft, subject: this.draft.subject || null });
      this.toast.success(`Draft v${t.versionNumber} saved`, 'Someone other than you approves it.');
      this.draft = { templateCode: '', channel: 'Email', purpose: 'Transactional', subject: '', bodyTemplate: '' };
    });
  }
  protected async approve(t: TemplateDto): Promise<void> {
    await this.run(async () => { await this.api.approveTemplate(t.templateId, t.rowVersion); this.toast.success('Template approved', `${t.templateCode} v${t.versionNumber} is live.`); });
  }
  protected async retire(t: TemplateDto): Promise<void> { await this.run(async () => { await this.api.retireTemplate(t.templateId, t.rowVersion); }); }
  protected async cancel(m: MessageDto): Promise<void> { await this.run(async () => { await this.api.cancelMessage(m.messageId, m.rowVersion); }); }

  private async run(work: () => Promise<void>): Promise<void> {
    try { await work(); await this.load(); } catch (e) { this.fail(e); }
  }
  private fail(e: unknown): void {
    const p = e as ApiProblem;
    if (p?.code === 'AUTHORIZATION_DENIED') this.toast.warn('Not permitted', p.detail ?? p.title, p.code);
    else this.toast.error('That did not work', p?.detail ?? p?.title ?? String(e), p?.correlationId);
  }
}
