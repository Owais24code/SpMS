import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import type { GuestIntakeDto } from '../../core/models/guests';
import { Router, RouterLink } from '@angular/router';
import { GuestSession, type GuestMeDto } from '../../core/services/guest-session.service';

/**
 * /guest — the signed-in guest's own page. Everything shown comes from
 * GET /guest/me, which answers only for the guest the session was issued to;
 * contact values arrive masked.
 */
@Component({
  selector: 'app-guest-portal',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './guest.scss',
  template: `
    <main class="panel" id="main">
      <p class="panel__brand">AARFID SpMS</p>
      @if (me(); as g) {
        <h1 class="panel__title">Hello{{ g.preferredName ? ', ' + g.preferredName : '' }}</h1>
        <p class="panel__sub">You're signed in until {{ expires() }}.</p>

        <h2 class="panel__sub"><strong>How we reach you</strong></h2>
        <ul class="list">
          @for (c of g.contacts; track c.displayHint) {
            <li><span>{{ c.contactType }}</span><span>{{ c.displayHint }}{{ c.verified ? '' : ' (unverified)' }}</span></li>
          } @empty {
            <li>No contact details on file.</li>
          }
        </ul>
        @for (f of forms(); track f.submissionId) {
          <section class="intake" [attr.aria-labelledby]="'form-' + f.submissionId">
            <h2 class="panel__sub" [id]="'form-' + f.submissionId"><strong>{{ f.formTitle }}</strong> — {{ f.serviceName }}{{ f.appointmentStartUtc ? ', ' + when(f.appointmentStartUtc) : '' }}</h2>
            @if (!f.editable) {
              <p class="panel__ok" role="status">{{ f.status === 'Submitted' || f.status === 'Locked' || f.status === 'Reviewed' ? 'Thank you — your answers are with your therapist.' : 'This form can no longer be changed. Tell the front desk if anything is different.' }}</p>
            } @else {
              @if (errors()[f.submissionId]; as e) { <p class="panel__error" role="alert">{{ e }}</p> }
              @for (q of f.fields; track q.key) {
                <label class="field">
                  <span>{{ q.label }}{{ q.required || q.mustBeTrue ? ' *' : '' }}</span>
                  @switch (q.type) {
                    @case ('boolean') {
                      <select [value]="val(f, q.key)" (change)="set(f, q.key, $any($event.target).value)">
                        <option value="">Choose…</option><option value="true">Yes</option><option value="false">No</option>
                      </select>
                    }
                    @case ('select') {
                      <select [value]="val(f, q.key)" (change)="set(f, q.key, $any($event.target).value)">
                        <option value="">Choose…</option>
                        @for (o of q.options ?? []; track o) { <option [value]="o">{{ o }}</option> }
                      </select>
                    }
                    @default {
                      <input [value]="val(f, q.key)" [attr.maxlength]="q.maxLength ?? 1000" (input)="set(f, q.key, $any($event.target).value)" />
                    }
                  }
                </label>
              }
              <div class="actions">
                <button type="button" class="btn" (click)="save(f, false)" [disabled]="saving()">Save for later</button>
                <button type="button" class="btn btn--primary" (click)="save(f, true)" [disabled]="saving()">Submit</button>
              </div>
            }
          </section>
        }

        <div class="actions">
          <button type="button" class="btn" (click)="signOut()">Sign out</button>
        </div>
      } @else if (error()) {
        <h1 class="panel__title">Your session has ended</h1>
        <p class="panel__sub">{{ error() }}</p>
        <div class="actions"><a class="btn btn--primary" routerLink="/guest/sign-in">Send me a new link</a></div>
      } @else {
        <p class="panel__sub" role="status">Loading…</p>
      }
    </main>
  `,
})
export class GuestPortal implements OnInit {
  private readonly guest = inject(GuestSession);
  private readonly router = inject(Router);
  protected readonly me = signal<GuestMeDto | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly forms = signal<readonly GuestIntakeDto[]>([]);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  /** Answers being edited, per form, as the form's own types (booleans stay booleans). */
  private readonly drafts = new Map<string, Record<string, unknown>>();

  protected expires(): string {
    const s = this.guest.session();
    return s ? new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(new Date(s.expiresUtc)) : '';
  }

  async ngOnInit(): Promise<void> {
    if (!this.guest.active()) {
      this.error.set('Sign-in links last a short time. Ask for a new one to continue.');
      return;
    }
    try {
      this.me.set(await this.guest.me());
      await this.loadForms();
    } catch (err) {
      this.error.set((err as { detail?: string | null })?.detail ?? 'We could not load your details.');
    }
  }

  private async loadForms(): Promise<void> {
    const forms = await this.guest.intake();
    for (const f of forms) this.drafts.set(f.submissionId, { ...(f.answers ?? {}) });
    this.forms.set(forms);
  }

  protected when(utc: string): string {
    return new Intl.DateTimeFormat(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }).format(new Date(utc));
  }

  protected val(f: GuestIntakeDto, key: string): string {
    const v = this.drafts.get(f.submissionId)?.[key];
    return v === undefined || v === null ? '' : String(v);
  }

  protected set(f: GuestIntakeDto, key: string, raw: string): void {
    const field = f.fields.find((x) => x.key === key)!;
    const d = this.drafts.get(f.submissionId) ?? {};
    if (raw === '') delete d[key];
    else d[key] = field.type === 'boolean' ? raw === 'true' : raw;
    this.drafts.set(f.submissionId, d);
  }

  protected async save(f: GuestIntakeDto, submit: boolean): Promise<void> {
    this.saving.set(true);
    this.errors.update((e) => ({ ...e, [f.submissionId]: '' }));
    try {
      const next = await this.guest.saveIntake(f.submissionId, f.rowVersion, this.drafts.get(f.submissionId) ?? {}, submit);
      this.forms.update((list) => list.map((x) => (x.submissionId === next.submissionId ? next : x)));
      this.drafts.set(next.submissionId, { ...(next.answers ?? {}) });
    } catch (err) {
      const p = err as { detail?: string | null; fieldViolations?: readonly { field: string }[] | null };
      const fields = (p.fieldViolations ?? []).map((v) => f.fields.find((x) => x.key === v.field)?.label ?? v.field);
      this.errors.update((e) => ({ ...e, [f.submissionId]: fields.length ? `Please answer: ${fields.join('; ')}` : p.detail ?? 'That did not save.' }));
    } finally {
      this.saving.set(false);
    }
  }

  protected signOut(): void {
    this.guest.end();
    void this.router.navigateByUrl('/guest/sign-in');
  }
}
