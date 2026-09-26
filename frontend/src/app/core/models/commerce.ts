/**
 * Wire shapes for orders, payments, deposits, refunds and reconciliation.
 * Money is integer minor units (cents) everywhere; format only at the edge.
 */

export type OrderStatus = 'Draft' | 'Open' | 'PartiallyPaid' | 'Paid' | 'PartiallyRefunded' | 'Refunded' | 'Voided' | 'Delegated' | 'Expired';
export type LineKind = 'Service' | 'Option' | 'Retail' | 'Fee' | 'Discount' | 'Tip' | 'Deposit';
export type Tender = 'Card' | 'Cash';
export type GatewayOutcome = 'Approved' | 'Declined' | 'Error' | 'Pending' | 'Unknown';

export interface OrderLineDto {
  readonly orderLineId: string;
  readonly lineNumber: number;
  readonly lineKind: LineKind;
  readonly appointmentId: string | null;
  readonly serviceId: string | null;
  readonly description: string;
  readonly quantity: number;
  readonly unitPriceMinor: number;
  readonly discountMinor: number;
  readonly taxMinor: number;
  readonly netMinor: number;
}

export interface TransactionDto {
  readonly paymentTransactionId: string;
  readonly paymentIntentId: string | null;
  readonly tenderCode: string;
  readonly transactionType: 'Sale' | 'Capture' | 'Refund' | 'Void' | 'Adjustment' | string;
  readonly outcome: GatewayOutcome | string;
  readonly amountMinor: number;
  readonly currencyCode: string;
  readonly cardBrand: string | null;
  readonly cardLast4: string | null;
  readonly processedUtc: string;
  readonly originalTransactionId: string | null;
}

export interface OrderDto {
  readonly orderId: string;
  readonly orderNumber: string | null;
  readonly receiptNumber: string | null;
  readonly guestId: string | null;
  readonly visitId: string | null;
  readonly ownerSystem: string;
  readonly status: OrderStatus;
  readonly currencyCode: string;
  readonly subtotalMinor: number;
  readonly discountTotalMinor: number;
  readonly taxTotalMinor: number;
  readonly tipTotalMinor: number;
  readonly totalMinor: number;
  readonly paidMinor: number;
  readonly refundedMinor: number;
  readonly balanceMinor: number;
  readonly lines: readonly OrderLineDto[];
  readonly transactions: readonly TransactionDto[];
  readonly rowVersion: number;
  readonly eTag: string;
}

export interface OrderSummaryDto {
  readonly orderId: string;
  readonly orderNumber: string | null;
  readonly receiptNumber: string | null;
  readonly status: OrderStatus;
  readonly ownerSystem: string;
  readonly totalMinor: number;
  readonly currencyCode: string;
  readonly guestId: string | null;
  readonly createdUtc: string;
}

export interface IntentDto {
  readonly paymentIntentId: string;
  readonly purpose: 'Payment' | 'Deposit' | 'Refund' | 'DepositApplied' | 'DepositForfeited' | string;
  readonly status: string;
  readonly amountMinor: number;
  readonly currencyCode: string;
  readonly orderId: string | null;
  readonly appointmentId: string | null;
  readonly originalTransactionId: string | null;
  readonly reasonCode: string | null;
  readonly approvedBy: string | null;
  readonly createdUtc: string;
  readonly requestedBy: string | null;
  readonly rowVersion: number;
  readonly eTag: string;
}

export interface PaymentDto {
  readonly intent: IntentDto;
  readonly transaction: TransactionDto | null;
  readonly outcome: GatewayOutcome;
  readonly message: string | null;
}

/** A 202 answer: the provider has not confirmed. Query the intent; never take the payment again. */
export interface AmbiguousPayment {
  readonly code: 'PAYMENT_OUTCOME_AMBIGUOUS';
  readonly payment_intent_id: string;
  readonly detail?: string;
}

export type PaymentResponse = PaymentDto | AmbiguousPayment;

export const isAmbiguous = (r: PaymentResponse): r is AmbiguousPayment => (r as AmbiguousPayment).code === 'PAYMENT_OUTCOME_AMBIGUOUS';

export interface RefundDto {
  readonly intent: IntentDto;
  readonly transaction: TransactionDto | null;
}

export interface TenderTotalDto {
  readonly tenderCode: string;
  readonly transactionType: string;
  readonly count: number;
  readonly amountMinor: number;
}

export interface ReconciliationDto {
  readonly date: string;
  readonly timeZone: string;
  readonly netMinor: number;
  readonly totals: readonly TenderTotalDto[];
  readonly ambiguous: readonly IntentDto[];
  readonly refundsAwaitingApproval: readonly IntentDto[];
  readonly transactions: readonly TransactionDto[];
}

/** Formats minor units for display: 14698 USD → "$146.98". */
export function money(minor: number, currency = 'USD'): string {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(minor / 100);
}
