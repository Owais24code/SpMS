import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import type {
  ConsentDto, CreateGuestRequest, CreateGuestResponse, DelegationDto, GuestDto, GuestSearchHitDto, IntakeSummaryDto,
  MergeCaseDto, NoteDto, PrivacyRequestDto,
} from '../models/guests';

/** Guests, merges, delegation, consent, privacy, and the staff side of intake. One method per endpoint. */
@Injectable({ providedIn: 'root' })
export class GuestsApi {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  search(q: string): Promise<readonly GuestSearchHitDto[]> { return this.get('/guests', { q }); }
  guest(id: string): Promise<GuestDto> { return this.get(`/guests/${id}`); }
  create(body: CreateGuestRequest): Promise<CreateGuestResponse> { return this.post('/guests', body); }
  update(id: string, rowVersion: number, body: Record<string, unknown>): Promise<GuestDto> {
    return firstValueFrom(this.http.patch<GuestDto>(`${this.base}/guests/${id}`, body, { headers: { 'If-Match': `"${rowVersion}"` } }));
  }
  addContact(id: string, body: { contactType: string; value: string; isPrimary?: boolean; verifiedInPerson?: boolean }): Promise<unknown> {
    return this.post(`/guests/${id}/contact-points`, body);
  }
  retireContact(id: string, contactId: string): Promise<GuestDto> { return this.post(`/guests/${id}/contact-points/${contactId}/retire`, {}); }
  sendLink(id: string, contactPointId: string, purpose: string): Promise<{ devToken?: string | null }> {
    return this.post(`/guests/${id}/magic-links`, { contactPointId, purpose });
  }

  merges(status?: string): Promise<readonly MergeCaseDto[]> { return this.get('/guest-merge-cases', status ? { status } : {}); }
  proposeMerge(survivingGuestId: string, duplicateGuestId: string, reason?: string): Promise<MergeCaseDto> {
    return this.post('/guest-merge-cases', { survivingGuestId, duplicateGuestId, reason });
  }
  decideMerge(id: string, rowVersion: number, decision: 'Approve' | 'Reject', reason?: string): Promise<MergeCaseDto> {
    return this.post(`/guest-merge-cases/${id}/decision`, { decision, reason }, rowVersion);
  }
  executeMerge(id: string, rowVersion: number): Promise<MergeCaseDto> { return this.post(`/guest-merge-cases/${id}/execute`, {}, rowVersion); }

  delegations(id: string): Promise<readonly DelegationDto[]> { return this.get(`/guests/${id}/delegations`); }
  grantDelegation(id: string, body: Record<string, unknown>): Promise<DelegationDto> { return this.post(`/guests/${id}/delegations`, body); }
  revokeDelegation(id: string, rowVersion: number): Promise<DelegationDto> { return this.post(`/delegations/${id}/revoke`, {}, rowVersion); }

  consents(id: string): Promise<readonly ConsentDto[]> { return this.get(`/guests/${id}/consents`); }
  recordConsent(id: string, body: Record<string, unknown>): Promise<ConsentDto> { return this.post(`/guests/${id}/consents`, body); }
  revokeConsent(id: string, rowVersion: number): Promise<ConsentDto> { return this.post(`/consents/${id}/revoke`, {}, rowVersion); }

  openPrivacy(id: string, requestType: string): Promise<PrivacyRequestDto> { return this.post(`/guests/${id}/privacy-requests`, { requestType }); }

  /* the staff side of intake */
  intakeSummary(appointmentId: string): Promise<IntakeSummaryDto> { return this.get(`/appointments/${appointmentId}/intake`); }
  acknowledgeIntake(appointmentId: string): Promise<IntakeSummaryDto> { return this.post(`/appointments/${appointmentId}/intake/acknowledge`, {}); }
  notes(appointmentId: string): Promise<readonly NoteDto[]> { return this.get(`/appointments/${appointmentId}/notes`); }
  writeNote(appointmentId: string, content: string, templateCode?: string): Promise<NoteDto> {
    return this.post(`/appointments/${appointmentId}/notes`, { content, templateCode: templateCode ?? null, authoredUtc: new Date().toISOString() });
  }
  amendNote(noteId: string, content: string, reason: string): Promise<NoteDto> { return this.post(`/treatment-notes/${noteId}/amend`, { content, reason }); }

  private get<T>(path: string, params: Record<string, string> = {}): Promise<T> {
    return firstValueFrom(this.http.get<T>(`${this.base}${path}`, { params }));
  }

  private post<T>(path: string, body: unknown, rowVersion?: number): Promise<T> {
    return firstValueFrom(this.http.post<T>(`${this.base}${path}`, body,
      rowVersion === undefined ? {} : { headers: { 'If-Match': `"${rowVersion}"` } }));
  }
}
