'use client';

import { useEffect, useRef } from 'react';
import { useRouter } from 'next/navigation';

export interface Notification {
  id: string;
  type: 'completed' | 'approval' | 'failed' | 'agent';
  title: string;
  subtitle?: string;
  taskId?: string;
  boardId?: string;
  timestamp: Date;
  read: boolean;
}

const TYPE_CONFIG = {
  completed: { icon: '✅', color: '#00c875', label: 'Task Completed' },
  approval:  { icon: '⏳', color: '#a25ddc', label: 'Approval Required' },
  failed:    { icon: '❌', color: '#e2445c', label: 'Task Failed' },
  agent:     { icon: '🤖', color: '#0073ea', label: 'Agent Update' },
};

function timeAgo(date: Date): string {
  const seconds = Math.floor((Date.now() - date.getTime()) / 1000);
  if (seconds < 60) return 'just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

interface Props {
  notifications: Notification[];
  onMarkAllRead: () => void;
  onClose: () => void;
}

export function NotificationPanel({ notifications, onMarkAllRead, onClose }: Props) {
  const router = useRouter();
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (panelRef.current && !panelRef.current.contains(e.target as Node)) {
        onClose();
      }
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [onClose]);

  const unread = notifications.filter(n => !n.read).length;

  function handleNotificationClick(n: Notification) {
    if (n.boardId && n.taskId) {
      router.push(`/workspace/${n.boardId}/${n.taskId}`);
    } else if (n.type === 'approval') {
      router.push('/approvals');
    }
    onClose();
  }

  return (
    <div
      ref={panelRef}
      className="absolute right-0 top-[calc(100%+8px)] z-50 w-[360px] bg-white rounded-xl shadow-2xl border border-[#e6e9ef] overflow-hidden animate-slide-in-panel"
    >
      {/* Header */}
      <div className="flex items-center justify-between px-4 py-3 border-b border-[#e6e9ef]">
        <div className="flex items-center gap-2">
          <span className="text-sm font-semibold text-[#323338]">Notifications</span>
          {unread > 0 && (
            <span className="min-w-[18px] h-[18px] px-1 rounded-full bg-[#e2445c] text-white text-[10px] font-bold flex items-center justify-center">
              {unread}
            </span>
          )}
        </div>
        <button
          onClick={onMarkAllRead}
          className="text-xs text-[#0073ea] font-semibold hover:underline"
        >
          Mark all as read
        </button>
      </div>

      {/* Notification list */}
      <div className="overflow-y-auto max-h-[400px]">
        {notifications.length === 0 ? (
          <div className="px-4 py-10 text-center text-sm text-[#676879]">
            <div className="text-2xl mb-2">🔔</div>
            All caught up!
          </div>
        ) : (
          notifications.map(n => {
            const cfg = TYPE_CONFIG[n.type];
            return (
              <button
                key={n.id}
                onClick={() => handleNotificationClick(n)}
                className="w-full flex items-start gap-3 px-4 py-3 hover:bg-[#f5f6f8] transition-colors text-left border-b border-[#f0f2f5] last:border-0"
                style={{ background: n.read ? 'transparent' : '#f8f9ff' }}
              >
                {/* Unread dot */}
                <div className="mt-1 shrink-0 relative">
                  <span className="text-lg">{cfg.icon}</span>
                  {!n.read && (
                    <span
                      className="absolute -top-0.5 -right-0.5 w-2 h-2 rounded-full"
                      style={{ background: cfg.color }}
                    />
                  )}
                </div>
                <div className="flex-1 min-w-0">
                  <p className="text-xs font-semibold text-[#323338] truncate">{n.title}</p>
                  {n.subtitle && (
                    <p className="text-xs text-[#676879] truncate mt-0.5">{n.subtitle}</p>
                  )}
                  <p className="text-[10px] text-[#c3c6d4] mt-1">{timeAgo(n.timestamp)}</p>
                </div>
                <span
                  className="shrink-0 mt-1 text-[10px] font-semibold px-1.5 py-0.5 rounded"
                  style={{ background: cfg.color + '22', color: cfg.color }}
                >
                  {cfg.label}
                </span>
              </button>
            );
          })
        )}
      </div>

      {/* Footer */}
      <div className="px-4 py-2.5 border-t border-[#e6e9ef] bg-[#f5f6f8]">
        <button
          onClick={() => { router.push('/approvals'); onClose(); }}
          className="text-xs text-[#0073ea] font-semibold hover:underline"
        >
          View all approvals →
        </button>
      </div>
    </div>
  );
}
