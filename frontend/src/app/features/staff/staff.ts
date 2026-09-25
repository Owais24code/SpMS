import { Component, ChangeDetectionStrategy, inject, signal, computed } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';

@Component({
  selector: 'app-staff',
  standalone: true,
  imports: [PageHeader, StatePanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Operations"
      title="Staff and credentials"
      subtitle="As a manager you can see whether someone can be assigned, and when a credential expires — not why a block exists."
    >
      <button type="button" class="btn btn--secondary" (click)="upload()">Upload credential</button>
      <button type="button" class="btn btn--primary" (click)="add()">Add team member</button>
    </app-page-header>

    <div class="stack">
      <div class="grid grid--split">
        <div class="panel">
          <div class="panel__head">
            <span class="panel__title">Team</span>
            <div class="panel__actions">
              <button type="button" class="chip" [attr.aria-pressed]="filter() === 'active'" (click)="filter.set('active')">Everyone</button>
              <button type="button" class="chip" [attr.aria-pressed]="filter() === 'blocked'" (click)="filter.set('blocked')">Blocked</button>
            </div>
          </div>
          <div class="panel__body panel__body--flush">
            <div class="table-wrap">
              <table class="table">
                <thead>
                  <tr>
                    <th scope="col">Name</th>
                    <th scope="col">Role</th>
                    <th scope="col">Credential</th>
                    <th scope="col">Expires</th>
                    <th scope="col">Assignable</th>
                  </tr>
                </thead>
                <tbody>
                  @for (s of staff(); track s.id) {
                    <tr>
                      <td>{{ s.name }}</td>
                      <td>{{ s.role }}</td>
                      <td>{{ s.credential }}</td>
                      <td class="numeric">{{ s.expires }}</td>
                      <td class="wrap">
                        @if (s.assignable) {
                          <span class="badge badge--ok">Yes</span>
                        } @else {
                          <span class="badge badge--danger">Blocked</span>
                          <span class="subtle" style="margin-inline-start: var(--space-2)">{{ s.blockHint }}</span>
                          <button type="button" class="btn btn--ghost" (click)="renew(s.id, s.name)">Renew</button>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </div>
        </div>

        <div class="stack">
          <app-state-panel
            state="denied"
            title="Screening workflow is not available to your role"
            body="Managers see a cleared or not-cleared result. The request, package and adjudication are handled by HR."
          />

          <div class="panel">
            <div class="panel__head"><span class="panel__title">Expiring in 30 days</span></div>
            <div class="panel__body">
              <ul class="timeline">
                <li class="is-active">
                  <p class="timeline__when numeric">11 Sep 2026</p>
                  <p class="timeline__what">Tomas Brandt — massage licence</p>
                  <p class="timeline__note">Already past. Assignment blocked until renewed.</p>
                </li>
                <li>
                  <p class="timeline__when numeric">30 Sep 2026</p>
                  <p class="timeline__what">Priya Nair — esthetics certificate</p>
                  <p class="timeline__note">Renewal evidence not yet uploaded.</p>
                </li>
                <li>
                  <p class="timeline__when numeric">02 Oct 2026</p>
                  <p class="timeline__what">Marco Ruiz — massage licence</p>
                  <p class="timeline__note">Verification in progress.</p>
                </li>
              </ul>
            </div>
          </div>
        </div>
      </div>

      <div class="panel">
        <div class="panel__head">
          <span class="panel__title">Document upload</span>
          <span class="panel__hint">PDF, JPG or PNG · up to 10 MB</span>
        </div>
        <div class="panel__body">
          <div class="drop">
            <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
              <path d="M12 16V4M8 8l4-4 4 4M4 16v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3"
                    fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
            <p><strong>Drop a file here</strong> or <button type="button" class="linklike" (click)="upload()">choose one</button></p>
            <p class="subtle">Uploads are quarantined until a malware scan clears them. Downloads are signed and expire.</p>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }

    .drop {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: var(--space-2);
      padding: var(--space-10);
      border: 2px dashed var(--border-default);
      border-radius: var(--radius-lg);
      background: var(--bg-subtle);
      text-align: center;
      transition: border-color var(--dur-fast) var(--ease-out), background var(--dur-fast) var(--ease-out);

      &:hover { border-color: var(--border-accent); background: var(--status-info-bg); }

      svg { width: 32px; height: 32px; color: var(--fg-accent); margin-bottom: var(--space-2); }
    }

    .linklike {
      border: 0;
      background: none;
      padding: 0;
      color: var(--fg-link);
      font: inherit;
      font-weight: var(--weight-bold);
      text-decoration: underline;
      cursor: pointer;
    }
  `],
})
export class Staff {
  private readonly toast = inject(ToastService);
  protected readonly store = inject(WorkspaceStore);

  protected readonly filter = signal<'active' | 'blocked'>('active');

  protected readonly staff = computed(() =>
    this.filter() === 'blocked'
      ? this.store.staff().filter((s) => !s.assignable)
      : this.store.staff());

  protected renew(id: string, name: string): void {
    this.store.renewCredential(id);
    this.toast.success('Credential renewed', `${name} is assignable again from now.`);
  }

  protected upload(): void {
    this.toast.info('Upload quarantined', 'The file is held until a malware scan clears it. Downloads are signed and expire.');
  }

  protected add(): void {
    this.toast.info('Invite sent', 'They will appear here once HR completes the employment record.');
  }
}
