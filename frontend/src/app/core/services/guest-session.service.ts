import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { GuestIntakeDto } from '../models/guests';

export interface GuestSessionDto {
  readonly accessToken: string;
  readonly tokenType: 'Bearer';
  readonly expiresUtc: string;
  readonly purpose: 'SignIn' | 'ManageBooking' | 'CompleteIntake' | 'Pay';
  readonly scopeEntityType: string | null;
  readonly scopeEntityId: string | null;
  readonly guest: { readonly displayName: string | null };
}

export interface GuestMeDto {
  readonly guestId: string;
  readonly preferredName: string | null;
  readonly displayAlias: string | null;
  readonly locale: string | null;
  readonly homePropertyId: string | null;
  readonly purpose: string | null;
  readonly contacts: readonly { contactType: string; displayHint: string; isPrimary: boolean; verified: boolean }[];
}

const KEY = 'spms-guest-session';

/**
 * A guest's magic-link session (SEC-010/011).
 *
 * The link is single use: redeeming it consumes it on the server, so the
 * session token is the only thing kept, in sessionStorage — it dies with the
 * tab, and it expires on the server's clock regardless.
 */
@Injectable({ providedIn: 'root' })
export class GuestSession {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;
  private readonly _session = signal<GuestSessionDto | null>(this.read());

  readonly session = this._session.asReadonly();
  readonly active = computed(() => {
    const s = this._session();
    return s !== null && Date.parse(s.expiresUtc) > Date.now();
  });

  token(): string | null {
    return this.active() ? this._session()!.accessToken : null;
  }

  /** Exchanges the link token for a session. Rejects with the API problem when the link is spent. */
  async redeem(linkToken: string): Promise<GuestSessionDto> {
    const s = await firstValueFrom(this.http.post<GuestSessionDto>(`${this.base}/guest/sessions`, { token: linkToken }));
    this._session.set(s);
    try { sessionStorage.setItem(KEY, JSON.stringify(s)); } catch { /* memory only */ }
    return s;
  }

  me(): Promise<GuestMeDto> {
    return firstValueFrom(this.http.get<GuestMeDto>(`${this.base}/guest/me`));
  }

  /** The guest's own intake forms for upcoming bookings, with their answers so far. */
  intake(): Promise<readonly GuestIntakeDto[]> {
    return firstValueFrom(this.http.get<readonly GuestIntakeDto[]>(`${this.base}/guest/intake`));
  }

  /** Saves a draft, or submits (every required answer and confirmation present). */
  saveIntake(submissionId: string, rowVersion: number, answers: Record<string, unknown>, submit: boolean): Promise<GuestIntakeDto> {
    return firstValueFrom(this.http.put<GuestIntakeDto>(`${this.base}/guest/intake/${submissionId}`, { answers, submit },
      { headers: { 'If-Match': `"${rowVersion}"` } }));
  }

  /** Always resolves the same way, whether or not the address is on file. */
  requestLink(email: string): Promise<unknown> {
    const site = environment.guestSite;
    return firstValueFrom(this.http.post(`${this.base}/guest/magic-links/request`,
      { tenant: site.tenant, property: site.property, email }));
  }

  end(): void {
    this._session.set(null);
    try { sessionStorage.removeItem(KEY); } catch { /* ignore */ }
  }

  private read(): GuestSessionDto | null {
    try {
      const raw = sessionStorage.getItem(KEY);
      return raw ? (JSON.parse(raw) as GuestSessionDto) : null;
    } catch {
      return null;
    }
  }
}
