import { HttpErrorResponse, type HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import type { AppointmentDto, ConflictDto } from '../models/api';
import type { ApiProblem, FieldViolation } from '../models/api-problem';

/**
 * Normalises every failure into one ApiProblem.
 *
 * Without this, each call site ends up re-deriving "was that a 412 with a
 * usable body, or a proxy's HTML, or the network dropping" — and the ones
 * written last get it wrong. Here the shape is guaranteed: a caller can read
 * `problem.code` and `problem.conflicts` without a single narrowing check.
 *
 * Nothing is inferred that the server did not say. A body that is not
 * problem+json produces INTERNAL_ERROR or NETWORK_UNREACHABLE rather than a
 * guessed code, because inventing STALE_VERSION from a 412 with no body would
 * send the UI down a refresh path with nothing to refresh from.
 */
export const problemInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    catchError((err: unknown) =>
      throwError(() => toProblem(err, req.headers.get('X-Correlation-Id')))),
  );

/** Exported for the store's tests and for anything that has a raw failure. */
export const toProblem = (err: unknown, sentCorrelationId: string | null): ApiProblem => {
  if (!(err instanceof HttpErrorResponse)) {
    return base({
      title: 'The request could not be sent',
      status: 0,
      code: 'NETWORK_UNREACHABLE',
      detail: 'The request never left the browser.',
      correlationId: sentCorrelationId ?? 'unknown',
      retryable: true,
    });
  }

  // Status 0 is the browser refusing to tell us why: offline, DNS, a CORS
  // preflight rejection. Calling it INTERNAL_ERROR would blame the server for
  // a request it never received.
  if (err.status === 0) {
    return base({
      title: 'No response from the API',
      status: 0,
      code: 'NETWORK_UNREACHABLE',
      detail: 'The API did not answer. It may be unreachable, or blocked by CORS.',
      correlationId: correlationOf(err) ?? sentCorrelationId ?? 'unknown',
      retryable: true,
    });
  }

  const body: Record<string, unknown> | null = isRecord(err.error) ? err.error : null;

  if (body === null || typeof body['code'] !== 'string') {
    return base({
      title: err.statusText || 'Request failed',
      status: err.status,
      code: err.status >= 500 ? 'INTERNAL_ERROR' : 'UNEXPECTED_RESPONSE',
      detail: 'The API answered with something other than problem+json.',
      correlationId: correlationOf(err) ?? sentCorrelationId ?? 'unknown',
      // 5xx and 429 are worth another attempt; a 4xx with no body is not.
      retryable: err.status >= 500 || err.status === 429,
    });
  }

  return {
    type: str(body['type']) ?? '',
    title: str(body['title']) ?? err.statusText,
    // The BODY's status, not the response's: they agree, and the body is what
    // the catalogue documents.
    status: num(body['status']) ?? err.status,
    code: body['code'] as string,
    detail: str(body['detail']),
    correlationId:
      str(body['correlation_id']) ?? correlationOf(err) ?? sentCorrelationId ?? 'unknown',
    retryable: body['retryable'] === true,
    retryAfterSeconds: num(body['retry_after_seconds']),
    fieldViolations: violations(body['field_violations']),
    current: isRecord(body['current']) ? (body['current'] as unknown as AppointmentDto) : null,
    conflicts: conflicts(body['conflicts']),
    conflictsWhenShown: conflicts(body['conflicts_when_shown']),
    token: str(body['token']),
    expiresUtc: str(body['expires_utc']),
  };
};

/* ---- small readers. Every one of them tolerates the field being absent ---- */

const isRecord = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null && !Array.isArray(v);

const str = (v: unknown): string | null => (typeof v === 'string' ? v : null);
const num = (v: unknown): number | null => (typeof v === 'number' ? v : null);

const correlationOf = (err: HttpErrorResponse): string | null =>
  err.headers?.get('X-Correlation-Id') ?? null;

const violations = (v: unknown): readonly FieldViolation[] | null => {
  if (!Array.isArray(v)) return null;
  const rows = v
    .filter(isRecord)
    .filter((r) => typeof r['field'] === 'string' && typeof r['rule'] === 'string')
    .map((r) => ({ field: r['field'] as string, rule: r['rule'] as string }));
  return rows.length > 0 ? rows : null;
};

/**
 * Conflicts are passed through without de-duplication. Two CON-004 entries
 * differing only in `rule` are two different breaches with two different sets
 * of resolutions, and collapsing them hides one.
 */
const conflicts = (v: unknown): readonly ConflictDto[] | null => {
  if (!Array.isArray(v)) return null;
  const rows = v.filter(isRecord).filter((r) => typeof r['code'] === 'string');
  return rows.length > 0 ? (rows as unknown as readonly ConflictDto[]) : null;
};

const base = (
  p: Pick<ApiProblem, 'title' | 'status' | 'code' | 'detail' | 'correlationId' | 'retryable'>,
): ApiProblem => ({
  type: '',
  retryAfterSeconds: null,
  fieldViolations: null,
  current: null,
  conflicts: null,
  conflictsWhenShown: null,
  token: null,
  expiresUtc: null,
  ...p,
});
