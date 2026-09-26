import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { OperationsApi } from '../../core/api/operations-api';
import { AuthService } from '../../core/services/auth.service';
import { ToastService } from '../../core/services/toast.service';
import { SCOPES } from '../../core/models/contract';
import type { ApiProblem } from '../../core/models/api-problem';
import type { TurnaroundDto, TurnaroundResult } from '../../core/models/operations';
import { environment } from '../../../environments/environment';

/**
 * Room turnover (room readiness). A completed treatment leaves a Turnover
 * task; housekeeping starts it and completes it with a result. The desk sees
 * the same list read-only: a room with an open task is not ready for the next
 * arrival, and check-in shows it as such.
 */
@Component({
  selector: 'app-turnover',
  standalone: true,
  imports: [PageHeader, StatePanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Operations"
      title="Room turnover"
      subtitle="A room is ready for the next guest once its turnover is complete. Every result is recorded against your name."
    >
      <button type="button" class="btn btn--secondary" (click)="load()" [disabled]="loading()">Refresh</button>
    </app-page-header>

    <div class="stack">
      <div class="toolbar">
        <button type="button" class="chip" [attr.aria-pressed]="openOnly()" (click)="setOpen(true)">Open</button>
        <button type="button" class="chip" [attr.aria-pressed]="!openOnly()" (click)="setOpen(false)">All</button>
        <span class="row row--end subtle numeric">{{ openCount() }} open</span>
      </div>

      @if (!live) {
        <app-state-panel state="offline" title="Room turnover needs the API"
          body="Turnover tasks are created by the server when a treatment completes. Start the API to see them." />
      } @else if (problem(); as p) {
        <app-state-panel [state]="p.status === 403 ? 'denied' : 'error'" [title]="p.title" [body]="p.detail ?? ''"
          [actionBound]="true" actionLabel="Try again" (action)="load()" />
      } @else if (loading() && tasks().length === 0) {
        <app-state-panel state="loading" title="Loading turnover tasks" />
      } @else if (tasks().length === 0) {
        <app-state-panel state="empty" title="Every room is ready" body="Nothing is waiting for housekeeping." />
      } @else {
        <div class="panel">
          <div class="panel__body panel__body--flush">
            <div class="table-wrap">
              <table class="table">
                <thead>
                  <tr>
                    <th scope="col">Due</th><th scope="col">Room</th><th scope="col">Task</th>
                    <th scope="col">Status</th><th scope="col"><span class="visually-hidden">Actions</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (t of tasks(); track t.turnaroundTaskId) {
                    <tr>
                      <td class="numeric">{{ time(t.dueUtc) }}</td>
                      <td>{{ t.roomName }}</td>
                      <td>{{ t.taskType }}</td>
                      <td>
                        <span class="badge"
                          [class.badge--warn]="t.status === 'Pending'" [class.badge--info]="t.status === 'InProgress'"
                          [class.badge--ok]="t.status === 'Completed'" [class.badge--neutral]="t.status === 'Skipped'">
                          {{ t.status === 'InProgress' ? 'In progress' : t.status }}{{ t.result ? ' · ' + t.result : '' }}
                        </span>
                      </td>
                      <td>
                        @if (canWrite() && (t.status === 'Pending' || t.status === 'InProgress')) {
                          <span class="row">
                            @if (t.status === 'Pending') {
                              <button type="button" class="btn btn--ghost" [disabled]="busy() === t.turnaroundTaskId" (click)="start(t)">Start</button>
                            }
                            <button type="button" class="btn btn--ghost" [disabled]="busy() === t.turnaroundTaskId" (click)="complete(t, 'Pass')">Pass</button>
                            <button type="button" class="btn btn--ghost" [disabled]="busy() === t.turnaroundTaskId" (click)="complete(t, 'NeedsAttention')">Needs attention</button>
                          </span>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </div>
        </div>
      }
    </div>
  `,
})
export class Turnover implements OnInit {
  private readonly api = inject(OperationsApi);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);

  protected readonly live = environment.useRealApi;
  protected readonly tasks = signal<readonly TurnaroundDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly busy = signal<string | null>(null);
  protected readonly openOnly = signal(true);
  protected readonly openCount = computed(() => this.tasks().filter((t) => t.status === 'Pending' || t.status === 'InProgress').length);
  /** The scope gate only; OpenFGA (can_update_room_readiness) is the server's answer. */
  protected readonly canWrite = computed(() => this.auth.has(SCOPES.inventory));

  ngOnInit(): void { void this.load(); }

  protected setOpen(open: boolean): void {
    this.openOnly.set(open);
    void this.load();
  }

  protected async load(): Promise<void> {
    if (!this.live) return;
    this.loading.set(true);
    try {
      this.tasks.set((await this.api.turnaround(this.openOnly())).items);
      this.problem.set(null);
    } catch (err) {
      this.problem.set(err as ApiProblem);
    } finally {
      this.loading.set(false);
    }
  }

  protected time(utc: string): string {
    return new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(new Date(utc));
  }

  protected async start(t: TurnaroundDto): Promise<void> {
    await this.write(t, () => this.api.startTurnaround(t.turnaroundTaskId, t.rowVersion), `${t.roomName}: turnover started`);
  }

  protected async complete(t: TurnaroundDto, result: TurnaroundResult): Promise<void> {
    await this.write(t, () => this.api.completeTurnaround(t.turnaroundTaskId, t.rowVersion, result),
      result === 'Pass' ? `${t.roomName} is ready` : `${t.roomName} needs attention`);
  }

  private async write(t: TurnaroundDto, call: () => Promise<TurnaroundDto>, done: string): Promise<void> {
    this.busy.set(t.turnaroundTaskId);
    try {
      const next = await call();
      this.tasks.update((list) => list.map((x) => (x.turnaroundTaskId === next.turnaroundTaskId ? next : x))
        .filter((x) => !this.openOnly() || x.status === 'Pending' || x.status === 'InProgress'));
      this.toast.success(done);
    } catch (err) {
      const p = err as ApiProblem;
      this.toast.error('That did not save', p.detail ?? p.title, p.correlationId);
      if (p.status === 412) void this.load();
    } finally {
      this.busy.set(null);
    }
  }
}
