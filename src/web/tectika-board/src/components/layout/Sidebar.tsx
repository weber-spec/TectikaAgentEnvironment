'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';

const NAV_ITEMS = [
  {
    href: '/boards',
    label: 'Boards',
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
    href: '/approvals',
    label: 'Approvals',
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none">
        <path d="M9 12l2 2 4-4" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"/>
        <circle cx="12" cy="12" r="9" stroke="currentColor" strokeWidth="1.8"/>
      </svg>
    ),
  },
];

export function Sidebar() {
  const pathname = usePathname();

  return (
    <aside
      className="group flex flex-col shrink-0 overflow-hidden transition-all duration-200"
      style={{
        width: '56px',
        background: 'var(--sidebar-bg)',
      }}
      onMouseEnter={e => { (e.currentTarget as HTMLElement).style.width = '220px'; }}
      onMouseLeave={e => { (e.currentTarget as HTMLElement).style.width = '56px'; }}
    >
      {/* Nav items */}
      <nav className="flex flex-col gap-1 pt-3 px-2 flex-1">
        {NAV_ITEMS.map(({ href, label, icon }) => {
          const active = pathname.startsWith(href);
          return (
            <Link
              key={href}
              href={href}
              className="flex items-center gap-3 px-2 py-2.5 rounded-lg transition-colors whitespace-nowrap overflow-hidden"
              style={{
                color: active ? '#ffffff' : 'var(--sidebar-text)',
                background: active ? 'var(--sidebar-active)' : 'transparent',
              }}
              onMouseEnter={e => {
                if (!active) (e.currentTarget as HTMLElement).style.background = 'var(--sidebar-active)';
              }}
              onMouseLeave={e => {
                if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent';
              }}
            >
              <span className="shrink-0">{icon}</span>
              <span className="text-sm font-medium opacity-0 group-hover:opacity-100 transition-opacity duration-150">
                {label}
              </span>
            </Link>
          );
        })}
      </nav>

      {/* Bottom: user avatar */}
      <div className="px-2 pb-4">
        <div className="flex items-center gap-3 px-2 py-2 rounded-lg whitespace-nowrap overflow-hidden">
          <div className="w-7 h-7 rounded-full bg-[#0073ea] flex items-center justify-center text-white text-xs font-bold shrink-0">
            T
          </div>
          <span className="text-xs font-medium opacity-0 group-hover:opacity-100 transition-opacity duration-150 text-white/75">
            Tectika
          </span>
        </div>
      </div>
    </aside>
  );
}
