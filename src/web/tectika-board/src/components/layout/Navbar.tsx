'use client';

import Link from 'next/link';
import { useState, useEffect } from 'react';
import { NotificationPanel } from './NotificationPanel';
import { UserPanel } from './UserPanel';
import { useSettings } from '@/lib/settings-context';
import { useNotifications } from '@/lib/useNotifications';
import { SearchBar } from './SearchBar';
import { Avatar } from '@/components/ui/primitives';
import { CURRENT_USER } from '@/lib/collaboration';

export function Navbar() {
  const { t } = useSettings();
  const { notifications, unreadCount, markAllRead } = useNotifications();
  const [showPanel, setShowPanel] = useState(false);
  const [showUser, setShowUser] = useState(false);

  useEffect(() => {
    const openUser = () => setShowUser(true);
    window.addEventListener('agentboard:open-user', openUser);
    return () => window.removeEventListener('agentboard:open-user', openUser);
  }, []);

  return (
    <header className="sticky top-0 z-50 h-12 bg-[var(--background)] border-b border-[var(--border)] flex items-center px-4 gap-3 shadow-[0_1px_4px_rgba(0,0,0,0.08)]">
      {/* Logo */}
      <Link href="/boards" className="flex items-center gap-2 shrink-0 mr-2">
        <span className="w-7 h-7 rounded-lg flex items-center justify-center shrink-0" style={{ background: 'linear-gradient(135deg, #0073ea, #00c875)' }}>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none"><path d="M13 2 4 14h7l-1 8 9-12h-7l1-8Z" fill="#fff" /></svg>
        </span>
        <span className="font-semibold text-sm text-[var(--foreground)] tracking-tight">
          AgentBoard
        </span>
      </Link>

      {/* Search */}
      <SearchBar />

      <div className="flex-1" />

      {/* Right side: bell + avatar */}
      <div className="flex items-center gap-3">
        {/* Notification bell */}
        <div className="relative">
          <button
            onClick={() => setShowPanel(v => !v)}
            className="w-8 h-8 flex items-center justify-center rounded-full text-[var(--muted)] hover:bg-[var(--surface)] transition-colors relative"
            aria-label={t('notifications')}
          >
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none">
              <path d="M15 17h5l-1.405-1.405A2.032 2.032 0 0 1 18 14.158V11a6 6 0 0 0-5-5.917V4a1 1 0 1 0-2 0v1.083A6 6 0 0 0 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 1 1-6 0v-1m6 0H9" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"/>
            </svg>
            {unreadCount > 0 && (
              <span className="absolute top-0.5 right-0.5 min-w-[16px] h-4 px-1 rounded-full bg-[#e2445c] text-white text-[9px] font-bold flex items-center justify-center animate-pulse">
                {unreadCount > 9 ? '9+' : unreadCount}
              </span>
            )}
          </button>

          {showPanel && (
            <NotificationPanel
              notifications={notifications}
              onMarkAllRead={() => { void markAllRead(); }}
              onClose={() => setShowPanel(false)}
            />
          )}
        </div>

        {/* User avatar → profile panel */}
        <button onClick={() => setShowUser(true)} aria-label="Open profile" className="rounded-full ring-2 ring-[#0073ea]/20 hover:ring-[#0073ea]/60 transition-all">
          <Avatar person={CURRENT_USER} size={32} />
        </button>
      </div>
      <UserPanel open={showUser} onClose={() => setShowUser(false)} />
    </header>
  );
}
