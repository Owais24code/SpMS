import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { OperationsApi } from '../../core/api/operations-api';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import type { ApiProblem } from '../../core/models/api-problem';
import type { WaitlistDto, WaitlistStatus } from '../../core/models/operations';
import { environment } from '../../../environments/environment';

/**
 * The waitlist: guests waiting for a window. When a slot frees, the desk
 * offers it for a limited time; the entry is accepted once the guest books,
 * and an offer that lapses puts them back in the queue (the server's job).
 */
@Component({
  selector: 'app-waitlist',
  standalone: true,
  imports: [PageHeader, StatePanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Guests"
      title="Waitlist"
      subtitle="Longest-waiting first. An offer holds for the minutes you choose, then the guest goes back in the queue."
    >
      <button type="button" class="btn btn--secondary" (click)="load()" [disabled]="loading()">Refresh</button>
    </app-page-header>

    <div class="stack">
      <div class="toolbar">
        @for (s of filters; track s) {
          <button type="button" class="chip" [attr.aria-pressed]="status() === s" (click)="setStatus(s)">{{ s }}</button>
        }
        <span class="row row--end subtle numeric">{{ entries().length }} shown</span>
      </div>

      @if (!live) {
        <app-state-panel state="offline" title="The waitlist needs the API" body="Start the API to see who is waiting." />
      } @else if (problem(); as p) {
        <app-state-panel [state]="p.status === 403 ? 'denied' : 'error'" [title]="p.title" [body]="p.detail ?? ''"
          [actionBound]="true" actionLabel="Try again" (action)="load()" />
      } @else if (entries().length === 0 && !loading()) {
        <app-state-panel state="empty" title="Nobody is waiting" body="Guests added from booking appear here." />
      } @else {
        <div class="panel">
          <div class="panel__body panel__body--flush">
            <div class="table-wrap">
              <table class="table">
                <thead>
                  <tr>
                    <th scope="col">Guest</th><th scope="col">Service</th><th scope="col">Window</th>
                    <th scope="col">Status</th><th scope="col"><span class="visually-hidden">Actions</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (w of entries(); track w.waitlistId) {
                    <tr>
                      <td>{{ w.guestAlias }}</td>
                      <td>{{ serviceName(w.serviceId) }}</td>
                      <td class="numeric">{{ window(w) }}</td>
                      <td>
                        <span class="badge" [class.badge--info]="w.status === 'Waiting'" [class.badge--warn]="w.status === 'Offered'"
                              [class.badge--ok]="w.status === 'Accepted'" [class.badge--neutral]="w.status === 'Expired' || w.status === 'Cancelled'">
                          {{ w.status }}{{ w.status === 'Offered' && w.offerExpiresUtc ? ' until ' + time(w.offerExpiresUtc) : '' }}
                        </span>
                      </td>
                      <td>
                        <span class="row">
                          @if (w.status === 'Waiting') {
                            <button type="button" class="btn btn--ghost" [disabled]="busy() === w.waitlistId" (click)="offer(w)">Offer 30 min</button>
                          }
                          @if (w.status === 'Waiting' || w.status === 'Offered') {
                            <button type="button" class="btn btn--ghost" [disabled]="busy() === w.waitlistId" (click)="cancel(w)">Remove</button>
                          }
                        </span>
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
export class Waitlist implements OnInit {
  private readonly api = inject(OperationsApi);
  private readonly store = inject(WorkspaceStore);
  private readonly toast = inject(ToastService);

  protected readonly live = environment.useRealApi;
  protected readonly filters: readonly WaitlistStatus[] = ['Waiting', 'Offered', 'Accepted', 'Expired', 'Cancelled'];
  protected readonly status = signal<WaitlistStatus>('Waiting');
  protected readonly entries = signal<readonly WaitlistDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly busy = signal<string | null>(null);
  private readonly services = computed(() => new Map(this.store.services().map((s) => [s.serviceId, s.name])));

  ngOnInit(): void {
    void this.store.loadServices();
    void this.load();
  }

  protected setStatus(s: WaitlistStatus): void {
    this.status.set(s);
    void this.load();
  }

  protected async load(): Promise<void> {
    if (!this.live) return;
    this.loading.set(true);
    try {
      this.entries.set((await this.api.waitlist(this.status())).items);
      this.problem.set(null);
    } catch (err) {
      this.problem.set(err as ApiProblem);
    } finally {
      this.loading.set(false);
    }
  }

  protected serviceName(id: string | null): string {
    return id === null ? 'Any' : this.services().get(id) ?? 'Service';
  }

  protected time(utc: string): string {
    return new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(new Date(utc));
  }

  protected window(w: WaitlistDto): string {
    const d = new Intl.DateTimeFormat(undefined, { weekday: 'short', day: 'numeric', month: 'short' }).format(new Date(w.earliestUtc));
    return `${d} ${this.time(w.earliestUtc)}–${this.time(w.latestUtc)}`;
  }

  protected async offer(w: WaitlistDto): Promise<void> {
    await this.write(w, () => this.api.offerWaitlist(w.waitlistId, w.rowVersion, 30), `Offered to ${w.guestAlias} for 30 minutes`);
  }

  protected async cancel(w: WaitlistDto): Promise<void> {
    await this.write(w, () => this.api.cancelWaitlist(w.waitlistId, w.rowVersion), `${w.guestAlias} removed from the waitlist`);
  }

  private async write(w: WaitlistDto, call: () => Promise<WaitlistDto>, done: string): Promise<void> {
    this.busy.set(w.waitlistId);
    try {
      await call();
      this.toast.success(done);
      await this.load();
    } catch (err) {
      const p = err as ApiProblem;
      this.toast.error('That did not save', p.detail ?? p.title, p.correlationId);
      if (p.status === 412) void this.load();
    } finally {
      this.busy.set(null);
    }
  }
}
