'use client';

import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { AppNotification } from '@/lib/types';
import { Icon } from '@/components/ui/icons';

const API_BASE = api.base;

type NotificationType = 'approval' | 'completed' | 'failed' | 'agent';

const TYPE_CONFIG: Record<NotificationType, { icon: string; color: string; label: string }> = {
  completed: { icon: 'checkCircle', color: '#00c875', label: 'Completed' },
  failed:    { icon: 'alertCircle', color: '#e2445c', label: 'Failed' },
  approval:  { icon: 'clock',       color: '#fdab3d', label: 'Approval' },
  agent:     { icon: 'robot',       color: '#0073ea', label: 'Agent' },
};

function timeAgo(date: Date): string {
  const seconds = Math.floor((Date.now() - date.getTime()) / 1000);
  if (seconds < 60) return 'Just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

function formatDate(date: Date): string {
  return date.toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

export default function NotificationsPage() {
  const [notifications, setNotifications] = useState<AppNotification[] | null>(null);
  const [error, setError] = useState(false);

  useEffect(() => {
    fetch(`${API_BASE}/api/notifications?limit=100`)
      .then(r => {
        if (!r.ok) throw new Error(`HTTP ${r.status}`);
        return r.json() as Promise<AppNotification[]>;
      })
      .then(setNotifications)
      .catch(() => { setNotifications([]); setError(true); });
  }, []);

  return (
    <div className="max-w-2xl mx-auto px-4 py-8">
      <div className="flex items-center gap-3 mb-6">
        <span className="w-8 h-8 rounded-lg flex items-center justify-center" style={{ background: 'var(--primary-light)', color: 'var(--primary)' }}>
          <Icon.bell size={18} />
        </span>
        <h1 className="text-xl font-semibold text-[var(--foreground)]">All Notifications</h1>
        {notifications !== null && (
          <span className="ml-auto text-xs text-[var(--muted)]">{notifications.length} total (last 100)</span>
        )}
      </div>

      {error && (
        <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3 text-sm text-[var(--muted)] mb-4">
          Could not load notifications from the server.
        </div>
      )}

      {notifications === null ? (
        <div className="space-y-2">
          {[...Array(5)].map((_, i) => (
            <div key={i} className="h-16 rounded-xl bg-[var(--surface)] animate-pulse" />
          ))}
        </div>
      ) : notifications.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-20 text-[var(--muted)]">
          <Icon.bell size={36} className="mb-3 opacity-30" />
          <p className="text-sm">No notifications yet</p>
        </div>
      ) : (
        <div className="rounded-xl border border-[var(--border)] overflow-hidden bg-[var(--background)]">
          {notifications.map((n, idx) => {
            const type = n.type as NotificationType;
            const cfg = TYPE_CONFIG[type] ?? TYPE_CONFIG.agent;
            const date = new Date(n.timestamp);
            const I = Icon[cfg.icon as keyof typeof Icon] as React.FC<{ size?: number }>;

            return (
              <div
                key={n.id}
                className="flex items-start gap-3 px-4 py-3 border-b border-[var(--border)] last:border-0 hover:bg-[var(--surface)] transition-colors"
              >
                <span
                  className="mt-0.5 shrink-0 w-8 h-8 rounded-lg flex items-center justify-center"
                  style={{ background: cfg.color + '22', color: cfg.color }}
                >
                  {I && <I size={16} />}
                </span>

                <div className="flex-1 min-w-0">
                  <p className="text-sm font-semibold text-[var(--foreground)] truncate">{n.title}</p>
                  {n.subtitle && (
                    <p className="text-xs text-[var(--muted)] truncate mt-0.5">{n.subtitle}</p>
                  )}
                  <p className="text-[11px] text-[var(--muted-2)] mt-1" title={date.toLocaleString()}>
                    {formatDate(date)} · {timeAgo(date)}
                  </p>
                </div>

                <span
                  className="shrink-0 mt-1 text-[10px] font-semibold px-2 py-0.5 rounded"
                  style={{ background: cfg.color + '22', color: cfg.color }}
                >
                  {cfg.label}
                </span>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
