// Compact shared icon set (stroke-based, inherits currentColor).
import React from 'react';

type P = { size?: number; className?: string; strokeWidth?: number };
const S = ({ size = 18, className, strokeWidth = 1.8, children }: P & { children: React.ReactNode }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" className={className}
    stroke="currentColor" strokeWidth={strokeWidth} strokeLinecap="round" strokeLinejoin="round">
    {children}
  </svg>
);

export const Icon = {
  chevronDown: (p: P) => <S {...p}><path d="M6 9l6 6 6-6" /></S>,
  chevronRight: (p: P) => <S {...p}><path d="M9 6l6 6-6 6" /></S>,
  chevronLeft: (p: P) => <S {...p}><path d="M15 6l-6 6 6 6" /></S>,
  plus: (p: P) => <S {...p}><path d="M12 5v14M5 12h14" /></S>,
  x: (p: P) => <S {...p}><path d="M18 6 6 18M6 6l12 12" /></S>,
  check: (p: P) => <S {...p}><path d="M20 6 9 17l-5-5" /></S>,
  search: (p: P) => <S {...p}><circle cx="11" cy="11" r="7" /><path d="m21 21-4.3-4.3" /></S>,
  filter: (p: P) => <S {...p}><path d="M3 5h18M6 12h12M10 19h4" /></S>,
  sort: (p: P) => <S {...p}><path d="M3 6h12M3 12h9M3 18h6M17 6v12m0 0 3-3m-3 3-3-3" /></S>,
  group: (p: P) => <S {...p}><rect x="3" y="4" width="18" height="6" rx="1" /><rect x="3" y="14" width="18" height="6" rx="1" /></S>,
  dots: (p: P) => <S {...p}><circle cx="12" cy="5" r="1.4" fill="currentColor" /><circle cx="12" cy="12" r="1.4" fill="currentColor" /><circle cx="12" cy="19" r="1.4" fill="currentColor" /></S>,
  dotsH: (p: P) => <S {...p}><circle cx="5" cy="12" r="1.4" fill="currentColor" /><circle cx="12" cy="12" r="1.4" fill="currentColor" /><circle cx="19" cy="12" r="1.4" fill="currentColor" /></S>,
  drag: (p: P) => <S {...p} strokeWidth={0}><circle cx="9" cy="6" r="1.5" fill="currentColor" /><circle cx="15" cy="6" r="1.5" fill="currentColor" /><circle cx="9" cy="12" r="1.5" fill="currentColor" /><circle cx="15" cy="12" r="1.5" fill="currentColor" /><circle cx="9" cy="18" r="1.5" fill="currentColor" /><circle cx="15" cy="18" r="1.5" fill="currentColor" /></S>,
  calendar: (p: P) => <S {...p}><rect x="3" y="4" width="18" height="17" rx="2" /><path d="M3 9h18M8 2v4M16 2v4" /></S>,
  clock: (p: P) => <S {...p}><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></S>,
  table: (p: P) => <S {...p}><rect x="3" y="4" width="18" height="16" rx="2" /><path d="M3 10h18M9 4v16" /></S>,
  kanban: (p: P) => <S {...p}><rect x="4" y="4" width="5" height="12" rx="1" /><rect x="15" y="4" width="5" height="8" rx="1" /></S>,
  timeline: (p: P) => <S {...p}><path d="M4 7h9M4 12h14M4 17h6" /></S>,
  cards: (p: P) => <S {...p}><rect x="3" y="3" width="8" height="8" rx="1" /><rect x="13" y="3" width="8" height="8" rx="1" /><rect x="3" y="13" width="8" height="8" rx="1" /><rect x="13" y="13" width="8" height="8" rx="1" /></S>,
  chart: (p: P) => <S {...p}><path d="M3 3v18h18" /><rect x="7" y="11" width="3" height="6" fill="currentColor" stroke="none" /><rect x="12" y="7" width="3" height="10" fill="currentColor" stroke="none" /><rect x="17" y="13" width="3" height="4" fill="currentColor" stroke="none" /></S>,
  flow: (p: P) => <S {...p}><circle cx="5" cy="12" r="2" /><circle cx="19" cy="6" r="2" /><circle cx="19" cy="18" r="2" /><path d="M7 11l10-4M7 13l10 4" /></S>,
  board: (p: P) => <S {...p}><rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><rect x="14" y="14" width="7" height="7" rx="1" /></S>,
  bolt: (p: P) => <S {...p}><path d="M13 2 4 14h7l-1 8 9-12h-7l1-8Z" /></S>,
  robot: (p: P) => <S {...p}><rect x="4" y="8" width="16" height="11" rx="2" /><path d="M9 4v4M15 4v4" /><circle cx="9.5" cy="13" r="1" fill="currentColor" /><circle cx="14.5" cy="13" r="1" fill="currentColor" /></S>,
  bell: (p: P) => <S {...p}><path d="M18 8a6 6 0 1 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0" /></S>,
  user: (p: P) => <S {...p}><circle cx="12" cy="8" r="4" /><path d="M4 21a8 8 0 0 1 16 0" /></S>,
  trash: (p: P) => <S {...p}><path d="M3 6h18M8 6V4h8v2M6 6l1 14h10l1-14" /></S>,
  duplicate: (p: P) => <S {...p}><rect x="9" y="9" width="11" height="11" rx="2" /><path d="M5 15V5a2 2 0 0 1 2-2h10" /></S>,
  edit: (p: P) => <S {...p}><path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z" /></S>,
  eye: (p: P) => <S {...p}><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z" /><circle cx="12" cy="12" r="3" /></S>,
  eyeOff: (p: P) => <S {...p}><path d="M3 3l18 18M10.6 10.6a3 3 0 0 0 4.2 4.2M9.9 5.1A9.6 9.6 0 0 1 12 5c6.5 0 10 7 10 7a17 17 0 0 1-3.4 4.2M6.6 6.6A17 17 0 0 0 2 12s3.5 7 10 7a9.7 9.7 0 0 0 2.1-.2" /></S>,
  pin: (p: P) => <S {...p}><path d="M12 17v5M9 3h6l-1 7 3 3H7l3-3-1-7Z" /></S>,
  link: (p: P) => <S {...p}><path d="M10 13a5 5 0 0 0 7 0l3-3a5 5 0 0 0-7-7l-1 1M14 11a5 5 0 0 0-7 0l-3 3a5 5 0 0 0 7 7l1-1" /></S>,
  arrowUp: (p: P) => <S {...p}><path d="M12 19V5M5 12l7-7 7 7" /></S>,
  arrowDown: (p: P) => <S {...p}><path d="M12 5v14M5 12l7 7 7-7" /></S>,
  star: (p: P) => <S {...p}><path d="M12 3l2.9 5.9 6.5.9-4.7 4.6 1.1 6.5L12 18l-5.8 3.1 1.1-6.5L2.6 9.8l6.5-.9L12 3Z" /></S>,
  command: (p: P) => <S {...p}><path d="M9 6a3 3 0 1 0-3 3h12a3 3 0 1 0-3-3v12a3 3 0 1 0 3-3H6a3 3 0 1 0 3 3V6Z" /></S>,
  settings: (p: P) => <S {...p}><circle cx="12" cy="12" r="3" /><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-2.8 1.2V21a2 2 0 1 1-4 0v-.1A1.7 1.7 0 0 0 7 19.1l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1A1.7 1.7 0 0 0 4.6 15H4a2 2 0 1 1 0-4h.1A1.7 1.7 0 0 0 5.9 7l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1A1.7 1.7 0 0 0 11 4.6V4a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 2.9 1.2l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1A1.7 1.7 0 0 0 19.4 11H21a2 2 0 1 1 0 4h-.1Z" /></S>,
  approvals: (p: P) => <S {...p}><circle cx="12" cy="12" r="9" /><path d="M9 12l2 2 4-4" /></S>,
  warning: (p: P) => <S {...p}><path d="M12 9v4M12 17h.01M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z" /></S>,
  file: (p: P) => <S {...p}><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" /><path d="M14 2v6h6" /></S>,
  send: (p: P) => <S {...p}><path d="M22 2 11 13M22 2l-7 20-4-9-9-4 20-7Z" /></S>,
  refresh: (p: P) => <S {...p}><path d="M21 12a9 9 0 1 1-3-6.7L21 8M21 3v5h-5" /></S>,
  history: (p: P) => <S {...p}><path d="M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5M12 7v5l3 2" /></S>,
};

export type IconName = keyof typeof Icon;
