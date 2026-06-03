'use client';

import Image from 'next/image';
import Link from 'next/link';
import { useState } from 'react';
import { NotificationPanel, type Notification } from './NotificationPanel';
import { useSettings } from '@/lib/settings-context';
import { SearchBar } from './SearchBar';

const MOCK_NOTIFICATIONS: Notification[] = [
  {
    id: '1',
    type: 'approval',
    title: 'Deploy to Production needs approval',
    subtitle: 'Board: Backend Pipeline',
    boardId: undefined,
    taskId: undefined,
    timestamp: new Date(Date.now() - 2 * 60 * 1000),
    read: false,
  },
  {
    id: '2',
    type: 'completed',
    title: 'Generate API docs — Done',
    subtitle: 'Agent: DocWriter finished successfully',
    timestamp: new Date(Date.now() - 15 * 60 * 1000),
    read: false,
  },
  {
    id: '3',
    type: 'failed',
    title: 'Run tests — Failed',
    subtitle: 'CI agent encountered an error',
    timestamp: new Date(Date.now() - 40 * 60 * 1000),
    read: true,
  },
  {
    id: '4',
    type: 'agent',
    title: 'Coder agent started new task',
    subtitle: 'Implementing auth middleware',
    timestamp: new Date(Date.now() - 2 * 60 * 60 * 1000),
    read: true,
  },
];

export function Navbar() {
  const { t } = useSettings();
  const [notifications, setNotifications] = useState<Notification[]>(MOCK_NOTIFICATIONS);
  const [showPanel, setShowPanel] = useState(false);

  const unreadCount = notifications.filter(n => !n.read).length;

  function markAllRead() {
    setNotifications(prev => prev.map(n => ({ ...n, read: true })));
  }

  return (
    <header className="sticky top-0 z-50 h-12 bg-white border-b border-[#e6e9ef] flex items-center px-4 gap-3 shadow-[0_1px_4px_rgba(0,0,0,0.08)]">
      {/* Logo */}
      <Link href="/boards" className="flex items-center gap-2 shrink-0 mr-2">
        <Image
          src="https://i.ibb.co/LJ1H14k/Tectika-ai-icon-only.png"
          alt="Tectika"
          width={26}
          height={26}
          className="rounded-md"
          unoptimized
        />
        <span className="font-semibold text-sm text-[#323338] tracking-tight">
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
            className="w-8 h-8 flex items-center justify-center rounded-full text-[#676879] hover:bg-[#f5f6f8] transition-colors relative"
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
              onMarkAllRead={markAllRead}
              onClose={() => setShowPanel(false)}
            />
          )}
        </div>

        {/* User avatar */}
        <div className="w-8 h-8 rounded-full bg-[#0073ea] flex items-center justify-center text-white text-xs font-bold cursor-pointer select-none ring-2 ring-[#0073ea]/20 hover:ring-[#0073ea]/50 transition-all">
          T
        </div>
      </div>
    </header>
  );
}
