import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { GuestsApi } from '../../core/api/guests-api';
import { AuthService } from '../../core/services/auth.service';
import { ToastService } from '../../core/services/toast.service';
import { SCOPES } from '../../core/models/contract';
import type { ApiProblem } from '../../core/models/api-problem';
import type {
  ConsentDto, DelegationDto, GuestDto, GuestSearchHitDto, MergeCaseDto,
} from '../../core/models/guests';

type Tab = 'profile' | 'contacts' | 'consent' | 'delegation' | 'privacy';

const PREFS: Record<string, readonly string[]> = {
  pressure: ['Light', 'Medium', 'Firm', 'Deep'],
  music: ['None', 'Soft', 'Nature', 'Classical', 'GuestChoice'],
  providerGender: ['NoPreference', 'Female', 'Male'],
  roomTemperature: ['Cool', 'Neutral', 'Warm'],
};

/**
 * Guest profiles (IDN-001/002/003/005/006). Search by name, email or phone
 * (matched on a keyed hash, never decrypted to search); create with the
 * privacy alias the board will show; keep operational preferences (health
 * information is refused — it belongs to intake); record consent and
 * delegated authority; log privacy requests; and work the merge queue,
 * where the proposer can never be the one who decides.
 */
@Component({
  selector: 'app-guests',
  standalone: true,
  imports: [PageHeader, StatePanel, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Guests" title="Guest profiles"
      subtitle="One person across every property. Contact details are stored encrypted and shown masked.">
      <button type="button" class="btn btn--secondary" (click)="toggleMerges()">{{ showMerges() ? 'Back to guests' : 'Merge queue' }}</button>
      <button type="button" class="btn btn--primary" (click)="creating.set(!creating())">{{ creating() ? 'Cancel' : 'New guest' }}</button>
    </app-page-header>

    @if (showMerges()) {
      <div class="panel">
        <div class="panel__head"><span class="panel__title">Possible duplicates</span>
          <span class="panel__hint">Never merged silently. The person who proposed a case cannot decide it.</span></div>
        <div class="panel__body panel__body--flush">
          @if (merges().length === 0) {
            <app-state-panel state="empty" title="No open cases" body="Likely duplicates found at creation appear here." />
          } @else {
            <table class="table">
              <thead><tr><th scope="col">Keep</th><th scope="col">Duplicate</th><th scope="col">Signals</th><th scope="col">Status</th><th scope="col"><span class="visually-hidden">Actions</span></th></tr></thead>
              <tbody>
                @for (m of merges(); track m.mergeCaseId) {
                  <tr>
                    <td class="numeric">{{ m.survivingGuestId.slice(-6) }}</td>
                    <td class="numeric">{{ m.duplicateGuestId.slice(-6) }}</td>
                    <td>{{ (m.matchSignals.signals ?? []).join(', ') }} · {{ (m.confidence * 100).toFixed(0) }}%</td>
                    <td><span class="badge" [class.badge--warn]="m.status === 'Candidate'" [class.badge--info]="m.status === 'Approved'">{{ m.status }}</span></td>
                    <td><span class="row">
                      @if (m.status === 'Candidate') {
                        <button type="button" class="btn btn--ghost" (click)="decide(m, 'Approve')">Approve</button>
                        <button type="button" class="btn btn--ghost" (click)="decide(m, 'Reject')">Reject</button>
                      }
                      @if (m.status === 'Approved') {
                        <button type="button" class="btn btn--ghost" (click)="execute(m)">Merge now</button>
                      }
                    </span></td>
                  </tr>
                }
              </tbody>
            </table>
          }
        </div>
      </div>
    } @else {
      <div class="grid grid--split">
        <div class="stack">
          @if (creating()) {
            <form class="panel" (ngSubmit)="create()">
              <div class="panel__head"><span class="panel__title">New guest</span></div>
              <div class="panel__body stack">
                <div class="form-grid">
                  <div><label for="g-first">First name</label><input id="g-first" class="input" name="first" [(ngModel)]="draft.first" required /></div>
                  <div><label for="g-last">Last name</label><input id="g-last" class="input" name="last" [(ngModel)]="draft.last" /></div>
                  <div><label for="g-pref">Preferred name</label><input id="g-pref" class="input" name="pref" [(ngModel)]="draft.preferred" /></div>
                  <div><label for="g-email">Email</label><input id="g-email" class="input" type="email" name="email" [(ngModel)]="draft.email" /></div>
                  <div><label for="g-mobile">Mobile</label><input id="g-mobile" class="input" type="tel" name="mobile" [(ngModel)]="draft.mobile" /></div>
                  <div><label for="g-birth">Date of birth</label><input id="g-birth" class="input" type="date" name="birth" [(ngModel)]="draft.birth" /></div>
                </div>
                <label class="row"><input type="checkbox" name="verified" [(ngModel)]="draft.verified" /> Contact details confirmed with the guest in person</label>
                <button type="submit" class="btn btn--primary" [disabled]="busy()">Create guest</button>
              </div>
            </form>
          }

          <div class="toolbar">
            <input class="input" type="search" placeholder="Name, email, phone or queue id" aria-label="Search guests"
              [ngModel]="q()" (ngModelChange)="q.set($event)" (keydown.enter)="search()" />
            <button type="button" class="btn btn--secondary" (click)="search()" [disabled]="q().trim().length < 2">Search</button>
          </div>

          @if (problem(); as p) {
            <app-state-panel [state]="p.status === 403 ? 'denied' : 'error'" [title]="p.title" [body]="p.detail ?? ''" />
          } @else if (hits().length === 0) {
            <app-state-panel state="first-use" title="Find a guest" body="Search by name, by the email or phone they give you, or by their queue id." />
          } @else {
            <div class="panel"><div class="panel__body panel__body--flush">
              <ul class="hits">
                @for (h of hits(); track h.guestId) {
                  <li><button type="button" class="hit" [class.is-picked]="selected()?.guestId === h.guestId" (click)="open(h.guestId)">
                    <strong>{{ h.displayAlias }}</strong>
                    <span class="subtle">{{ h.preferredName ?? '' }} {{ h.legalLastName ?? '' }} · {{ h.publicQueueId }}</span>
                    <span class="subtle">{{ hintsOf(h) }}</span>
                  </button></li>
                }
              </ul>
            </div></div>
          }
        </div>

        <div class="stack">
          @if (selected(); as g) {
            <div class="panel">
              <div class="panel__head">
                <span class="panel__title">{{ g.displayAlias }}</span>
                <span class="badge">{{ g.publicQueueId }}</span>
                @if (g.isMinor) { <span class="badge badge--warn">Minor</span> }
                @if (g.status !== 'Active') { <span class="badge badge--neutral">{{ g.status }}</span> }
              </div>
              <div class="panel__body stack">
                <div class="toolbar">
                  @for (t of tabs; track t) {
                    <button type="button" class="chip" [attr.aria-pressed]="tab() === t" (click)="tab.set(t)">{{ t }}</button>
                  }
                </div>

                @switch (tab()) {
                  @case ('profile') {
                    <div class="form-grid">
                      <div><label for="p-first">First name</label><input id="p-first" class="input" [(ngModel)]="edit.first" [disabled]="!canWrite()" /></div>
                      <div><label for="p-last">Last name</label><input id="p-last" class="input" [(ngModel)]="edit.last" [disabled]="!canWrite()" /></div>
                      <div><label for="p-pref">Preferred name</label><input id="p-pref" class="input" [(ngModel)]="edit.preferred" [disabled]="!canWrite()" /></div>
                      <div><label for="p-alias">Board alias</label><input id="p-alias" class="input" [(ngModel)]="edit.alias" [disabled]="!canWrite()" /></div>
                      @for (k of prefKeys; track k) {
                        <div><label [for]="'pref-' + k">{{ k }}</label>
                          <select [id]="'pref-' + k" [(ngModel)]="edit.prefs[k]" [disabled]="!canWrite()">
                            <option value="">—</option>
                            @for (o of prefOptions[k]; track o) { <option [value]="o">{{ o }}</option> }
                          </select></div>
                      }
                    </div>
                    <p class="subtle">Operational preferences only. Allergies and conditions go on the intake form, where they are encrypted.</p>
                    @if (canWrite()) { <button type="button" class="btn btn--primary" (click)="save(g)" [disabled]="busy()">Save profile</button> }
                  }
                  @case ('contacts') {
                    <ul class="list">
                      @for (c of g.contacts; track c.contactPointId) {
                        <li><span>{{ c.contactType }}{{ c.isPrimary ? ' · primary' : '' }}</span>
                          <span>{{ c.displayHint }} {{ c.verified ? '' : '(unverified)' }}
                            @if (canWrite()) { <button type="button" class="btn btn--ghost" (click)="retire(g, c.contactPointId)">Remove</button> }
                            @if (c.verified && c.contactType === 'Email') { <button type="button" class="btn btn--ghost" (click)="sendLink(g, c.contactPointId)">Send sign-in link</button> }
                          </span></li>
                      } @empty { <li>No contact details.</li> }
                    </ul>
                    @if (canWrite()) {
                      <div class="row">
                        <select [(ngModel)]="newContact.type" aria-label="Contact type"><option>Email</option><option>Mobile</option><option>Phone</option></select>
                        <input class="input" [(ngModel)]="newContact.value" aria-label="Contact value" placeholder="Value" />
                        <label class="row"><input type="checkbox" [(ngModel)]="newContact.verified" /> confirmed in person</label>
                        <button type="button" class="btn btn--secondary" (click)="addContact(g)">Add</button>
                      </div>
                    }
                  }
                  @case ('consent') {
                    <ul class="list">
                      @for (c of consents(); track c.consentId) {
                        <li><span>{{ c.purpose }}{{ c.channel ? ' · ' + c.channel : '' }} (v{{ c.templateVersion }})</span>
                          <span><span class="badge" [class.badge--ok]="c.status === 'Active'">{{ c.status }}</span>
                            @if (c.status === 'Active' && canWrite()) { <button type="button" class="btn btn--ghost" (click)="revokeConsent(g, c)">Revoke</button> }</span></li>
                      } @empty { <li>No consent recorded.</li> }
                    </ul>
                    @if (canWrite()) {
                      <div class="row">
                        <select [(ngModel)]="newConsent.purpose" aria-label="Purpose">
                          @for (p of purposes; track p) { <option>{{ p }}</option> }
                        </select>
                        <select [(ngModel)]="newConsent.channel" aria-label="Channel"><option value="">Any channel</option><option>Email</option><option>Sms</option></select>
                        <input class="input" [(ngModel)]="newConsent.evidence" aria-label="Evidence" placeholder="How was it given?" />
                        <button type="button" class="btn btn--secondary" (click)="recordConsent(g)" [disabled]="!newConsent.evidence">Record</button>
                      </div>
                    }
                  }
                  @case ('delegation') {
                    <ul class="list">
                      @for (d of delegations(); track d.delegationId) {
                        <li><span>{{ d.allowedActions.join(', ') }} · {{ d.informationVisibility }}</span>
                          <span><span class="badge" [class.badge--ok]="d.status === 'Active'">{{ d.status }}</span>
                            @if (d.status === 'Active' && canWrite()) { <button type="button" class="btn btn--ghost" (click)="revokeDelegation(g, d)">Revoke</button> }</span></li>
                      } @empty { <li>Nobody acts for this guest.</li> }
                    </ul>
                    <p class="subtle">To add a delegate, search for them, then use their queue id here. There is no level that shows intake.</p>
                    @if (canWrite()) {
                      <div class="row">
                        <input class="input" [(ngModel)]="newDelegate.queueId" aria-label="Delegate queue id" placeholder="Delegate's queue id" />
                        <input class="input" [(ngModel)]="newDelegate.evidence" aria-label="Evidence" placeholder="Evidence (signed form…)" />
                        <button type="button" class="btn btn--secondary" (click)="grant(g)" [disabled]="!newDelegate.queueId || !newDelegate.evidence">Grant booking for 90 days</button>
                      </div>
                    }
                  }
                  @case ('privacy') {
                    <p class="subtle">Log what the guest asked for. The privacy team verifies and fulfils it; a deletion is irreversible.</p>
                    <div class="row">
                      @for (t of privacyTypes; track t) {
                        <button type="button" class="btn btn--ghost" (click)="openPrivacy(g, t)">{{ t }}</button>
                      }
                    </div>
                  }
                }
              </div>
            </div>
          } @else {
            <app-state-panel state="empty" title="No guest open" body="Pick a guest from the results." />
          }
        </div>
      </div>
    }
  `,
  styles: [`
    :host { display: block; }
    .hits { list-style: none; margin: 0; padding: 0; }
    .hit { display: grid; gap: 2px; width: 100%; text-align: start; padding: var(--space-3); border: 0; border-bottom: 1px solid var(--border-subtle);
      background: transparent; color: var(--fg-default); font: inherit; cursor: pointer; }
    .hit:hover, .hit.is-picked { background: var(--bg-subtle); }
    .list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--space-2); }
    .list li { display: flex; justify-content: space-between; align-items: center; gap: var(--space-3); padding: var(--space-2) var(--space-3); border-radius: var(--radius-sm); background: var(--bg-muted); }
    label { display: block; font-size: var(--text-sm); font-weight: var(--weight-bold); margin-bottom: var(--space-2); }
  `],
})
export class Guests {
  private readonly api = inject(GuestsApi);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);

  protected readonly tabs: readonly Tab[] = ['profile', 'contacts', 'consent', 'delegation', 'privacy'];
  protected readonly prefKeys = Object.keys(PREFS);
  protected readonly prefOptions = PREFS;
  protected readonly purposes = ['Marketing', 'Transactional', 'Photography', 'DataSharing'];
  protected readonly privacyTypes = ['Access', 'Export', 'Correction', 'Deletion'];

  protected readonly q = signal('');
  protected readonly hits = signal<readonly GuestSearchHitDto[]>([]);
  protected readonly selected = signal<GuestDto | null>(null);
  protected readonly consents = signal<readonly ConsentDto[]>([]);
  protected readonly delegations = signal<readonly DelegationDto[]>([]);
  protected readonly merges = signal<readonly MergeCaseDto[]>([]);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly busy = signal(false);
  protected readonly creating = signal(false);
  protected readonly showMerges = signal(false);
  protected readonly tab = signal<Tab>('profile');
  protected readonly canWrite = computed(() => this.auth.has(SCOPES.guestWrite));

  protected draft = { first: '', last: '', preferred: '', email: '', mobile: '', birth: '', verified: true };
  protected edit = { first: '', last: '', preferred: '', alias: '', prefs: {} as Record<string, string> };
  protected newContact = { type: 'Email', value: '', verified: true };
  protected newConsent = { purpose: 'Marketing', channel: 'Email', evidence: '' };
  protected newDelegate = { queueId: '', evidence: '' };

  protected hintsOf(h: GuestSearchHitDto): string {
    return h.contacts.map((c) => c.displayHint).join(' · ');
  }

  protected async search(): Promise<void> {
    if (this.q().trim().length < 2) return;
    await this.run(async () => {
      this.hits.set(await this.api.search(this.q().trim()));
      this.problem.set(null);
    }, true);
  }

  protected async open(id: string): Promise<void> {
    await this.run(async () => {
      const g = await this.api.guest(id);
      this.select(g);
      const [c, d] = await Promise.all([this.api.consents(id), this.api.delegations(id)]);
      this.consents.set(c);
      this.delegations.set(d);
    });
  }

  private select(g: GuestDto): void {
    this.selected.set(g);
    this.edit = {
      first: g.legalFirstName ?? '', last: g.legalLastName ?? '', preferred: g.preferredName ?? '', alias: g.displayAlias ?? '',
      prefs: { ...g.preferences },
    };
  }

  protected async create(): Promise<void> {
    const d = this.draft;
    await this.run(async () => {
      const r = await this.api.create({
        legalFirstName: d.first || undefined, legalLastName: d.last || undefined, preferredName: d.preferred || undefined,
        email: d.email || undefined, mobile: d.mobile || undefined, birthDate: d.birth || undefined, contactsVerifiedInPerson: d.verified,
      });
      this.creating.set(false);
      this.draft = { first: '', last: '', preferred: '', email: '', mobile: '', birth: '', verified: true };
      this.select(r.guest);
      this.hits.set([{ guestId: r.guest.guestId, displayAlias: r.guest.displayAlias, preferredName: r.guest.preferredName,
        legalLastName: r.guest.legalLastName, publicQueueId: r.guest.publicQueueId, contacts: r.guest.contacts }]);
      this.toast.success('Guest created', `${r.guest.displayAlias} · ${r.guest.publicQueueId}`);
      if (r.possibleDuplicates.length > 0)
        this.toast.warn('Possible duplicate', 'Another guest has the same contact details. A manager will review it in the merge queue.');
    });
  }

  protected async save(g: GuestDto): Promise<void> {
    const prefs = Object.fromEntries(Object.entries(this.edit.prefs).filter(([, v]) => !!v));
    await this.run(async () => {
      this.select(await this.api.update(g.guestId, g.rowVersion, {
        legalFirstName: this.edit.first, legalLastName: this.edit.last, preferredName: this.edit.preferred,
        displayAlias: this.edit.alias, preferences: prefs,
      }));
      this.toast.success('Profile saved');
    });
  }

  protected async addContact(g: GuestDto): Promise<void> {
    await this.run(async () => {
      await this.api.addContact(g.guestId, { contactType: this.newContact.type, value: this.newContact.value, verifiedInPerson: this.newContact.verified });
      this.newContact.value = '';
      this.select(await this.api.guest(g.guestId));
      this.toast.success('Contact added');
    });
  }

  protected async retire(g: GuestDto, contactId: string): Promise<void> {
    await this.run(async () => { this.select(await this.api.retireContact(g.guestId, contactId)); this.toast.success('Contact removed'); });
  }

  protected async sendLink(g: GuestDto, contactId: string): Promise<void> {
    await this.run(async () => { await this.api.sendLink(g.guestId, contactId, 'SignIn'); this.toast.success('Sign-in link sent', 'It works once, for 15 minutes.'); });
  }

  protected async recordConsent(g: GuestDto): Promise<void> {
    const c = this.newConsent;
    await this.run(async () => {
      await this.api.recordConsent(g.guestId, { purpose: c.purpose, channel: c.channel || null, templateId: `${c.purpose.toLowerCase()}-v1`, templateVersion: 1, evidence: c.evidence });
      this.newConsent.evidence = '';
      this.consents.set(await this.api.consents(g.guestId));
      this.toast.success('Consent recorded');
    });
  }

  protected async revokeConsent(g: GuestDto, c: ConsentDto): Promise<void> {
    await this.run(async () => { await this.api.revokeConsent(c.consentId, c.rowVersion); this.consents.set(await this.api.consents(g.guestId)); this.toast.success('Consent revoked'); });
  }

  protected async grant(g: GuestDto): Promise<void> {
    await this.run(async () => {
      const found = await this.api.search(this.newDelegate.queueId.trim());
      const delegate = found.find((h) => h.publicQueueId === this.newDelegate.queueId.trim().toUpperCase());
      if (!delegate) { this.toast.warn('No guest with that queue id'); return; }
      await this.api.grantDelegation(g.guestId, {
        delegateGuestId: delegate.guestId, allowedActions: ['Book', 'ViewItinerary'], informationVisibility: 'ItineraryOnly',
        evidenceReference: this.newDelegate.evidence, effectiveToUtc: new Date(Date.now() + 90 * 86400_000).toISOString(),
      });
      this.newDelegate = { queueId: '', evidence: '' };
      this.delegations.set(await this.api.delegations(g.guestId));
      this.toast.success('Delegation granted', `${delegate.displayAlias} may book for this guest for 90 days.`);
    });
  }

  protected async revokeDelegation(g: GuestDto, d: DelegationDto): Promise<void> {
    await this.run(async () => { await this.api.revokeDelegation(d.delegationId, d.rowVersion); this.delegations.set(await this.api.delegations(g.guestId)); this.toast.success('Delegation revoked'); });
  }

  protected async openPrivacy(g: GuestDto, type: string): Promise<void> {
    await this.run(async () => { await this.api.openPrivacy(g.guestId, type); this.toast.success(`${type} request logged`, 'The privacy team has 30 days to answer it.'); });
  }

  protected async toggleMerges(): Promise<void> {
    this.showMerges.update((v) => !v);
    if (this.showMerges()) await this.loadMerges();
  }

  private async loadMerges(): Promise<void> {
    await this.run(async () => {
      const [c, a] = await Promise.all([this.api.merges('Candidate'), this.api.merges('Approved')]);
      this.merges.set([...c, ...a]);
    });
  }

  protected async decide(m: MergeCaseDto, decision: 'Approve' | 'Reject'): Promise<void> {
    await this.run(async () => { await this.api.decideMerge(m.mergeCaseId, m.rowVersion, decision); this.toast.success(decision === 'Approve' ? 'Merge approved' : 'Merge rejected'); await this.loadMerges(); });
  }

  protected async execute(m: MergeCaseDto): Promise<void> {
    await this.run(async () => { await this.api.executeMerge(m.mergeCaseId, m.rowVersion); this.toast.success('Guests merged', 'The duplicate now points at the kept record. A split can undo it.'); await this.loadMerges(); });
  }

  private async run(work: () => Promise<void>, surface = false): Promise<void> {
    this.busy.set(true);
    try {
      await work();
    } catch (err) {
      const p = err as ApiProblem;
      if (surface) this.problem.set(p);
      else this.toast.error('That did not work', p.detail ?? p.title, p.correlationId);
    } finally {
      this.busy.set(false);
    }
  }
}
