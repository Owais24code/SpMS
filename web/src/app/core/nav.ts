export interface NavItem {
  readonly label: string;
  readonly path: string;
  /** Rendered as an inline SVG path in the sidebar. */
  readonly icon: string;
}

/** Single source of truth for the marketing navigation. */
export const NAV_ITEMS: readonly NavItem[] = [
  { label: 'Overview',  path: '/',          icon: 'M3 11.5 12 4l9 7.5M5.5 10v9.5h13V10' },
  { label: 'Platform',  path: '/platform',  icon: 'M4 6h16M4 12h16M4 18h10' },
  { label: 'Modules',   path: '/modules',   icon: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z' },
  { label: 'FAQ',       path: '/faq',       icon: 'M12 3a9 9 0 1 1 0 18 9 9 0 0 1 0-18M9.7 9.4a2.4 2.4 0 1 1 3.3 2.2c-.8.4-1 1-1 1.7M12 16.9h.01' },
  { label: 'Contact',   path: '/contact',   icon: 'M4 6h16v12H4zM4 7l8 6 8-6' },
] as const;
