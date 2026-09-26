export interface NavItem {
  readonly label: string;
  readonly path: string;
  /** Inline SVG path data, drawn at 24×24. */
  readonly icon: string;
  /** Optional count rendered as a pill on the rail. */
  readonly badge?: number;
}

export interface NavGroup {
  readonly label: string;
  readonly items: readonly NavItem[];
}

/** Public marketing navigation. */
export const SITE_NAV: readonly NavItem[] = [
  { label: 'Overview', path: '/',         icon: 'M3 11.5 12 4l9 7.5M5.5 10v9.5h13V10' },
  { label: 'Platform', path: '/platform', icon: 'M4 6h16M4 12h16M4 18h10' },
  { label: 'Modules',  path: '/modules',  icon: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z' },
  { label: 'FAQ',      path: '/faq',      icon: 'M12 3a9 9 0 1 1 0 18 9 9 0 0 1 0-18M9.7 9.4a2.4 2.4 0 1 1 3.3 2.2c-.8.4-1 1-1 1.7M12 16.9h.01' },
  { label: 'Contact',  path: '/contact',  icon: 'M4 6h16v12H4zM4 7l8 6 8-6' },
];

/** Workspace navigation, grouped by what the person is doing. */
export const WORKSPACE_NAV: readonly NavGroup[] = [
  {
    label: 'Today',
    items: [
      { label: 'Dashboard', path: '/app',           icon: 'M4 13h7V4H4zM13 8h7V4h-7zM13 20h7v-9h-7zM4 20h7v-5H4z' },
      { label: 'Schedule',  path: '/app/schedule',  icon: 'M4 5h16v15H4zM4 9h16M9 3v4M15 3v4', badge: 3 },
      { label: 'Check-in',  path: '/app/check-in',  icon: 'M5 12.5 10 17l9-10M4 20h16', badge: 7 },
      { label: 'Treatments',path: '/app/treatments',icon: 'M6 3h12v18H6zM10 18.5h4' },
      { label: 'Checkout',  path: '/app/checkout',  icon: 'M3 6h18v12H3zM3 10h18M7 15h3' },
    ],
  },
  {
    label: 'Guests',
    items: [
      { label: 'Guests',       path: '/app/guests',       icon: 'M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8M4 21v-1a6 6 0 0 1 6-6h4a6 6 0 0 1 6 6v1' },
      { label: 'Booking',      path: '/app/booking',      icon: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18M12 7v5l3.2 2' },
      { label: 'Appointments', path: '/app/appointments', icon: 'M7 4h10a2 2 0 0 1 2 2v14l-7-3.5L5 20V6a2 2 0 0 1 2-2z' },
      { label: 'Waitlist',     path: '/app/waitlist',     icon: 'M8 6h12M8 12h12M8 18h8M4 6h.01M4 12h.01M4 18h.01' },
      { label: 'Messaging',    path: '/app/messaging',    icon: 'M21 11.5a8.4 8.4 0 0 1-9 8.4L4 21l1.1-3.6A8.4 8.4 0 1 1 21 11.5Z' },
    ],
  },
  {
    label: 'Operations',
    items: [
      { label: 'Room turnover', path: '/app/turnover', icon: 'M4 20V9l8-5 8 5v11M9 20v-6h6v6' },
      { label: 'Inventory', path: '/app/inventory', icon: 'M4 8l8-4 8 4-8 4zM4 8v8l8 4 8-4V8' },
      { label: 'Devices',   path: '/app/devices',   icon: 'M18 15V10a6 6 0 1 0-12 0v5l-2 3h16zM10 21h4' },
      { label: 'Staff',     path: '/app/staff',     icon: 'M16 20v-1.5a4 4 0 0 0-4-4H7a4 4 0 0 0-4 4V20M9.5 10.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7M21 20v-1.5a4 4 0 0 0-3-3.9' },
    ],
  },
  {
    label: 'Finance & setup',
    items: [
      { label: 'Reconciliation', path: '/app/reconciliation', icon: 'M4 7h16M4 12h16M4 17h9M17.5 15l2.5 2.5L17.5 20' },
      { label: 'Reports',        path: '/app/reports',        icon: 'M5 20V10M12 20V4M19 20v-7' },
      { label: 'Integrations',   path: '/app/integrations',   icon: 'M9 3v4M15 3v4M5 7h14v5a7 7 0 0 1-14 0zM12 19v2' },
      { label: 'Settings',       path: '/app/settings',       icon: 'M12 15.2a3.2 3.2 0 1 0 0-6.4 3.2 3.2 0 0 0 0 6.4M19.4 15a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-2.7 1.1V21a2 2 0 0 1-4 0v-.2A1.6 1.6 0 0 0 7.5 19.4l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1A1.6 1.6 0 0 0 3 14.9a2 2 0 0 1 0-4h.2a1.6 1.6 0 0 0 1.1-2.7l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1A1.6 1.6 0 0 0 9.1 3H9a2 2 0 0 1 4 0v.2a1.6 1.6 0 0 0 2.7 1.1l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.6 1.6 0 0 0 1.1 2.7H21a2 2 0 0 1 0 4h-.2a1.6 1.6 0 0 0-1.4 1.2z' },
    ],
  },
];
