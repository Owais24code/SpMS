import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { newCorrelationId } from '../http/correlation.interceptor';
import type {
  AppointmentDto, AppointmentQuery, AuditPageDto, AvailabilityDto,
  CatalogServiceDto, CreateAppointmentRequest, HealthDto, PageDto,
  PreflightRequest, PreflightResponseDto, ReassignRequest, TransitionRequest,
} from '../models/api';

/**
 * One method per endpoint and nothing else.
 *
 * No retries, no caching, no conflict interpretation, no view mapping: the
 * store owns all of that. Keeping this file free of business logic is what
 * lets it be read against Contracts.cs line by line.
 *
 * Failures arrive as the normalised ApiProblem thrown by the problem+json
 * interceptor, so every method here rejects with an ApiProblem — never an
 * HttpErrorResponse.
 */
@Injectable({ providedIn: 'root' })
export class SchedulingApi {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  /* ------------------------------- health -------------------------------- */

  health(): Promise<HealthDto> {
    return this.get<HealthDto>('/health');
  }

  live(): Promise<HealthDto> {
    return this.get<HealthDto>('/health/live');
  }

  ready(): Promise<HealthDto> {
    return this.get<HealthDto>('/health/ready');
  }

  /* ---------------------------- reference data --------------------------- */

  /** [spa.read] A bare array, not a page — the catalogue is small and fixed. */
  services(): Promise<readonly CatalogServiceDto[]> {
    return this.get<readonly CatalogServiceDto[]>('/services');
  }

  /* ---------------------------- appointments ----------------------------- */

  /**
   * [spa.read] Either `date`, or `from` and `to` together. The server refuses
   * a window over 62 days, so a caller wanting more has to page by window as
   * well as by offset.
   */
  appointments(query: AppointmentQuery): Promise<PageDto<AppointmentDto>> {
    const params: Record<string, string> = {};
    if (query.date !== undefined) params['date'] = query.date;
    if (query.from !== undefined) params['from'] = query.from;
    if (query.to !== undefined) params['to'] = query.to;
    if (query.offset !== undefined) params['offset'] = String(query.offset);
    if (query.limit !== undefined) params['limit'] = String(query.limit);
    return firstValueFrom(
      this.http.get<PageDto<AppointmentDto>>(this.url('/appointments'), { params }));
  }

  /**
   * [spa.read] The DTO carries `eTag` as a field, so the response header is
   * not needed to obtain the version — which is why this returns the body.
   */
  appointment(id: string): Promise<AppointmentDto> {
    return this.get<AppointmentDto>(`/appointments/${encodeURIComponent(id)}`);
  }

  /**
   * [spa.write] 201 with Location and ETag.
   *
   * `idempotencyKey` is the CALLER's, not generated here: a key minted inside
   * this method would be new on every attempt, which is precisely the failure
   * idempotency exists to prevent. See reassign() for the rule.
   */
  createAppointment(
    body: CreateAppointmentRequest,
    idempotencyKey: string,
  ): Promise<AppointmentDto> {
    return firstValueFrom(
      this.http.post<AppointmentDto>(this.url('/appointments'), body, {
        headers: { 'Idempotency-Key': idempotencyKey },
      }));
  }

  /**
   * [spa.write] Lifecycle transition.
   *
   * If-Match is required and must carry the version read, quoted. A wildcard
   * is refused by the server, so `rowVersion` is passed as a number and quoted
   * here rather than letting a call site hand over `*`.
   */
  transition(id: string, rowVersion: number, body: TransitionRequest): Promise<AppointmentDto> {
    return firstValueFrom(
      this.http.post<AppointmentDto>(
        this.url(`/appointments/${encodeURIComponent(id)}/transitions`), body, {
          headers: { 'If-Match': `"${rowVersion}"` },
        }));
  }

  /* ----------------------------- availability ---------------------------- */

  /**
   * [spa.read] The half-hourly grid for one property-local day.
   *
   * This is the only endpoint that publishes the property's time zone next to
   * the day's slots, which makes it the board's source for the business day.
   */
  availability(date: string, serviceId?: string): Promise<AvailabilityDto> {
    const params: Record<string, string> = { date };
    if (serviceId !== undefined) params['serviceId'] = serviceId;
    return firstValueFrom(
      this.http.get<AvailabilityDto>(this.url('/availability'), { params }));
  }

  /* ------------------------------ scheduling ----------------------------- */

  /**
   * [spa.schedule] Validates a proposal and returns a token. Writes nothing.
   *
   * Always 200 when the request is well formed: conflicts are information, and
   * refusing here would deny the operator the alternatives CON-002 requires
   * them to be shown. A stale `fromRowVersion` is the exception — that is a
   * 412 with the current record attached.
   */
  preflight(body: PreflightRequest): Promise<PreflightResponseDto> {
    return firstValueFrom(
      this.http.post<PreflightResponseDto>(this.url('/schedule/preflight'), body));
  }

  /**
   * [spa.schedule] Commits a preflighted move.
   *
   * The key belongs to one logical attempt and must be REUSED when that same
   * attempt is retried — a new key on retry makes the replay unreachable and
   * can book the move twice. It must NOT be reused when the body changes: the
   * server hashes method, path and body, so retrying with a newly supplied
   * reason under the old key answers IDEMPOTENCY_MISMATCH. `keyFor` below is
   * how the store keeps both halves of that true.
   */
  reassign(id: string, body: ReassignRequest, idempotencyKey: string): Promise<AppointmentDto> {
    return firstValueFrom(
      this.http.post<AppointmentDto>(
        this.url(`/appointments/${encodeURIComponent(id)}/reassign`), body, {
          headers: { 'Idempotency-Key': idempotencyKey },
        }));
  }

  /* -------------------------------- audit -------------------------------- */

  /** [spa.admin] Scoped to the caller's tenant AND property, server-side. */
  audit(limit = 50): Promise<AuditPageDto> {
    return firstValueFrom(
      this.http.get<AuditPageDto>(this.url('/audit'), { params: { limit: String(limit) } }));
  }

  /* -------------------------------- plumbing ----------------------------- */

  private get<T>(path: string): Promise<T> {
    return firstValueFrom(this.http.get<T>(this.url(path)));
  }

  private url(path: string): string {
    return `${this.base}${path}`;
  }
}

/**
 * Idempotency keys, one per distinct request body.
 *
 * `identity` names the logical operation including everything that goes into
 * the body — for a reassign that is the token AND the reason. Ask twice with
 * the same identity and you get the same key back, which is what makes a
 * retry a replay. Ask with a reason the operator has just typed and you get a
 * new key, because the body differs and the server would otherwise refuse the
 * request as a mismatch.
 */
@Injectable({ providedIn: 'root' })
export class IdempotencyKeys {
  private readonly keys = new Map<string, string>();

  keyFor(identity: string): string {
    const existing = this.keys.get(identity);
    if (existing !== undefined) return existing;

    const key = newCorrelationId();
    this.keys.set(identity, key);

    // Bounded: a long shift at the front desk would otherwise grow this map
    // for the life of the tab. The oldest entries are ones nothing can still
    // be retrying.
    if (this.keys.size > 200) {
      const oldest = this.keys.keys().next().value;
      if (oldest !== undefined) this.keys.delete(oldest);
    }
    return key;
  }

  /** Called once an attempt has reached a terminal outcome. */
  forget(identity: string): void {
    this.keys.delete(identity);
  }
}
