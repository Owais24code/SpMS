import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { SchedulingApi } from '../../core/api/scheduling-api';
import { GuestsApi } from '../../core/api/guests-api';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import type { ApiProblem } from '../../core/models/api-problem';
import type { AppointmentDto } from '../../core/models/api';
import type { IntakeSummaryDto, NoteDto } from '../../core/models/guests';

/**
 * The provider tablet against the API: today's appointments assigned to the
 * signed-in provider, each with its minimum-necessary intake summary (read,
 * then acknowledged — the form locks), the treatment's own steps, and its
 * notes. Everything restricted is served only to the assigned provider.
 */
@Component({
  selector: 'app-treatments-live',
  standalone: true,
  imports: [PageHeader, StatePanel, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Provider" title="My treatments"
      subtitle="Only your own appointments. The intake summary is the minimum you need; the full form stays with the guest." >
      <button type="button" class="btn btn--secondary" (click)="load()">Refresh</button>
    </app-page-header>

    <div class="grid grid--split">
      <div class="panel">
        <div class="panel__head"><span class="panel__title">Today</span></div>
        <div class="panel__body panel__body--flush">
          @if (problem(); as p) {
            <app-state-panel [state]="p.status === 403 ? 'denied' : 'error'" [title]="p.title" [body]="p.detail ?? ''" />
          } @else if (mine().length === 0) {
            <app-state-panel state="empty" title="Nothing assigned to you today" body="Appointments appear here once you are the provider on them." />
          } @else {
            <ul class="hits">
              @for (a of mine(); track a.appointmentId) {
                <li><button type="button" class="hit" [class.is-picked]="current()?.appointmentId === a.appointmentId" (click)="open(a)">
                  <strong class="numeric">{{ a.startLocal.slice(11) }} · {{ a.serviceName }}</strong>
                  <span class="subtle">{{ a.guestAlias }} · {{ a.status }}</span>
                </button></li>
              }
            </ul>
          }
        </div>
      </div>

      <div class="stack">
        @if (current(); as a) {
          <div class="panel">
            <div class="panel__head"><span class="panel__title">{{ a.guestAlias }} · {{ a.serviceName }}</span>
              <span class="badge badge--info">{{ a.status }}</span></div>
            <div class="panel__body stack">
              <div class="row">
                @for (t of steps(a); track t) {
                  <button type="button" class="btn btn--secondary" (click)="move(a, t)" [disabled]="busy()">{{ label(t) }}</button>
                }
              </div>
            </div>
          </div>

          <div class="panel">
            <div class="panel__head"><span class="panel__title">Intake summary</span>
              @if (summary()?.requiresReview) { <span class="badge badge--warn">Needs review</span> }</div>
            <div class="panel__body stack">
              @if (summary(); as s) {
                @if (s.items.length === 0) {
                  <p class="subtle">The guest has not submitted their form yet ({{ s.status }}).</p>
                } @else {
                  <dl class="dl">
                    @for (i of s.items; track i.key) {
                      <dt>{{ i.label }}</dt><dd [class.is-review]="i.review && i.answer === true">{{ show(i.answer) }}</dd>
                    }
                  </dl>
                  @if (s.acknowledgedUtc) {
                    <p class="subtle">Acknowledged {{ time(s.acknowledgedUtc) }}. The form is locked.</p>
                  } @else {
                    <button type="button" class="btn btn--primary" (click)="acknowledge(a)" [disabled]="busy()">I have read this</button>
                  }
                }
              } @else {
                <p class="subtle">This service has no intake form.</p>
              }
            </div>
          </div>

          <div class="panel">
            <div class="panel__head"><span class="panel__title">Treatment notes</span></div>
            <div class="panel__body stack">
              @for (n of notes(); track n.noteId) {
                <div class="note" [class.is-superseded]="n.superseded">
                  <p class="subtle">{{ time(n.authoredUtc) }}{{ n.amendmentReason ? ' · amended: ' + n.amendmentReason : '' }}{{ n.superseded ? ' · superseded' : '' }}</p>
                  <p>{{ n.content }}</p>
                </div>
              } @empty { <p class="subtle">No notes yet.</p> }
              <label for="note">New note</label>
              <textarea id="note" class="input" rows="4" [(ngModel)]="draft"></textarea>
              <button type="button" class="btn btn--secondary" (click)="write(a)" [disabled]="!draft.trim() || busy()">Save note</button>
              <p class="subtle">Notes are never edited. A correction is a new note that supersedes the old one, with a reason.</p>
            </div>
          </div>
        } @else {
          <app-state-panel state="empty" title="Choose an appointment" body="Its intake summary and notes open here." />
        }
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .hits { list-style: none; margin: 0; padding: 0; }
    .hit { display: grid; gap: 2px; width: 100%; text-align: start; padding: var(--space-3); border: 0; border-bottom: 1px solid var(--border-subtle);
      background: transparent; color: var(--fg-default); font: inherit; cursor: pointer; }
    .hit:hover, .hit.is-picked { background: var(--bg-subtle); }
    .note { padding: var(--space-2) var(--space-3); border-radius: var(--radius-sm); background: var(--bg-muted); }
    .note.is-superseded { opacity: 0.6; }
    .is-review { color: var(--status-warning-fg); font-weight: var(--weight-bold); }
    label { font-size: var(--text-sm); font-weight: var(--weight-bold); }
  `],
})
export class TreatmentsLive implements OnInit {
  private readonly api = inject(SchedulingApi);
  private readonly guests = inject(GuestsApi);
  private readonly store = inject(WorkspaceStore);
  private readonly toast = inject(ToastService);

  protected readonly mine = signal<readonly AppointmentDto[]>([]);
  protected readonly current = signal<AppointmentDto | null>(null);
  protected readonly summary = signal<IntakeSummaryDto | null>(null);
  protected readonly notes = signal<readonly NoteDto[]>([]);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly busy = signal(false);
  protected draft = '';

  ngOnInit(): void { void this.load(); }

  protected async load(): Promise<void> {
    try {
      const page = await this.api.mine(this.store.boardDate());
      this.mine.set(page.items);
      this.problem.set(null);
    } catch (e) {
      this.problem.set(e as ApiProblem);
    }
  }

  protected async open(a: AppointmentDto): Promise<void> {
    this.current.set(a);
    this.summary.set(null);
    this.notes.set([]);
    try { this.summary.set(await this.guests.intakeSummary(a.appointmentId)); } catch { this.summary.set(null); }
    try { this.notes.set(await this.guests.notes(a.appointmentId)); } catch (e) { this.fail(e); }
  }

  protected steps(a: AppointmentDto): readonly string[] {
    return a.allowedTransitions.filter((t) => t === 'Ready' || t === 'InService' || t === 'Completed');
  }

  protected label(t: string): string {
    return t === 'Ready' ? 'Guest is ready' : t === 'InService' ? 'Start treatment' : 'Complete';
  }

  protected show(v: unknown): string {
    return v === true ? 'Yes' : v === false ? 'No' : String(v ?? '—');
  }

  protected time(utc: string): string {
    return new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(new Date(utc));
  }

  protected async move(a: AppointmentDto, to: string): Promise<void> {
    if (to === 'Completed' && this.summary()?.items.length && !this.summary()?.acknowledgedUtc) {
      this.toast.warn('Acknowledge the intake summary first', 'You must have read the minimum-necessary summary before completing.');
      return;
    }
    await this.run(async () => {
      const next = await this.api.transition(a.appointmentId, a.rowVersion, { to });
      this.current.set(next);
      this.mine.update((l) => l.map((x) => (x.appointmentId === next.appointmentId ? next : x)));
      this.toast.success(this.label(to));
    });
  }

  protected async acknowledge(a: AppointmentDto): Promise<void> {
    await this.run(async () => { this.summary.set(await this.guests.acknowledgeIntake(a.appointmentId)); this.toast.success('Intake acknowledged', 'Recorded against your name.'); });
  }

  protected async write(a: AppointmentDto): Promise<void> {
    await this.run(async () => {
      await this.guests.writeNote(a.appointmentId, this.draft.trim(), 'SOAP');
      this.draft = '';
      this.notes.set(await this.guests.notes(a.appointmentId));
      this.toast.success('Note saved');
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try { await work(); } catch (e) { this.fail(e); } finally { this.busy.set(false); }
  }

  private fail(e: unknown): void {
    const p = e as ApiProblem;
    this.toast.error('That did not work', p.detail ?? p.title, p.correlationId);
  }
}
