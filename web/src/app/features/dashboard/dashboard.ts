import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatCard } from '../../shared/components/stat-card/stat-card';
import { KPIS, WEEK_BOOKINGS, WEEK_LABELS, ARRIVALS, ACTIVE_CONFLICT } from '../../core/data/workspace-data';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [RouterLink, PageHeader, StatCard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class Dashboard {
  protected readonly kpis = KPIS;
  protected readonly arrivals = ARRIVALS.slice(0, 4);
  protected readonly conflict = ACTIVE_CONFLICT;
  protected readonly labels = WEEK_LABELS;
  protected readonly bookings = WEEK_BOOKINGS;
  protected readonly peak = Math.max(...WEEK_BOOKINGS);

  protected height(v: number): string {
    return `${Math.round((v / this.peak) * 100)}%`;
  }
}
