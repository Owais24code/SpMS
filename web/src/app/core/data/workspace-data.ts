import type {
  Appointment, ArrivalRow, Conflict, DeviceRow, Lane,
  LedgerRow, MessageRule, OwnerRow, StaffRow, StockLine,
} from '../models/spa.model';

/** Placeholder data so every screen renders with realistic shape and density.
 *  Replace with API calls — the view models are already the API contract. */

export const LANES: readonly Lane[] = [
  { name: 'Lena Kovač', role: 'Therapist', slots: [
    { label: 'Aromatherapy 60',  startPct: 6,  widthPct: 20, state: 'complete' },
    { label: 'Deep tissue 90',   startPct: 30, widthPct: 28, state: 'in-progress' },
    { label: 'Hot stone 60',     startPct: 64, widthPct: 20, state: 'booked' },
  ]},
  { name: 'Marco Ruiz', role: 'Therapist', slots: [
    { label: 'Swedish 60',       startPct: 10, widthPct: 20, state: 'complete' },
    { label: 'Double booked',    startPct: 38, widthPct: 24, state: 'conflict' },
    { label: 'Facial 45',        startPct: 70, widthPct: 16, state: 'booked' },
  ]},
  { name: 'Priya Nair', role: 'Esthetician', slots: [
    { label: 'Facial 45',        startPct: 4,  widthPct: 15, state: 'complete' },
    { label: 'Peel 30',          startPct: 24, widthPct: 11, state: 'complete' },
    { label: 'Facial 60',        startPct: 44, widthPct: 20, state: 'booked' },
  ]},
  { name: 'Suite 1', role: 'Room', slots: [
    { label: 'Turnover',         startPct: 8,  widthPct: 10, state: 'turnover' },
    { label: 'Couples 90',       startPct: 22, widthPct: 30, state: 'booked' },
    { label: 'Turnover',         startPct: 54, widthPct: 10, state: 'turnover' },
  ]},
  { name: 'Suite 2', role: 'Room', slots: [
    { label: 'Maintenance',      startPct: 0,  widthPct: 34, state: 'blocked' },
    { label: 'Aromatherapy 60',  startPct: 40, widthPct: 22, state: 'booked' },
  ]},
];

export const HOURS = ['9a', '10a', '11a', '12p', '1p', '2p', '3p', '4p', '5p'];

export const ACTIVE_CONFLICT: Conflict = {
  code: 'SCHED-PROVIDER-BUSY',
  severity: 'soft',
  summary: 'Marco Ruiz is already booked between 1:00pm and 2:30pm.',
  consequence: 'Committing will leave two appointments assigned to one therapist. The later guest will wait.',
  alternatives: [
    'Move to Priya Nair, free from 1:00pm',
    'Shift the new booking to 2:45pm',
    'Split across Suite 2 with a second therapist',
  ],
};

export const HARD_CONFLICT: Conflict = {
  code: 'SCHED-ROOM-OCCUPIED',
  severity: 'hard',
  summary: 'Suite 1 is occupied for the whole requested window.',
  consequence: 'A room cannot hold two treatments. This cannot be overridden by any role.',
  alternatives: ['Use Suite 3, free from 1:30pm', 'Move the booking to 3:15pm'],
};

export const APPOINTMENTS: readonly Appointment[] = [
  { id: 'a-4821', guestAlias: 'Guest 4821', service: 'Deep tissue 90',  provider: 'Lena Kovač', room: 'Suite 3', start: '1:00pm', durationMin: 90, state: 'in-progress', version: 'v7' },
  { id: 'a-4822', guestAlias: 'Guest 4822', service: 'Facial 45',       provider: 'Priya Nair', room: 'Room 2',  start: '1:30pm', durationMin: 45, state: 'booked',      version: 'v2' },
  { id: 'a-4823', guestAlias: 'Guest 4823', service: 'Couples 90',      provider: 'Unassigned', room: 'Suite 1', start: '2:00pm', durationMin: 90, state: 'booked',      version: 'v1' },
  { id: 'a-4824', guestAlias: 'Guest 4824', service: 'Hot stone 60',    provider: 'Marco Ruiz', room: 'Room 4',  start: '2:15pm', durationMin: 60, state: 'conflict',    version: 'v4' },
  { id: 'a-4825', guestAlias: 'Guest 4825', service: 'Aromatherapy 60', provider: 'Lena Kovač', room: 'Suite 2', start: '3:00pm', durationMin: 60, state: 'booked',      version: 'v1' },
  { id: 'a-4826', guestAlias: 'Guest 4826', service: 'Swedish 60',      provider: 'Marco Ruiz', room: 'Room 2',  start: '4:00pm', durationMin: 60, state: 'booked',      version: 'v3' },
];

export const ARRIVALS: readonly ArrivalRow[] = [
  { id: 'r-1', guestAlias: 'Guest 4821', time: '12:45pm', service: 'Deep tissue 90',  formsComplete: true,  depositSettled: true,  roomReady: true,  locker: 'L-204', pager: 'P-09' },
  { id: 'r-2', guestAlias: 'Guest 4822', time: '1:15pm',  service: 'Facial 45',       formsComplete: true,  depositSettled: true,  roomReady: true,  locker: 'L-118', pager: null },
  { id: 'r-3', guestAlias: 'Guest 4823', time: '1:45pm',  service: 'Couples 90',      formsComplete: false, depositSettled: true,  roomReady: false, locker: null,    pager: null },
  { id: 'r-4', guestAlias: 'Guest 4824', time: '2:00pm',  service: 'Hot stone 60',    formsComplete: true,  depositSettled: false, roomReady: true,  locker: null,    pager: null },
  { id: 'r-5', guestAlias: 'Guest 4825', time: '2:45pm',  service: 'Aromatherapy 60', formsComplete: true,  depositSettled: true,  roomReady: true,  locker: 'L-301', pager: 'P-14' },
];

export const STOCK: readonly StockLine[] = [
  { item: 'Robe — Large',   onHand: 120, clean: 64, soiled: 31, inWash: 25, reserved: 48, forecast: 72, confidence: 'high' },
  { item: 'Robe — Medium',  onHand: 140, clean: 88, soiled: 22, inWash: 30, reserved: 61, forecast: 84, confidence: 'high' },
  { item: 'Robe — Small',   onHand: 60,  clean: 18, soiled: 26, inWash: 16, reserved: 34, forecast: 41, confidence: 'medium' },
  { item: 'Slippers — 40',  onHand: 200, clean: 150, soiled: 30, inWash: 20, reserved: 96, forecast: 110, confidence: 'high' },
  { item: 'Towel — Bath',   onHand: 420, clean: 210, soiled: 120, inWash: 90, reserved: 260, forecast: 318, confidence: 'medium' },
  { item: 'Towel — Hand',   onHand: 380, clean: 96,  soiled: 180, inWash: 104, reserved: 220, forecast: 295, confidence: 'low' },
];

export const DEVICES: readonly DeviceRow[] = [
  { id: 'P-09', kind: 'Pager',  battery: 82, online: true,  assignedToken: 'tkn-9f2e', state: 'assigned' },
  { id: 'P-14', kind: 'Pager',  battery: 41, online: true,  assignedToken: 'tkn-1a77', state: 'assigned' },
  { id: 'P-15', kind: 'Pager',  battery: 96, online: true,  assignedToken: null,       state: 'available' },
  { id: 'P-16', kind: 'Pager',  battery: 12, online: false, assignedToken: null,       state: 'out-of-service' },
  { id: 'L-204', kind: 'Locker', battery: 74, online: true, assignedToken: 'tkn-9f2e', state: 'assigned' },
  { id: 'L-118', kind: 'Locker', battery: 88, online: true, assignedToken: 'tkn-33b1', state: 'assigned' },
  { id: 'L-301', kind: 'Locker', battery: 65, online: true, assignedToken: null,       state: 'cleaning' },
  { id: 'RD-02', kind: 'Reader', battery: 100, online: true, assignedToken: null,      state: 'available' },
];

export const STAFF: readonly StaffRow[] = [
  { id: 's-1', name: 'Lena Kovač',  role: 'Therapist',   credential: 'Massage licence', expires: '14 Mar 2027', assignable: true,  blockHint: null },
  { id: 's-2', name: 'Marco Ruiz',  role: 'Therapist',   credential: 'Massage licence', expires: '02 Oct 2026', assignable: true,  blockHint: null },
  { id: 's-3', name: 'Priya Nair',  role: 'Esthetician', credential: 'Esthetics cert',  expires: '30 Sep 2026', assignable: true,  blockHint: null },
  { id: 's-4', name: 'Tomas Brandt', role: 'Therapist',   credential: 'Massage licence', expires: '11 Sep 2026', assignable: false, blockHint: 'A requirement for this role is not currently met' },
  { id: 's-5', name: 'Ada Osei',    role: 'Attendant',   credential: 'Safety training', expires: '21 Jan 2027', assignable: true,  blockHint: null },
];

export const LEDGER: readonly LedgerRow[] = [
  { id: 'txn-8812', connector: 'Payments', reference: 'idem-4a91c2', amount: 240.00, state: 'matched',   captured: '09:12' },
  { id: 'txn-8813', connector: 'Payments', reference: 'idem-4a91c3', amount: 185.50, state: 'matched',   captured: '09:48' },
  { id: 'txn-8814', connector: 'Payments', reference: 'idem-4a91c4', amount: 320.00, state: 'ambiguous', captured: '10:31' },
  { id: 'txn-8815', connector: 'Folio',    reference: 'fol-77210',   amount: 96.00,  state: 'unmatched', captured: '11:02' },
  { id: 'txn-8816', connector: 'Payments', reference: 'idem-4a91c6', amount: 410.00, state: 'matched',   captured: '11:44' },
  { id: 'txn-8817', connector: 'Folio',    reference: 'fol-77214',   amount: 52.00,  state: 'resolved',  captured: '12:19' },
];

export const MESSAGE_RULES: readonly MessageRule[] = [
  { id: 'm-1', name: 'Booking confirmation', trigger: 'Appointment created',  offset: 'Immediately', channel: 'Email', active: true,  lastSent: '4 min ago' },
  { id: 'm-2', name: 'Intake reminder',      trigger: 'Before appointment',   offset: '48 hours',    channel: 'Email', active: true,  lastSent: '2 hours ago' },
  { id: 'm-3', name: 'Arrival guidance',     trigger: 'Before appointment',   offset: '3 hours',     channel: 'SMS',   active: true,  lastSent: '18 min ago' },
  { id: 'm-4', name: 'Room ready',           trigger: 'Room reaches ready',   offset: 'Immediately', channel: 'Push',  active: false, lastSent: '—' },
  { id: 'm-5', name: 'Post-visit thanks',    trigger: 'After completion',     offset: '4 hours',     channel: 'Email', active: true,  lastSent: '1 hour ago' },
];

export const OWNERS: readonly OwnerRow[] = [
  { capability: 'Guest identity',    owner: 'Loyalty platform',   effective: '01 Jun 2026', state: 'active',            dependencies: 6 },
  { capability: 'Payment capture',   owner: 'Payments gateway',   effective: '01 Jun 2026', state: 'active',            dependencies: 4 },
  { capability: 'Room inventory',    owner: 'Property system',    effective: '01 Jun 2026', state: 'active',            dependencies: 9 },
  { capability: 'Credential issue',  owner: 'AARFID SeQure',      effective: '01 Oct 2026', state: 'awaiting-approval', dependencies: 3 },
  { capability: 'Linen ledger',      owner: 'SpMS',               effective: '15 Oct 2026', state: 'pending',           dependencies: 2 },
];

export const KPIS = [
  { label: 'Booked today',   value: '86',     delta: '+12%',   trend: 'up'   as const, tone: 'positive' as const, hint: 'vs. same day last week' },
  { label: 'Utilisation',    value: '78%',    delta: '+4pt',   trend: 'up'   as const, tone: 'positive' as const, hint: 'therapist hours filled' },
  { label: 'No-show rate',   value: '3.2%',   delta: '-0.8pt', trend: 'down' as const, tone: 'positive' as const, hint: 'lower is better' },
  { label: 'Open conflicts', value: '3',      delta: '+2',     trend: 'up'   as const, tone: 'negative' as const, hint: 'one is non-overridable' },
];

export const WEEK_BOOKINGS = [52, 61, 58, 74, 86, 95, 71];
export const WEEK_LABELS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
