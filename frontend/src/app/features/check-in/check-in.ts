import { Component, ChangeDetectionStrategy, signal, computed, inject, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { environment } from '../../../environments/environment';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { HighlightDirective } from '../../shared/directives/highlight.directive';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import type { ArrivalRow } from '../../core/models/spa.model';

type Filter = 'all' | 'ready' | 'blocked' | 'done';

@Component({
  selector: 'app-check-in',
  standalone: true,
  imports: [PageHeader, StatePanel, HighlightDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './check-in.html',
})
export class CheckIn implements OnInit {
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  protected readonly live = environment.useRealApi;
  protected readonly store = inject(WorkspaceStore);

  protected readonly term = signal('');
  protected readonly filter = signal<Filter>('all');
  protected readonly busy = signal<string | null>(null);

  ngOnInit(): void {
    void this.store.loadArrivals();
  }

  protected readonly rows = computed(() => {
    const t = this.term().trim().toLowerCase();
    const f = this.filter();
    const done = this.store.checkedIn();

    return this.store.arrivals().filter((a) => {
      if (t && !(a.guestAlias.toLowerCase().includes(t) || a.service.toLowerCase().includes(t))) return false;
      if (f === 'done') return done.includes(a.id);
      if (done.includes(a.id)) return f === 'all';
      if (f === 'ready') return this.ready(a);
      if (f === 'blocked') return !this.ready(a);
      return true;
    });
  });

  protected ready(a: ArrivalRow): boolean {
    return a.formsComplete && a.depositSettled && a.roomReady;
  }

  protected isDone(a: ArrivalRow): boolean {
    return this.store.checkedIn().includes(a.id);
  }

  protected async checkIn(a: ArrivalRow): Promise<void> {
    this.busy.set(a.id);
    const res = await this.store.checkIn(a.id);
    this.busy.set(null);

    if (res.kind === 'committed') {
      this.toast.success(
        `${a.guestAlias} checked in`,
        a.locker ? `Locker ${a.locker}.` : 'No locker assigned yet.',
        this.store.canUndoCheckIn ? () => this.store.undoCheckIn(a.id) : undefined,
      );
    } else if (res.code === 'STALE_VERSION') {
      this.toast.warn('This booking just changed', 'The list has been refreshed. Check the row and try again.', res.code);
    } else {
      this.toast.warn('Not ready to check in', 'Resolve the outstanding items on that row first.', res.code);
    }
  }

  protected fix(a: ArrivalRow, what: 'forms' | 'deposit' | 'room'): void {
    if (this.live) {
      // Against the API these are recorded where they happen, not ticked off here.
      if (what === 'room') { void this.router.navigateByUrl('/app/turnover'); return; }
      if (what === 'deposit') { void this.router.navigateByUrl('/app/checkout'); return; }
      this.toast.info(what === 'forms' ? 'Intake is completed by the guest' : 'Deposits are taken at payment',
        what === 'forms' ? 'Send the intake link, or complete it on the provider tablet.' : 'Take the deposit from the booking’s payment panel.');
      return;
    }
    if (what === 'forms')   { this.store.completeForms(a.id);  this.toast.success('Forms marked complete', a.guestAlias); }
    if (what === 'deposit') { this.store.settleDeposit(a.id);  this.toast.success('Deposit settled', a.guestAlias); }
    if (what === 'room')    { this.store.markRoomReady(a.id);  this.toast.success('Room marked ready', a.guestAlias); }
  }

  protected assignLocker(a: ArrivalRow): void {
    this.store.assignLocker(a.id);
    this.toast.success('Locker assigned', a.guestAlias);
  }

  protected walkIn(): void {
    if (this.live) {
      void this.router.navigateByUrl('/app/booking');
      return;
    }
    const row = this.store.addWalkIn();
    this.toast.info('Walk-in added', `${row.guestAlias} — complete forms and deposit to check in.`);
  }

  protected scan(): void {
    const next = this.store.arrivals().find((a) => !this.isDone(a));
    if (!next) { this.toast.info('Nothing to scan', 'Everyone expected has arrived.'); return; }
    this.term.set(next.guestAlias);
    this.toast.info('Credential read', `Matched ${next.guestAlias}.`);
  }

  protected clearFilters(): void {
    this.term.set('');
    this.filter.set('all');
  }
}
