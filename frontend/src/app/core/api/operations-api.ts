import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { AppointmentDto, PageDto } from '../models/api';
import type {
  ArrivalsDto, BulkMoveItemRequest, BulkMoveResponseDto, CreateWaitlistRequest, TurnaroundDto,
  TurnaroundResult, VisitDto, VisitStatus, VisitType, WaitlistDto, WaitlistStatus,
} from '../models/operations';

/**
 * Visits, the waitlist, room turnover, the desk's arrivals list, bulk moves
 * and undo. One method per endpoint, nothing else — like SchedulingApi.
 * Every consequential write takes the version it asserts as If-Match.
 */
@Injectable({ providedIn: 'root' })
export class OperationsApi {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  /* ------------------------------ arrivals ------------------------------ */

  /** [spa.read] Today's arrivals with room readiness and intake status. */
  arrivals(date: string): Promise<ArrivalsDto> {
    return this.get('/front-desk/arrivals', { date });
  }

  /* ------------------------------- visits ------------------------------- */

  visits(date: string, status?: VisitStatus): Promise<PageDto<VisitDto>> {
    return this.get('/visits', status ? { date, status } : { date });
  }

  visit(id: string): Promise<VisitDto> {
    return this.get(`/visits/${encodeURIComponent(id)}`);
  }

  createVisit(body: { guestId: string; visitDate: string; visitType: VisitType; scheduledArrivalUtc?: string; notes?: string }): Promise<VisitDto> {
    return this.post('/visits', body);
  }

  transitionVisit(id: string, rowVersion: number, to: VisitStatus, reason?: string): Promise<VisitDto> {
    return this.post(`/visits/${encodeURIComponent(id)}/transitions`, { to, reason: reason ?? null }, rowVersion);
  }

  /* ------------------------------ waitlist ------------------------------ */

  waitlist(status?: WaitlistStatus): Promise<PageDto<WaitlistDto>> {
    return this.get('/waitlist', status ? { status } : {});
  }

  waitlistCandidates(startUtc: string, endUtc: string, serviceId?: string): Promise<readonly WaitlistDto[]> {
    return this.get('/waitlist/candidates', serviceId ? { startUtc, endUtc, serviceId } : { startUtc, endUtc });
  }

  addToWaitlist(body: CreateWaitlistRequest): Promise<WaitlistDto> {
    return this.post('/waitlist', body);
  }

  offerWaitlist(id: string, rowVersion: number, minutes: number): Promise<WaitlistDto> {
    return this.post(`/waitlist/${encodeURIComponent(id)}/offer`, { minutes }, rowVersion);
  }

  acceptWaitlist(id: string, rowVersion: number, appointmentId: string): Promise<WaitlistDto> {
    return this.post(`/waitlist/${encodeURIComponent(id)}/accept`, { appointmentId }, rowVersion);
  }

  cancelWaitlist(id: string, rowVersion: number): Promise<WaitlistDto> {
    return this.post(`/waitlist/${encodeURIComponent(id)}/cancel`, {}, rowVersion);
  }

  /* ----------------------------- turnaround ----------------------------- */

  turnaround(open = true): Promise<PageDto<TurnaroundDto>> {
    return this.get('/turnaround', { open: String(open) });
  }

  startTurnaround(id: string, rowVersion: number): Promise<TurnaroundDto> {
    return this.post(`/turnaround/${encodeURIComponent(id)}/start`, {}, rowVersion);
  }

  completeTurnaround(id: string, rowVersion: number, result: TurnaroundResult, checklistCode?: string): Promise<TurnaroundDto> {
    return this.post(`/turnaround/${encodeURIComponent(id)}/complete`, { result, checklistCode: checklistCode ?? null }, rowVersion);
  }

  skipTurnaround(id: string, rowVersion: number, reason: string): Promise<TurnaroundDto> {
    return this.post(`/turnaround/${encodeURIComponent(id)}/skip`, { reason }, rowVersion);
  }

  /* ---------------------------- moves and undo --------------------------- */

  /** [spa.schedule] CON-006: the token that committed the reassign undoes it, once, inside the window. */
  undoReassign(appointmentId: string, token: string, idempotencyKey: string): Promise<AppointmentDto> {
    return firstValueFrom(this.http.post<AppointmentDto>(
      `${this.base}/appointments/${encodeURIComponent(appointmentId)}/undo-reassign`, { token },
      { headers: { 'Idempotency-Key': idempotencyKey } }));
  }

  /** [spa.schedule] All or nothing; dryRun evaluates against the post-move board and writes nothing. */
  bulkMove(moves: readonly BulkMoveItemRequest[], reason: string | null, dryRun: boolean, idempotencyKey?: string): Promise<BulkMoveResponseDto> {
    return firstValueFrom(this.http.post<BulkMoveResponseDto>(`${this.base}/schedule/bulk-move`, { moves, reason, dryRun },
      idempotencyKey ? { headers: { 'Idempotency-Key': idempotencyKey } } : {}));
  }

  /* ------------------------------ plumbing ------------------------------ */

  private get<T>(path: string, params: Record<string, string> = {}): Promise<T> {
    return firstValueFrom(this.http.get<T>(`${this.base}${path}`, { params }));
  }

  private post<T>(path: string, body: unknown, rowVersion?: number): Promise<T> {
    return firstValueFrom(this.http.post<T>(`${this.base}${path}`, body,
      rowVersion === undefined ? {} : { headers: { 'If-Match': `"${rowVersion}"` } }));
  }
}
