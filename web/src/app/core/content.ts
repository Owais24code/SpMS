import type { FaqItem } from '../shared/faq-accordion/faq-accordion';

export interface Capability {
  readonly id: string;
  readonly title: string;
  readonly copy: string;
  readonly icon: string;
}

/** Drawn from the SpMS UX field specification (UX-001 … UX-012). */
export const CAPABILITIES: readonly Capability[] = [
  {
    id: 'UX-001',
    title: 'Scheduling board',
    copy: 'Provider and room lanes with preflight conflict detection. Hard resource clashes are blocked outright; soft clashes require a reason and are audited.',
    icon: 'M4 5h16v15H4zM4 9h16M9 3v4M15 3v4M8 13h3v3H8z',
  },
  {
    id: 'UX-002',
    title: 'Guest booking',
    copy: 'DST-safe availability across bookable properties, with tokenized deposits and an explicit ambiguous-payment path rather than a silent retry.',
    icon: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18ZM12 7v5l3.2 2',
  },
  {
    id: 'UX-004',
    title: 'Arrival and check-in',
    copy: 'Readiness checklist showing completion status only — never the underlying health answers — with locker and quiet-device assignment.',
    icon: 'M5 12.5 10 17l9-10M4 20h16',
  },
  {
    id: 'UX-005',
    title: 'Provider tablet',
    copy: 'Offline-capable delivery with an idempotent service timer, encrypted autosave, and treatment records that lock on completion and amend thereafter.',
    icon: 'M6 3h12v18H6zM10 18.5h4',
  },
  {
    id: 'UX-007',
    title: 'Inventory and readiness',
    copy: 'Ledger-backed linen and room states with forecast demand that shows its assumptions and confidence rather than a bare number.',
    icon: 'M4 8l8-4 8 4-8 4zM4 8v8l8 4 8-4V8',
  },
  {
    id: 'UX-009',
    title: 'Quiet notification',
    copy: 'Anonymous-token pagers that never display a guest name, with accessible alert patterns and discreet escalation on timeout.',
    icon: 'M18 15V10a6 6 0 1 0-12 0v5l-2 3h16zM10 21h4',
  },
];

export const FAQ_ITEMS: readonly FaqItem[] = [
  {
    q: 'Does SpMS replace our property management system?',
    a: 'No. SpMS owns spa scheduling, delivery and readiness, and integrates with whichever system already owns rooms, folios and payment. Every integration point has exactly one declared capability owner per property, so there is never ambiguity about which system is authoritative for a given record at a given moment.',
  },
  {
    q: 'How is guest health information handled?',
    a: 'Intake answers are never serialized to surfaces that do not need them. The scheduling board carries no health detail at all; the front desk sees completion status only; the provider sees a minimum-necessary restriction summary they must acknowledge before starting. Analytics records stable codes, never field values.',
  },
  {
    q: 'What happens when the network drops mid-treatment?',
    a: 'The provider tablet keeps working from a cached minimum. Status changes queue locally and are idempotent, so a reconnect cannot double-post a start or create a duplicate rebooking. Treatment notes autosave encrypted and reconcile on sync.',
  },
  {
    q: 'Can a scheduler override a booking conflict?',
    a: 'It depends on the conflict class. A soft conflict can be overridden by an authorized role with a recorded reason. A hard physical conflict — the same room or resource double-booked — cannot be overridden by anyone, because the constraint is physical rather than procedural.',
  },
  {
    q: 'What accessibility standard does SpMS meet?',
    a: 'WCAG 2.2 AA is the target across desktop, tablet, phone and kiosk. Every drag interaction on the scheduling board has a keyboard equivalent, and critical journeys carry manual screen-reader evidence against browser and screen-reader combinations approved for each release.',
  },
  {
    q: 'How are configuration changes to a live property controlled?',
    a: 'Ownership changes are proposed, preflight-validated, approved by a second person — the proposer cannot self-approve — and take effect at a declared future timestamp. A controlled rollback path freezes and reconciles rather than simply reverting.',
  },
];
