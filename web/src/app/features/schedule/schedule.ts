import { Component, ChangeDetectionStrategy, signal } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { LANES, HOURS, ACTIVE_CONFLICT, HARD_CONFLICT } from '../../core/data/workspace-data';
import type { Conflict } from '../../core/models/spa.model';

@Component({
  selector: 'app-schedule',
  standalone: true,
  imports: [PageHeader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './schedule.html',
  styleUrl: './schedule.scss',
})
export class Schedule {
  protected readonly lanes = LANES;
  protected readonly hours = HOURS;
  protected readonly view = signal<'day' | 'week'>('day');

  /** The drawer is the only place an override can be committed. */
  protected readonly drawer = signal<Conflict | null>(ACTIVE_CONFLICT);
  protected readonly hard = HARD_CONFLICT;
  protected readonly reason = signal('');

  protected open(c: Conflict): void { this.drawer.set(c); }
  protected close(): void { this.drawer.set(null); }

  protected canCommit(): boolean {
    const c = this.drawer();
    return !!c && c.severity === 'soft' && this.reason().trim().length > 3;
  }
}
