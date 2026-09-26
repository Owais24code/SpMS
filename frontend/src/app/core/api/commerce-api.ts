import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import type {
  IntentDto, LineKind, OrderDto, OrderSummaryDto, PaymentResponse, ReconciliationDto, RefundDto, Tender,
} from '../models/commerce';

/** A fresh key per payment attempt; a retry of the same attempt reuses it. */
export function newIdempotencyKey(): string {
  return `web-${crypto.randomUUID()}`;
}

/**
 * Orders, payments, deposits, refunds and reconciliation. One method per
 * endpoint. Money writes carry an Idempotency-Key the caller keeps for the
 * attempt, so a retried click is the same payment, never a second one.
 */
@Injectable({ providedIn: 'root' })
export class CommerceApi {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  orders(query: { date?: string; status?: string; guestId?: string } = {}): Promise<readonly OrderSummaryDto[]> {
    return this.get('/orders', query);
  }
  order(id: string): Promise<OrderDto> { return this.get(`/orders/${id}`); }
  createOrder(body: { guestId?: string; visitId?: string; appointmentIds?: readonly string[] }): Promise<OrderDto> {
    return this.post('/orders', body);
  }
  addLine(id: string, body: { lineKind: LineKind; description?: string; unitPriceMinor?: number; quantity?: number; serviceId?: string; appointmentId?: string }): Promise<OrderDto> {
    return this.post(`/orders/${id}/lines`, body);
  }
  removeLine(id: string, lineId: string): Promise<OrderDto> {
    return firstValueFrom(this.http.delete<OrderDto>(`${this.base}/orders/${id}/lines/${lineId}`));
  }
  place(id: string, rowVersion: number): Promise<OrderDto> { return this.post(`/orders/${id}/place`, {}, { rowVersion }); }
  voidOrder(id: string, rowVersion: number, reason: string): Promise<OrderDto> { return this.post(`/orders/${id}/void`, { reason }, { rowVersion }); }

  pay(id: string, key: string, body: { tenderCode: Tender; amountMinor?: number; paymentMethodToken?: string }): Promise<PaymentResponse> {
    return this.post(`/orders/${id}/payments`, body, { key });
  }
  deposit(appointmentId: string, key: string, body: { tenderCode: Tender; amountMinor?: number; paymentMethodToken?: string }): Promise<PaymentResponse> {
    return this.post(`/appointments/${appointmentId}/deposit`, body, { key });
  }
  intent(id: string): Promise<IntentDto> { return this.get(`/payment-intents/${id}`); }
  /** Asks the provider what happened to the original. Never charges. */
  resolve(id: string): Promise<PaymentResponse> { return this.post(`/payment-intents/${id}/resolve`, {}); }

  requestRefund(transactionId: string, amountMinor: number, reasonCode: string): Promise<RefundDto> {
    return this.post(`/payment-transactions/${transactionId}/refunds`, { amountMinor, reasonCode });
  }
  approveRefund(intentId: string, rowVersion: number): Promise<RefundDto> { return this.post(`/payment-intents/${intentId}/approve`, {}, { rowVersion }); }
  rejectRefund(intentId: string, rowVersion: number, reason: string): Promise<RefundDto> {
    return this.post(`/payment-intents/${intentId}/reject`, { reason }, { rowVersion });
  }

  reconciliation(date: string): Promise<ReconciliationDto> { return this.get('/reconciliation', { date }); }
  resolveAllAmbiguous(): Promise<{ settled: number }> { return this.post('/reconciliation/resolve-ambiguous', {}); }

  private get<T>(path: string, params: Record<string, string | undefined> = {}): Promise<T> {
    const clean = Object.fromEntries(Object.entries(params).filter(([, v]) => v !== undefined && v !== '')) as Record<string, string>;
    return firstValueFrom(this.http.get<T>(`${this.base}${path}`, { params: clean }));
  }

  private post<T>(path: string, body: unknown, opts: { rowVersion?: number; key?: string } = {}): Promise<T> {
    const headers: Record<string, string> = {};
    if (opts.rowVersion !== undefined) headers['If-Match'] = `"${opts.rowVersion}"`;
    if (opts.key) headers['Idempotency-Key'] = opts.key;
    return firstValueFrom(this.http.post<T>(`${this.base}${path}`, body, { headers }));
  }
}
