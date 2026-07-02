'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import { useSettings, type TranslationKey } from '@/lib/settings-context';
import { Icon } from '@/components/ui/icons';
import { Avatar } from '@/components/ui/primitives';
import { CURRENT_USER } from '@/lib/collaboration';

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
    href: '/connections',
    labelKey: 'connections',
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none">
        <path d="M10 13a5 5 0 0 0 7.07 0l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"/>
        <path d="M14 11a5 5 0 0 0-7.07 0l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"/>
      </svg>
    ),
  },
  {
    href: '/interactions',
    labelKey: 'interactions',
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
  collapsed,
  badgeCount,
}: {
  item: NavItem;
  active: boolean;
  collapsed: boolean;
  badgeCount?: number;
}) {
  const { t } = useSettings();
  const label = t(item.labelKey);

  return (
    <Link
      href={item.href}
      title={collapsed ? label : undefined}
      className={`group/item relative flex items-center gap-3 ${collapsed ? 'justify-center' : ''} px-2 py-2.5 rounded-lg transition-all duration-150 whitespace-nowrap`}
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

      {!collapsed && <span className="text-sm font-medium flex-1">{label}</span>}

      {!collapsed && item.badge === 'approvals' && badgeCount != null && badgeCount > 0 && (
        <span className="min-w-[18px] h-[18px] px-1 rounded-full bg-[#e2445c] text-white text-[10px] font-bold flex items-center justify-center shrink-0">
          {badgeCount > 99 ? '99+' : badgeCount}
        </span>
      )}

      {/* Tooltip — only when collapsed */}
      {collapsed && (
        <span className="
          pointer-events-none absolute left-[calc(100%+10px)] z-50
          bg-[#1e2235] text-white text-xs font-medium px-2.5 py-1.5 rounded-lg
          whitespace-nowrap shadow-lg border border-white/10
          opacity-0 group-hover/item:opacity-100 transition-opacity duration-100
        ">
          {label}
          {item.badge === 'approvals' && badgeCount != null && badgeCount > 0 && (
            <span className="ml-1.5 bg-[#e2445c] text-white text-[9px] font-bold px-1 rounded-full">{badgeCount}</span>
          )}
        </span>
      )}
    </Link>
  );
}

const SIDEBAR_KEY = 'tectika:sidebar-collapsed';

export function Sidebar() {
  const pathname = usePathname();
  const { t } = useSettings();
  const [pendingApprovals, setPendingApprovals] = useState(0);
  const [collapsed, setCollapsed] = useState(false);

  useEffect(() => {
    api.interactions.pending().then(list => setPendingApprovals(list.length)).catch(() => {});
  }, []);
  // restore persisted collapse state after mount (avoids hydration mismatch)
  useEffect(() => {
    try { const v = localStorage.getItem(SIDEBAR_KEY); if (v) setCollapsed(v === '1'); } catch { /* ignore */ }
  }, []);

  const toggle = () => setCollapsed(c => {
    const next = !c;
    try { localStorage.setItem(SIDEBAR_KEY, next ? '1' : '0'); } catch { /* ignore */ }
    return next;
  });

  return (
    <aside
      className="flex flex-col shrink-0 h-full overflow-hidden transition-[width] duration-200"
      style={{ width: collapsed ? '56px' : '220px', background: 'var(--sidebar-bg)', zIndex: 40 }}
    >
      {/* Collapse / expand toggle */}
      <div className={`flex items-center ${collapsed ? 'justify-center' : 'justify-between'} px-2 pt-3 pb-1 shrink-0`}>
        {!collapsed && <span className="px-1 text-[10px] font-semibold uppercase tracking-widest text-white/40">Menu</span>}
        <button onClick={toggle} title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'} aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          className="w-8 h-8 flex items-center justify-center rounded-lg text-white/70 hover:bg-white/10 hover:text-white transition-colors">
          {collapsed ? <Icon.sidebarExpand size={18} /> : <Icon.sidebarCollapse size={18} />}
        </button>
      </div>

      {/* Main nav group — scrolls independently if it overflows */}
      <nav className="flex flex-col gap-0.5 px-2 flex-1 min-h-0 overflow-y-auto">
        {NAV_MAIN.map(item => (
          <NavLink key={item.href} item={item} active={pathname.startsWith(item.href)} collapsed={collapsed}
            badgeCount={item.badge === 'approvals' ? pendingApprovals : undefined} />
        ))}

        <div className="my-2 border-t border-white/10" />

        {!collapsed && <p className="px-2 text-[9px] uppercase tracking-widest font-semibold text-white/50 mb-1">{t('insights')}</p>}
        {NAV_INSIGHTS.map(item => (
          <NavLink key={item.href} item={item} active={pathname.startsWith(item.href)} collapsed={collapsed} />
        ))}
      </nav>

      {/* Bottom: settings + user — always pinned, never scrolls away */}
      <div className="px-2 pb-3 flex flex-col gap-0.5 shrink-0">
        {NAV_BOTTOM.map(item => (
          <NavLink key={item.href} item={item} active={pathname.startsWith(item.href)} collapsed={collapsed} />
        ))}

        <div className="my-2 border-t border-white/10" />

        <button
          onClick={() => window.dispatchEvent(new Event('agentboard:open-user'))}
          title="Your profile"
          className={`flex items-center gap-3 ${collapsed ? 'justify-center' : ''} px-2 py-2 rounded-lg whitespace-nowrap overflow-hidden hover:bg-white/10 transition-colors`}
        >
          <span className="shrink-0 ring-2 ring-white/20 rounded-full"><Avatar person={CURRENT_USER} size={28} /></span>
          {!collapsed && (
            <div className="flex flex-col min-w-0 text-left">
              <span className="text-xs font-semibold text-white truncate">{CURRENT_USER.name}</span>
              <span className="text-[10px] text-white/50 truncate">{CURRENT_USER.id}</span>
            </div>
          )}
        </button>
      </div>
    </aside>
  );
}
