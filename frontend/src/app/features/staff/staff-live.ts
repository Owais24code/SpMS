import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { ReferenceApi } from '../../core/api/reference-api';
import { ToastService } from '../../core/services/toast.service';
import { AuthService } from '../../core/services/auth.service';
import { ROLE_CODES } from '../../core/models/reference';
import type {
  CredentialDto, HrDto, QualificationDto, RoleAssignmentDto, RosterEntryDto, ServiceDto, StaffDto,
} from '../../core/models/reference';
import type { ApiProblem } from '../../core/models/api-problem';

type Tab = 'profile' | 'hr' | 'roles' | 'qualifications' | 'credentials';

/**
 * Staff against the API (§53.2, SEC-007, SEC-014, CON-003). The team and
 * their operational profile; the HR file (shown only to HR and the person);
 * roles proposed by one administrator and approved by another; what each
 * person is qualified to perform; credentials, whose numbers are only ever
 * masked; and the property's roster for the next two weeks.
 */
@Component({
  selector: 'app-staff-live',
  standalone: true,
  imports: [PageHeader, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Operations" title="Staff"
      subtitle="Operational profiles for everyone; the HR file only for HR and the person themselves.">
      <button type="button" class="btn btn--secondary" (click)="adding.set(!adding())">{{ adding() ? 'Cancel' : 'Add staff' }}</button>
    </app-page-header>

    @if (adding()) {
      <div class="panel"><div class="panel__body row">
        <label class="inline">Preferred name <input [(ngModel)]="newName" name="newName" aria-label="Preferred name" /></label>
        <label class="inline"><input type="checkbox" [(ngModel)]="newBookable" name="newBookable" /> Bookable</label>
        <button type="button" class="btn btn--primary" [disabled]="!newName.trim()" (click)="create()">Create</button>
      </div></div>
    }

    <div class="grid grid--split">
      <section class="panel" aria-labelledby="team-h">
        <div class="panel__head"><span class="panel__title" id="team-h">Team</span><span class="panel__hint">{{ team().length }} people</span></div>
        <ul class="list" data-testid="team">
          @for (s of team(); track s.staffId) {
            <li class="list__row" [class.is-selected]="selected()?.staffId === s.staffId">
              <button type="button" class="linklike" (click)="select(s)">{{ s.preferredName }}</button>
              <span class="subtle">{{ activeRoles(s) }}</span>
              <span class="badge" [class.badge--ok]="s.employmentStatus === 'Active'" [class.badge--warn]="s.employmentStatus !== 'Active'">{{ s.employmentStatus }}</span>
            </li>
          }
        </ul>
      </section>

      <section class="panel" aria-labelledby="person-h">
        @if (selected(); as s) {
          <div class="panel__head"><span class="panel__title" id="person-h">{{ s.preferredName }}</span>
            @if (!s.hasSignIn) { <span class="badge">No sign-in</span> }
          </div>
          <div class="tabs" role="tablist">
            @for (t of tabs; track t.key) {
              <button type="button" role="tab" [attr.aria-selected]="tab() === t.key" (click)="openTab(t.key)">{{ t.label }}</button>
            }
          </div>
          <div class="panel__body stack">
            @switch (tab()) {
              @case ('profile') {
                <label>Preferred name <input [(ngModel)]="edit.preferredName" name="pn" /></label>
                <label>Employment status
                  <select [(ngModel)]="edit.employmentStatus" name="es">
                    @for (o of statuses; track o) { <option [value]="o">{{ o }}</option> }
                  </select>
                </label>
                <label class="inline"><input type="checkbox" [(ngModel)]="edit.bookable" name="bk" /> Bookable on the board</label>
                <button type="button" class="btn btn--primary" (click)="saveProfile(s)">Save profile</button>
              }
              @case ('hr') {
                @if (hrDenied()) {
                  <p class="subtle" data-testid="hr-denied">The HR file is visible to HR and to {{ s.preferredName }} only.</p>
                } @else {
                  <div class="form-grid">
                    <label>Employee number <input [(ngModel)]="hrForm.employeeNumber" name="en" /></label>
                    <label>Job title <input [(ngModel)]="hrForm.jobTitle" name="jt" /></label>
                    <label>First name <input [(ngModel)]="hrForm.firstName" name="fn" /></label>
                    <label>Last name <input [(ngModel)]="hrForm.lastName" name="ln" /></label>
                    <label>Worker type
                      <select [(ngModel)]="hrForm.workerType" name="wt"><option>Employee</option><option>Contractor</option><option>Agency</option></select>
                    </label>
                    <label>Personal email <input [(ngModel)]="hrForm.personalEmail" name="pe" /></label>
                  </div>
                  <button type="button" class="btn btn--primary" (click)="saveHr(s)">Save HR file</button>
                }
              }
              @case ('roles') {
                <ul class="list">
                  @for (r of roles(); track r.assignmentId) {
                    <li class="list__row">
                      <span>{{ r.roleCode }} · {{ r.tenantWide ? 'all properties' : 'this property' }}</span>
                      <span class="badge" [class.badge--ok]="r.status === 'Active'" [class.badge--warn]="r.status === 'Proposed'">{{ r.status }}</span>
                      @if (r.status === 'Proposed') {
                        <button type="button" class="btn btn--ghost" (click)="approveRole(r)" [disabled]="r.proposedBy === me()">Approve</button>
                      }
                      @if (r.status === 'Active' || r.status === 'Proposed') {
                        <button type="button" class="btn btn--ghost" (click)="revokeRole(r)">Revoke</button>
                      }
                    </li>
                  } @empty { <li class="subtle">No roles.</li> }
                </ul>
                <div class="row">
                  <select [(ngModel)]="newRole" name="nr" aria-label="Role">
                    @for (r of roleCodes; track r) { <option [value]="r">{{ r }}</option> }
                  </select>
                  <label class="inline"><input type="checkbox" [(ngModel)]="newRoleTenantWide" name="tw" /> All properties</label>
                  <button type="button" class="btn btn--secondary" (click)="proposeRole(s)">Propose role</button>
                </div>
                <p class="subtle">A role is approved by a different administrator from the one who proposed it.</p>
              }
              @case ('qualifications') {
                <ul class="list">
                  @for (q of qualifications(); track q.qualificationId) {
                    <li class="list__row"><span>{{ serviceName(q.serviceId) }}</span><span class="badge" [class.badge--ok]="q.status === 'Active'">{{ q.status }}</span>
                      @if (q.status === 'Active') { <button type="button" class="btn btn--ghost" (click)="revokeQualification(q)">Revoke</button> }
                    </li>
                  } @empty { <li class="subtle">Not qualified for any service: the board will not offer them.</li> }
                </ul>
                <div class="row">
                  <select [(ngModel)]="grantService" name="gs" aria-label="Service to qualify for">
                    @for (sv of services(); track sv.serviceId) { <option [value]="sv.serviceId">{{ sv.name }}</option> }
                  </select>
                  <button type="button" class="btn btn--secondary" [disabled]="!grantService" (click)="grant(s)">Grant</button>
                </div>
              }
              @case ('credentials') {
                @if (credDenied()) {
                  <p class="subtle">Credentials are visible to HR and to {{ s.preferredName }} only.</p>
                } @else {
                  <ul class="list" data-testid="credentials">
                    @for (c of credentials(); track c.credentialId) {
                      <li class="list__row">
                        <span>{{ c.credentialKind }} {{ c.licenseTypeCode ?? '' }} <span class="numeric">{{ c.numberMasked ?? '' }}</span></span>
                        <span class="subtle">{{ c.expiresAt ? 'expires ' + c.expiresAt : '' }}</span>
                        <span class="badge" [class.badge--ok]="c.status === 'Verified'" [class.badge--warn]="c.status === 'Pending'">{{ c.status }}</span>
                        @if (c.status === 'Pending') { <button type="button" class="btn btn--ghost" (click)="verify(c)">Verify</button> }
                      </li>
                    } @empty { <li class="subtle">No credentials on file.</li> }
                  </ul>
                  <div class="form-grid">
                    <label>Kind <select [(ngModel)]="cred.credentialKind" name="ck"><option>License</option><option>Certification</option><option>Training</option></select></label>
                    <label>Type <input [(ngModel)]="cred.licenseTypeCode" name="ct" placeholder="LMT" /></label>
                    <label>Number <input [(ngModel)]="cred.number" name="cn" autocomplete="off" /></label>
                    <label>Expires <input type="date" [(ngModel)]="cred.expiresAt" name="ce" /></label>
                  </div>
                  <button type="button" class="btn btn--secondary" (click)="addCredential(s)">Add credential</button>
                }
              }
            }
          </div>
        } @else {
          <div class="panel__body"><p class="subtle">Choose someone from the team.</p></div>
        }
      </section>
    </div>

    <section class="panel" aria-labelledby="roster-h">
      <div class="panel__head"><span class="panel__title" id="roster-h">Roster — next 14 days</span></div>
      <div class="panel__body stack">
        <div class="row">
          <select [(ngModel)]="shift.staffId" name="rs" aria-label="Who">
            @for (s of team(); track s.staffId) { <option [value]="s.staffId">{{ s.preferredName }}</option> }
          </select>
          <select [(ngModel)]="shift.entryType" name="rt" aria-label="Entry type"><option>Shift</option><option>OnCall</option><option>Leave</option></select>
          <input type="datetime-local" [(ngModel)]="shift.starts" name="rf" aria-label="Starts" />
          <input type="datetime-local" [(ngModel)]="shift.ends" name="re" aria-label="Ends" />
          <button type="button" class="btn btn--secondary" (click)="addRoster()">Add</button>
        </div>
        <table class="table" data-testid="roster">
          <tbody>
            @for (r of roster(); track r.workScheduleId) {
              <tr>
                <td>{{ nameOf(r.staffId) }}</td><td>{{ r.entryType }}{{ r.leaveType ? ' · ' + r.leaveType : '' }}</td>
                <td class="numeric">{{ local(r.startsUtc) }} – {{ local(r.endsUtc) }}</td>
                <td><span class="badge" [class.badge--ok]="r.status === 'Published' || r.status === 'Approved'">{{ r.status }}</span></td>
                <td class="row row--end">
                  @if (r.status === 'Draft') { <button type="button" class="btn btn--ghost" (click)="moveRoster(r, 'publish')">Publish</button> }
                  @if (r.status === 'Requested') {
                    <button type="button" class="btn btn--ghost" (click)="moveRoster(r, 'approve')">Approve</button>
                    <button type="button" class="btn btn--ghost" (click)="moveRoster(r, 'reject')">Reject</button>
                  }
                  @if (r.status !== 'Cancelled' && r.status !== 'Rejected') { <button type="button" class="btn btn--ghost" (click)="moveRoster(r, 'cancel')">Cancel</button> }
                </td>
              </tr>
            } @empty { <tr><td class="subtle">Nothing rostered: availability is not asserted either way.</td></tr> }
          </tbody>
        </table>
      </div>
    </section>
  `,
  styles: [`
    :host { display: block; }
    .grid--split { display: grid; gap: var(--space-4); grid-template-columns: minmax(0, 1fr) minmax(0, 1.4fr); margin-bottom: var(--space-4); }
    @media (max-width: 900px) { .grid--split { grid-template-columns: 1fr; } }
    .list { list-style: none; margin: 0; padding: 0; }
    .list__row { display: flex; gap: var(--space-3); align-items: center; justify-content: space-between; padding: var(--space-2) var(--space-4); border-bottom: 1px solid var(--border, #e5e5e5); flex-wrap: wrap; }
    .is-selected { background: var(--surface-2, #f6f6f6); }
    .tabs { display: flex; gap: var(--space-2); padding: 0 var(--space-4); flex-wrap: wrap; }
    .tabs [aria-selected="true"] { font-weight: var(--weight-bold, 600); text-decoration: underline; }
    .tabs button { background: none; border: 0; padding: var(--space-2); cursor: pointer; }
    .inline { display: inline-flex; gap: var(--space-2); align-items: center; }
    .linklike { background: none; border: 0; padding: 0; text-decoration: underline; cursor: pointer; color: inherit; }
    label { display: grid; gap: var(--space-1); font-size: var(--text-sm); }
  `],
})
export class StaffLive implements OnInit {
  private readonly api = inject(ReferenceApi);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);

  protected readonly tabs: readonly { key: Tab; label: string }[] = [
    { key: 'profile', label: 'Profile' }, { key: 'hr', label: 'HR file' }, { key: 'roles', label: 'Roles' },
    { key: 'qualifications', label: 'Qualifications' }, { key: 'credentials', label: 'Credentials' },
  ];
  protected readonly statuses = ['Pending', 'Active', 'OnLeave', 'Suspended', 'Terminated'];
  protected readonly roleCodes = ROLE_CODES;

  protected readonly team = signal<readonly StaffDto[]>([]);
  protected readonly services = signal<readonly ServiceDto[]>([]);
  protected readonly selected = signal<StaffDto | null>(null);
  protected readonly tab = signal<Tab>('profile');
  protected readonly roles = signal<readonly RoleAssignmentDto[]>([]);
  protected readonly qualifications = signal<readonly QualificationDto[]>([]);
  protected readonly credentials = signal<readonly CredentialDto[]>([]);
  protected readonly roster = signal<readonly RosterEntryDto[]>([]);
  protected readonly hr = signal<HrDto | null>(null);
  protected readonly hrDenied = signal(false);
  protected readonly credDenied = signal(false);
  protected readonly adding = signal(false);
  protected readonly me = computed(() => this.auth.user()?.principalId ?? null);

  protected newName = '';
  protected newBookable = true;
  protected edit = { preferredName: '', employmentStatus: 'Active', bookable: true };
  protected hrForm = { employeeNumber: '', firstName: '', lastName: '', jobTitle: '', workerType: 'Employee', personalEmail: '' };
  protected newRole = 'provider';
  protected newRoleTenantWide = false;
  protected grantService = '';
  protected cred = { credentialKind: 'License', licenseTypeCode: '', number: '', expiresAt: '' };
  protected shift = { staffId: '', entryType: 'Shift', starts: '', ends: '' };

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> {
    try {
      const [team, services] = await Promise.all([this.api.staff(), this.api.services('Active')]);
      this.team.set(team);
      this.services.set(services);
      this.grantService ||= services[0]?.serviceId ?? '';
      this.shift.staffId ||= team[0]?.staffId ?? '';
      await this.loadRoster();
    } catch (e) { this.fail(e); }
  }

  private async loadRoster(): Promise<void> {
    const from = new Date(); const to = new Date(Date.now() + 14 * 86400_000);
    this.roster.set(await this.api.roster(from.toISOString(), to.toISOString()));
  }

  protected activeRoles(s: StaffDto): string {
    return (s.roles ?? []).filter((r) => r.status === 'Active').map((r) => r.roleCode.replace('_', ' ')).join(', ');
  }
  protected serviceName(id: string): string { return this.services().find((s) => s.serviceId === id)?.name ?? id.slice(-6); }
  protected nameOf(id: string): string { return this.team().find((s) => s.staffId === id)?.preferredName ?? '—'; }
  protected local(utc: string): string { return new Date(utc).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }); }

  protected select(s: StaffDto): void {
    this.selected.set(s);
    this.edit = { preferredName: s.preferredName, employmentStatus: s.employmentStatus, bookable: s.bookable };
    void this.openTab(this.tab());
  }

  protected async openTab(t: Tab): Promise<void> {
    this.tab.set(t);
    const s = this.selected(); if (!s) return;
    try {
      if (t === 'roles') this.roles.set(await this.api.roles(s.staffId));
      if (t === 'qualifications') this.qualifications.set(await this.api.qualifications(s.staffId));
      if (t === 'hr') {
        try {
          const h = await this.api.hr(s.staffId);
          this.hr.set(h); this.hrDenied.set(false);
          this.hrForm = { employeeNumber: h.employeeNumber, firstName: h.firstName, lastName: h.lastName, jobTitle: h.jobTitle, workerType: h.workerType, personalEmail: h.personalEmail ?? '' };
        } catch (e) {
          const p = e as ApiProblem;
          this.hr.set(null);
          this.hrDenied.set(p.code === 'AUTHORIZATION_DENIED');
          if (p.code === 'NOT_FOUND') this.hrForm = { employeeNumber: '', firstName: '', lastName: '', jobTitle: '', workerType: 'Employee', personalEmail: '' };
        }
      }
      if (t === 'credentials') {
        try { this.credentials.set(await this.api.credentials(s.staffId)); this.credDenied.set(false); }
        catch (e) { if ((e as ApiProblem).code === 'AUTHORIZATION_DENIED') this.credDenied.set(true); else throw e; }
      }
    } catch (e) { this.fail(e); }
  }

  protected async create(): Promise<void> {
    await this.run(async () => {
      await this.api.createStaff({ preferredName: this.newName.trim(), bookable: this.newBookable });
      this.toast.success('Staff member added', this.newName.trim());
      this.newName = ''; this.adding.set(false);
      await this.load();
    });
  }

  protected async saveProfile(s: StaffDto): Promise<void> {
    await this.run(async () => {
      const saved = await this.api.updateStaff(s.staffId, s.rowVersion, this.edit);
      this.toast.success('Profile saved', saved.preferredName);
      await this.load();
      this.select(this.team().find((x) => x.staffId === s.staffId) ?? saved);
    });
  }

  protected async saveHr(s: StaffDto): Promise<void> {
    await this.run(async () => {
      const h = this.hr();
      const saved = await this.api.saveHr(s.staffId, this.hrForm, h?.rowVersion);
      this.hr.set(saved);
      this.toast.success('HR file saved');
    });
  }

  protected async proposeRole(s: StaffDto): Promise<void> {
    await this.run(async () => {
      await this.api.proposeRole(s.staffId, this.newRole, this.newRoleTenantWide);
      this.toast.success('Role proposed', 'Another administrator approves it.');
      this.roles.set(await this.api.roles(s.staffId));
    });
  }

  protected async approveRole(r: RoleAssignmentDto): Promise<void> {
    await this.run(async () => {
      await this.api.approveRole(r.assignmentId, r.rowVersion);
      this.toast.success('Role approved', r.roleCode);
      this.roles.set(await this.api.roles(r.staffId));
    });
  }

  protected async revokeRole(r: RoleAssignmentDto): Promise<void> {
    await this.run(async () => {
      await this.api.revokeRole(r.assignmentId, r.rowVersion, 'Revoked from the staff screen');
      this.toast.info('Role revoked', r.roleCode);
      this.roles.set(await this.api.roles(r.staffId));
    });
  }

  protected async grant(s: StaffDto): Promise<void> {
    await this.run(async () => {
      await this.api.grant(s.staffId, this.grantService);
      this.toast.success('Qualification granted', this.serviceName(this.grantService));
      this.qualifications.set(await this.api.qualifications(s.staffId));
    });
  }

  protected async revokeQualification(q: QualificationDto): Promise<void> {
    await this.run(async () => {
      await this.api.revokeQualification(q.qualificationId, q.rowVersion, 'Revoked from the staff screen');
      this.qualifications.set(await this.api.qualifications(q.staffId));
    });
  }

  protected async addCredential(s: StaffDto): Promise<void> {
    await this.run(async () => {
      const c = await this.api.addCredential(s.staffId, {
        credentialKind: this.cred.credentialKind, licenseTypeCode: this.cred.licenseTypeCode || null,
        number: this.cred.number || null, expiresAt: this.cred.expiresAt || null,
      });
      this.toast.success('Credential added', c.numberMasked ?? c.credentialKind);
      this.cred = { credentialKind: 'License', licenseTypeCode: '', number: '', expiresAt: '' };
      this.credentials.set(await this.api.credentials(s.staffId));
    });
  }

  protected async verify(c: CredentialDto): Promise<void> {
    await this.run(async () => {
      await this.api.verifyCredential(c.credentialId, c.rowVersion, true);
      this.toast.success('Credential verified');
      this.credentials.set(await this.api.credentials(c.staffId));
    });
  }

  protected async addRoster(): Promise<void> {
    if (!this.shift.starts || !this.shift.ends) { this.toast.warn('Choose when', 'A start and an end are needed.'); return; }
    await this.run(async () => {
      await this.api.addRoster({
        staffId: this.shift.staffId, entryType: this.shift.entryType, leaveType: this.shift.entryType === 'Leave' ? 'Planned' : undefined,
        startsUtc: new Date(this.shift.starts).toISOString(), endsUtc: new Date(this.shift.ends).toISOString(),
      });
      await this.loadRoster();
    });
  }

  protected async moveRoster(r: RosterEntryDto, action: 'publish' | 'approve' | 'reject' | 'cancel'): Promise<void> {
    await this.run(async () => { await this.api.moveRoster(r.workScheduleId, r.rowVersion, action); await this.loadRoster(); });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    try { await work(); } catch (e) { this.fail(e); }
  }

  private fail(e: unknown): void {
    const p = e as ApiProblem;
    if (p?.code === 'AUTHORIZATION_DENIED') this.toast.warn('Not permitted', p.detail ?? p.title, p.code);
    else this.toast.error('That did not work', p?.detail ?? p?.title ?? String(e), p?.correlationId);
  }
}
