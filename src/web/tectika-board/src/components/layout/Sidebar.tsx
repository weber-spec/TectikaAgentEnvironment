'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import { useSettings, type TranslationKey } from '@/lib/settings-context';

interface NavItem {
  href: string;
  labelKey: TranslationKey;
  badge?: 'approvals';
  icon: React.ReactNode;
}

const NAV_MAIN: NavItem[] = [
  {
    href: '/boards',
    labelKey: 'boards',
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none">
        <rect x="3" y="3" width="7" height="7" rx="1" stroke="currentColor" strokeWidth="1.8"/>
        <rect x="14" y="3" width="7" height="7" rx="1" stroke="currentColor" strokeWidth="1.8"/>
        <rect x="3" y="14" width="7" height="7" rx="1" stroke="currentColor" strokeWidth="1.8"/>
        <rect x="14" y="14" width="7" height="7" rx="1" stroke="currentColor" strokeWidth="1.8"/>
      </svg>
    ),
  },
  {
    href: '/agents',
    labelKey: 'agents',
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none">
        <rect x="3" y="7" width="18" height="13" rx="2" stroke="currentColor" strokeWidth="1.8"/>
        <path d="M8 7V5a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" stroke="currentColor" strokeWidth="1.8"/>
        <circle cx="9" cy="13" r="1.5" fill="currentColor"/>
        <circle cx="15" cy="13" r="1.5" fill="currentColor"/>
        <path d="M9 17h6" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"/>
      </svg>
    ),
  },
  {
    href: '/approvals',
    labelKey: 'approvals',
    badge: 'approvals',
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none">
        <path d="M9 12l2 2 4-4" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"/>
        <circle cx="12" cy="12" r="9" stroke="currentColor" strokeWidth="1.8"/>
      </svg>
    ),
  },
];

const NAV_INSIGHTS: NavItem[] = [
  {
    href: '/dashboards',
    labelKey: 'dashboards',
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none">
        <rect x="3" y="3" width="8" height="10" rx="1" stroke="currentColor" strokeWidth="1.8"/>
        <rect x="13" y="3" width="8" height="6" rx="1" stroke="currentColor" strokeWidth="1.8"/>
        <rect x="13" y="13" width="8" height="8" rx="1" stroke="currentColor" strokeWidth="1.8"/>
        <rect x="3" y="17" width="8" height="4" rx="1" stroke="currentColor" strokeWidth="1.8"/>
      </svg>
    ),
  },
  {
    href: '/analytics',
    labelKey: 'analytics',
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none">
        <path d="M3 3v18h18" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"/>
        <path d="M7 16l4-5 4 3 4-7" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"/>
      </svg>
    ),
  },
];

const NAV_BOTTOM: NavItem[] = [
  {
    href: '/settings',
    labelKey: 'settings',
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none">
        <circle cx="12" cy="12" r="3" stroke="currentColor" strokeWidth="1.8"/>
        <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" stroke="currentColor" strokeWidth="1.8"/>
      </svg>
    ),
  },
];

function NavLink({
  item,
  active,
  badgeCount,
}: {
  item: NavItem;
  active: boolean;
  badgeCount?: number;
}) {
  const { t } = useSettings();
  const label = t(item.labelKey);

  return (
    <Link
      href={item.href}
      className="group/item relative flex items-center gap-3 px-2 py-2.5 rounded-lg transition-all duration-150 whitespace-nowrap overflow-visible"
      style={{
        color: active ? '#ffffff' : 'var(--sidebar-text)',
        background: active ? 'rgba(255,255,255,0.15)' : 'transparent',
      }}
      onMouseEnter={e => {
        if (!active) (e.currentTarget as HTMLElement).style.background = 'rgba(255,255,255,0.10)';
      }}
      onMouseLeave={e => {
        if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent';
      }}
    >
      {/* Icon + badge wrapper */}
      <span className="relative shrink-0">
        {item.icon}
        {item.badge === 'approvals' && badgeCount != null && badgeCount > 0 && (
          <span className="absolute -top-1.5 -right-1.5 min-w-[16px] h-4 px-1 rounded-full bg-[#e2445c] text-white text-[9px] font-bold flex items-center justify-center animate-pulse">
            {badgeCount > 99 ? '99+' : badgeCount}
          </span>
        )}
      </span>

      {/* Label — visible when sidebar expanded */}
      <span className="text-sm font-medium opacity-0 group-hover/sidebar:opacity-100 transition-opacity duration-150 flex-1">
        {label}
      </span>

      {/* Badge count in label row (only when expanded) */}
      {item.badge === 'approvals' && badgeCount != null && badgeCount > 0 && (
        <span className="opacity-0 group-hover/sidebar:opacity-100 transition-opacity duration-150 min-w-[18px] h-[18px] px-1 rounded-full bg-[#e2445c] text-white text-[10px] font-bold flex items-center justify-center shrink-0">
          {badgeCount > 99 ? '99+' : badgeCount}
        </span>
      )}

      {/* Tooltip — visible on item hover when sidebar is COLLAPSED */}
      <span className="
        pointer-events-none absolute left-[calc(100%+10px)] z-50
        bg-[#1e2235] text-white text-xs font-medium px-2.5 py-1.5 rounded-lg
        whitespace-nowrap shadow-lg border border-white/10
        opacity-0 group-hover/item:opacity-100 group-hover/sidebar:!opacity-0
        transition-opacity duration-100
      ">
        {label}
        {item.badge === 'approvals' && badgeCount != null && badgeCount > 0 && (
          <span className="ml-1.5 bg-[#e2445c] text-white text-[9px] font-bold px-1 rounded-full">
            {badgeCount}
          </span>
        )}
      </span>
    </Link>
  );
}

export function Sidebar() {
  const pathname = usePathname();
  const { t } = useSettings();
  const [pendingApprovals, setPendingApprovals] = useState(0);

  useEffect(() => {
    api.approvals.pending()
      .then(list => setPendingApprovals(list.length))
      .catch(() => {});
  }, []);

  return (
    <aside
      className="group/sidebar flex flex-col shrink-0 overflow-visible transition-all duration-200"
      style={{
        width: '56px',
        background: 'var(--sidebar-bg)',
        zIndex: 40,
      }}
      onMouseEnter={e => { (e.currentTarget as HTMLElement).style.width = '220px'; }}
      onMouseLeave={e => { (e.currentTarget as HTMLElement).style.width = '56px'; }}
    >
      {/* Main nav group */}
      <nav className="flex flex-col gap-0.5 pt-3 px-2 flex-1">
        {NAV_MAIN.map(item => (
          <NavLink
            key={item.href}
            item={item}
            active={pathname.startsWith(item.href)}
            badgeCount={item.badge === 'approvals' ? pendingApprovals : undefined}
          />
        ))}

        {/* Separator */}
        <div className="my-2 border-t border-white/10" />

        {/* Insights label */}
        <p className="px-2 text-[9px] uppercase tracking-widest font-semibold opacity-0 group-hover/sidebar:opacity-50 transition-opacity duration-150 text-white mb-1">
          {t('insights')}
        </p>
        {NAV_INSIGHTS.map(item => (
          <NavLink
            key={item.href}
            item={item}
            active={pathname.startsWith(item.href)}
          />
        ))}
      </nav>

      {/* Bottom: settings + user avatar */}
      <div className="px-2 pb-3 flex flex-col gap-0.5">
        {NAV_BOTTOM.map(item => (
          <NavLink
            key={item.href}
            item={item}
            active={pathname.startsWith(item.href)}
          />
        ))}

        {/* Separator */}
        <div className="my-2 border-t border-white/10" />

        {/* User avatar */}
        <div className="flex items-center gap-3 px-2 py-2 rounded-lg whitespace-nowrap overflow-hidden">
          <div className="w-7 h-7 rounded-full bg-[#0073ea] flex items-center justify-center text-white text-xs font-bold shrink-0 ring-2 ring-white/20">
            T
          </div>
          <div className="opacity-0 group-hover/sidebar:opacity-100 transition-opacity duration-150 flex flex-col min-w-0">
            <span className="text-xs font-semibold text-white truncate">Tectika</span>
            <span className="text-[10px] text-white/50 truncate">weber@tectika.com</span>
          </div>
        </div>
      </div>
    </aside>
  );
}
