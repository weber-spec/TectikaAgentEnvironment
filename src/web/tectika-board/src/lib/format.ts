// Formatting helpers — dates, relative time, numbers, currency, initials.

export function relativeTime(input: string | Date | undefined | null): string {
  if (!input) return '';
  const d = typeof input === 'string' ? new Date(input) : input;
  const diff = Date.now() - d.getTime();
  const abs = Math.abs(diff);
  const future = diff < 0;
  const mins = Math.round(abs / 60000);
  const hours = Math.round(abs / 3600000);
  const days = Math.round(abs / 86400000);
  const fmt = (n: number, unit: string) =>
    future ? `in ${n} ${unit}${n !== 1 ? 's' : ''}` : `${n} ${unit}${n !== 1 ? 's' : ''} ago`;
  if (abs < 45000) return future ? 'in a moment' : 'just now';
  if (mins < 60) return fmt(mins, 'min');
  if (hours < 24) return fmt(hours, 'hour');
  if (days < 30) return fmt(days, 'day');
  const months = Math.round(days / 30);
  if (months < 12) return fmt(months, 'month');
  return fmt(Math.round(days / 365), 'year');
}

export function formatDate(input: string | Date | undefined | null, opts?: Intl.DateTimeFormatOptions): string {
  if (!input) return '';
  const d = typeof input === 'string' ? new Date(input) : input;
  if (isNaN(d.getTime())) return '';
  return d.toLocaleDateString(undefined, opts ?? { month: 'short', day: 'numeric', year: 'numeric' });
}

export function formatDateShort(input: string | Date | undefined | null): string {
  if (!input) return '';
  const d = typeof input === 'string' ? new Date(input) : input;
  if (isNaN(d.getTime())) return '';
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

export function formatDateTime(input: string | Date | undefined | null): string {
  if (!input) return '';
  const d = typeof input === 'string' ? new Date(input) : input;
  if (isNaN(d.getTime())) return '';
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

/** Days until a due date (negative = overdue). null when no date. */
export function daysUntil(input: string | Date | undefined | null): number | null {
  if (!input) return null;
  const d = typeof input === 'string' ? new Date(input) : input;
  if (isNaN(d.getTime())) return null;
  const start = new Date(); start.setHours(0, 0, 0, 0);
  const target = new Date(d); target.setHours(0, 0, 0, 0);
  return Math.round((target.getTime() - start.getTime()) / 86400000);
}

export function formatNumber(n: number | undefined | null, opts?: Intl.NumberFormatOptions): string {
  if (n == null || isNaN(n)) return '';
  return n.toLocaleString(undefined, opts);
}

export function formatCompact(n: number | undefined | null): string {
  if (n == null || isNaN(n)) return '0';
  return n.toLocaleString(undefined, { notation: 'compact', maximumFractionDigits: 1 });
}

export function formatCurrency(n: number | undefined | null): string {
  if (n == null || isNaN(n)) return '';
  return n.toLocaleString(undefined, { style: 'currency', currency: 'USD', minimumFractionDigits: 2 });
}

export function formatDuration(ms: number | undefined | null): string {
  if (!ms) return '';
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  const rem = s % 60;
  if (m < 60) return rem ? `${m}m ${rem}s` : `${m}m`;
  const h = Math.floor(m / 60);
  return `${h}h ${m % 60}m`;
}

export function initials(name: string): string {
  const parts = name.replace(/@.*/, '').split(/[\s._-]+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

/** A human display name from an email/id. */
export function displayName(idOrEmail: string): string {
  if (!idOrEmail) return 'Unassigned';
  const local = idOrEmail.includes('@') ? idOrEmail.split('@')[0] : idOrEmail;
  return local
    .split(/[._-]+/)
    .filter(Boolean)
    .map(w => w[0].toUpperCase() + w.slice(1))
    .join(' ');
}

/** Simple fuzzy match: does `query` appear as a subsequence of `text`? */
export function fuzzyMatch(text: string, query: string): boolean {
  if (!query) return true;
  const t = text.toLowerCase();
  const q = query.toLowerCase();
  let ti = 0;
  for (let qi = 0; qi < q.length; qi++) {
    const ch = q[qi];
    ti = t.indexOf(ch, ti);
    if (ti === -1) return false;
    ti++;
  }
  return true;
}

/** Fuzzy score for ranking (higher is better). 0 = no match. */
export function fuzzyScore(text: string, query: string): number {
  if (!query) return 1;
  const t = text.toLowerCase();
  const q = query.toLowerCase();
  if (t === q) return 1000;
  if (t.startsWith(q)) return 500 - t.length;
  const idx = t.indexOf(q);
  if (idx >= 0) return 200 - idx - t.length * 0.1;
  return fuzzyMatch(text, query) ? 50 - t.length * 0.1 : 0;
}
