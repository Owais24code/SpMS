/**
 * WCAG 2.2 contrast audit for the SpMS token layer.
 *
 * Run: node tools/contrast-audit.mjs
 * Exits non-zero if any pair falls below its required ratio, so this can be
 * wired straight into CI as a design-system regression guard.
 *
 * Thresholds (WCAG 2.2):
 *   4.5:1  normal text            (1.4.3)
 *   3.0:1  large text >=24px/19px bold  (1.4.3)
 *   3.0:1  UI components & graphics     (1.4.11)
 */

const hex = (h) => {
  const s = h.replace('#', '');
  const f = s.length === 3 ? s.split('').map((c) => c + c).join('') : s;
  return [0, 2, 4].map((i) => parseInt(f.slice(i, i + 2), 16));
};

const luminance = (h) => {
  const [r, g, b] = hex(h).map((v) => {
    const c = v / 255;
    return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};

const ratio = (a, b) => {
  const [l1, l2] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (l1 + 0.05) / (l2 + 0.05);
};

/* ---------------------------------------------------------------- */

const LIGHT = {
  canvas: '#ffffff',
  subtle: '#f7f8f9',
  muted: '#eef0f2',
  brand: '#003057',
  accent: '#0066b3',
  fgDefault: '#1d1c1e',
  fgMuted: '#565d65',
  fgSubtle: '#6f7780',
  fgAccent: '#005596',
  borderDefault: '#7e8791',
  borderAccent: '#0066b3',
  focusRing: '#005596',
  successFg: '#0a6237', successBg: '#e7f5ed', successBr: '#0e7a43',
  warningFg: '#7d4e00', warningBg: '#fdf3e3', warningBr: '#9a6100',
  dangerFg:  '#8f1d17', dangerBg:  '#fdeceb', dangerBr:  '#b3261e',
  infoFg:    '#005596', infoBg:    '#e8f3fc', infoBr:    '#0066b3',
  white: '#ffffff',
};

const DARK = {
  canvas: '#000d18',
  subtle: '#00182c',
  muted: '#002340',
  brand: '#002340',
  accent: '#2b88d2',
  fgDefault: '#e9eef4',
  fgMuted: '#a8b6c6',
  fgSubtle: '#8494a6',
  fgAccent: '#5fa9e5',
  borderDefault: '#4f7193',
  borderAccent: '#2b88d2',
  focusRing: '#5fa9e5',
  successFg: '#6fd39b', successBg: '#0b2b1c', successBr: '#2f9e68',
  warningFg: '#efba63', warningBg: '#33230a', warningBr: '#b98524',
  dangerFg:  '#f59b94', dangerBg:  '#3a1512', dangerBr:  '#cf5b52',
  infoFg:    '#7fc0f0', infoBg:    '#0a2942', infoBr:    '#2b88d2',
  navy950:   '#000d18',
};

/** [label, fg, bg, minimum] */
const pairs = (t, dark) => [
  ['body text on canvas',        t.fgDefault, t.canvas, 4.5],
  ['muted text on canvas',       t.fgMuted,   t.canvas, 4.5],
  ['subtle text on canvas',      t.fgSubtle,  t.canvas, 4.5],
  ['muted text on subtle bg',    t.fgMuted,   t.subtle, 4.5],
  ['body text on muted bg',      t.fgDefault, t.muted,  4.5],
  ['accent text on canvas',      t.fgAccent,  t.canvas, 4.5],
  ['text on brand surface',      dark ? t.fgDefault : t.white, t.brand,  4.5],
  ['text on accent surface',     dark ? t.navy950  : t.white, t.accent, 4.5],

  ['default border on canvas',   t.borderDefault, t.canvas, 3.0],
  ['default border on subtle bg', t.borderDefault, t.subtle, 3.0],
  ['default border on muted bg',  t.borderDefault, t.muted,  3.0],
  ['accent border on canvas',    t.borderAccent,  t.canvas, 3.0],
  ['focus ring on canvas',       t.focusRing,     t.canvas, 3.0],
  ['focus ring on subtle bg',    t.focusRing,     t.subtle, 3.0],

  ['success text on success bg', t.successFg, t.successBg, 4.5],
  ['success border on canvas',   t.successBr, t.canvas,    3.0],
  ['warning text on warning bg', t.warningFg, t.warningBg, 4.5],
  ['warning border on canvas',   t.warningBr, t.canvas,    3.0],
  ['danger text on danger bg',   t.dangerFg,  t.dangerBg,  4.5],
  ['danger border on canvas',    t.dangerBr,  t.canvas,    3.0],
  ['info text on info bg',       t.infoFg,    t.infoBg,    4.5],
  ['info border on canvas',      t.infoBr,    t.canvas,    3.0],
];

let failures = 0;

for (const [themeName, tokens, isDark] of [['LIGHT', LIGHT, false], ['DARK', DARK, true]]) {
  console.log(`\n  ${themeName} THEME`);
  console.log('  ' + '-'.repeat(62));

  for (const [label, fg, bg, min] of pairs(tokens, isDark)) {
    const r = ratio(fg, bg);
    const pass = r >= min;
    if (!pass) failures++;
    console.log(
      `  ${pass ? 'PASS' : 'FAIL'}  ${label.padEnd(30)} ` +
      `${r.toFixed(2).padStart(6)}:1  (needs ${min.toFixed(1)}:1)`
    );
  }
}

/* --- Brand-guide gray: documented exclusion --------------------- */
const coolGray = ratio('#bcbec0', '#ffffff');
console.log(`\n  BRAND NOTE`);
console.log('  ' + '-'.repeat(62));
console.log(
  `  PANTONE Cool Gray 4C (#bcbec0) on white = ${coolGray.toFixed(2)}:1 — ` +
  `below the 3.0:1 required for borders, focus rings and icon shapes.\n` +
  `  Retained as --surface-gray-brand for large decorative fills only;\n` +
  `  --border-default uses #7e8791 (${ratio('#7e8791', '#ffffff').toFixed(2)}:1) instead.`
);

console.log(
  failures === 0
    ? `\n  All pairs meet WCAG 2.2 AA.\n`
    : `\n  ${failures} pair(s) below threshold.\n`
);

process.exit(failures === 0 ? 0 : 1);
