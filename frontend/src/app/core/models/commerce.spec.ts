import { describe, expect, it } from 'vitest';
import { isAmbiguous, money } from './commerce';
import type { PaymentResponse } from './commerce';

describe('commerce wire helpers', () => {
  it('formats minor units as currency', () => {
    expect(money(14698)).toBe('$146.98');
    expect(money(0)).toBe('$0.00');
    expect(money(-500)).toBe('-$5.00');
  });

  it('tells an unconfirmed 202 apart from a settled payment', () => {
    const pending = { code: 'PAYMENT_OUTCOME_AMBIGUOUS', payment_intent_id: 'pi-1' } as PaymentResponse;
    const settled = { intent: { paymentIntentId: 'pi-2' }, transaction: null, outcome: 'Declined', message: null } as unknown as PaymentResponse;
    expect(isAmbiguous(pending)).toBe(true);
    expect(isAmbiguous(settled)).toBe(false);
  });
});
