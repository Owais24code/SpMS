import type { HttpInterceptorFn } from '@angular/common/http';

/**
 * Puts a fresh X-Correlation-Id on every request.
 *
 * The server echoes it into the response header, every log line for the
 * request and the `correlation_id` of any problem+json body, so a support
 * report carries one id that reaches the server log. Per REQUEST, not per
 * session: an id shared by a day's traffic identifies nothing.
 */
export const correlationInterceptor: HttpInterceptorFn = (req, next) =>
  next(req.clone({ setHeaders: { 'X-Correlation-Id': newCorrelationId() } }));

/**
 * A v4 uuid.
 *
 * crypto.randomUUID needs a secure context, which http://<lan-ip>:4200 is not,
 * so the fallback is not theoretical. Math.random is acceptable here and only
 * here: this value is a log key, never a token or an idempotency key.
 */
export const newCorrelationId = (): string => {
  try {
    if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
      return crypto.randomUUID();
    }
  } catch {
    /* fall through to the arithmetic form */
  }
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (ch) => {
    const r = (Math.random() * 16) | 0;
    return (ch === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
};
