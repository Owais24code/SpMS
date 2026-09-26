import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { OpsApi } from '../../core/api/ops-api';
import { ToastService } from '../../core/services/toast.service';
import { browserToday } from '../../core/services/board-time';
import type { ReportCatalogDto, ReportRunDto } from '../../core/models/ops';
import type { ApiProblem } from '../../core/models/api-problem';

/**
 * Reports against the API (RPT21315/21316). Each run is saved with the
 * SHA-256 of its result and of its definition, so a printed report can be
 * checked later against what the system actually showed. A day that has
 * ended is a closed snapshot. Who may run and export each report is the
 * server's decision.
 */
@Component({
  selector: 'app-reports-live',
  standalone: true,
  imports: [PageHeader, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Finance & setup" title="Reports" subtitle="Counts, minutes and money — never a guest's identity.">
      <label class="inline">Day <input type="date" [(ngModel)]="date" name="d" aria-label="Report day" /></label>
    </app-page-header>

    <div class="cards">
      @for (r of catalog(); track r.reportId) {
        <article class="panel card">
          <div class="panel__body stack">
            <strong>{{ r.title }}</strong>
            <p class="subtle">{{ r.description }}</p>
            <button type="button" class="btn btn--secondary" [attr.data-testid]="'run-' + r.reportId" (click)="run(r)">Run</button>
          </div>
        </article>
      }
    </div>

    @if (current(); as run) {
      <section class="panel" data-testid="report-result">
        <div class="panel__head">
          <span class="panel__title">{{ titleOf(run.reportId) }} · {{ run.result?.date }}</span>
          <span class="badge" [class.badge--ok]="run.resultIntact">{{ run.resultIntact ? 'Verified' : 'Hash mismatch' }}</span>
          @if (run.closed) { <span class="badge">Closed day</span> }
          <button type="button" class="btn btn--ghost" (click)="export(run)">Export CSV</button>
        </div>
        <div class="panel__body stack">
          <p class="subtle numeric">SHA-256 {{ run.resultSha256 }}</p>
          @for (t of run.result?.tables ?? []; track t.name) {
            <h3 class="h-sub">{{ t.name.replace('_', ' ') }}</h3>
            <table class="table">
              <thead><tr>@for (c of t.columns; track c) { <th scope="col">{{ c.replace('_', ' ') }}</th> }</tr></thead>
              <tbody>
                @for (row of t.rows; track $index) { <tr>@for (v of row; track $index) { <td class="numeric">{{ cell(v) }}</td> }</tr> }
                @empty { <tr><td class="subtle" [attr.colspan]="t.columns.length">Nothing on this day.</td></tr> }
              </tbody>
            </table>
          }
        </div>
      </section>
    }
  `,
  styles: [`
    :host { display: block; }
    .cards { display: grid; gap: var(--space-4); grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); margin-bottom: var(--space-4); }
    .inline { display: inline-flex; gap: var(--space-2); align-items: center; font-size: var(--text-sm); }
    .h-sub { font-size: var(--text-sm); margin: var(--space-3) 0 0; text-transform: capitalize; }
  `],
})
export class ReportsLive implements OnInit {
  private readonly api = inject(OpsApi);
  private readonly toast = inject(ToastService);

  protected readonly catalog = signal<readonly ReportCatalogDto[]>([]);
  protected readonly current = signal<ReportRunDto | null>(null);
  protected date = browserToday();

  async ngOnInit(): Promise<void> {
    try { this.catalog.set(await this.api.reports()); } catch (e) { this.fail(e); }
  }

  protected titleOf(id: string): string { return this.catalog().find((r) => r.reportId === id)?.title ?? id; }
  protected cell(v: unknown): string { return v === null || v === undefined ? '—' : typeof v === 'number' ? v.toLocaleString() : String(v); }

  protected async run(r: ReportCatalogDto): Promise<void> {
    try { this.current.set(await this.api.runReport(r.reportId, this.date)); }
    catch (e) { this.fail(e); }
  }

  protected async export(run: ReportRunDto): Promise<void> {
    try {
      const blob = await this.api.exportReport(run.runId);
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = `${run.reportId}-${run.result?.date ?? 'report'}.csv`;
      a.click();
      URL.revokeObjectURL(a.href);
    } catch (e) { this.fail(e); }
  }

  private fail(e: unknown): void {
    const p = e as ApiProblem;
    if (p?.code === 'AUTHORIZATION_DENIED') this.toast.warn('Not permitted', p.detail ?? p.title, p.code);
    else this.toast.error('That did not work', p?.detail ?? p?.title ?? String(e), p?.correlationId);
  }
}
