'use client';

import { useToasts, dismissToast } from '@/lib/toast';
import { Icon } from '@/components/ui/icons';

const KIND_STYLE: Record<string, { bg: string; icon: React.ReactNode }> = {
  success: { bg: '#00c875', icon: <Icon.check size={16} /> },
  error: { bg: '#e2445c', icon: <Icon.warning size={16} /> },
  info: { bg: '#0073ea', icon: <Icon.bolt size={16} /> },
};

export function Toaster() {
  const toasts = useToasts();
  if (toasts.length === 0) return null;
  return (
    <div className="fixed bottom-5 left-1/2 -translate-x-1/2 z-[2000] flex flex-col gap-2 items-center pointer-events-none">
      {toasts.map(t => {
        const s = KIND_STYLE[t.kind] ?? KIND_STYLE.info;
        return (
          <div key={t.id}
            className="pointer-events-auto flex items-center gap-3 px-4 py-2.5 rounded-lg shadow-2xl text-white text-sm font-medium animate-slide-in-panel min-w-[260px] max-w-[480px]"
            style={{ background: '#323338' }}>
            <span className="shrink-0 rounded-full w-6 h-6 flex items-center justify-center" style={{ background: s.bg }}>{s.icon}</span>
            <span className="flex-1">{t.message}</span>
            {t.action && (
              <button onClick={() => { t.action!.onClick(); dismissToast(t.id); }}
                className="font-semibold text-[var(--primary)] hover:underline shrink-0" style={{ color: '#6db0ff' }}>
                {t.action.label}
              </button>
            )}
            <button onClick={() => dismissToast(t.id)} className="shrink-0 opacity-60 hover:opacity-100"><Icon.x size={14} /></button>
          </div>
        );
      })}
    </div>
  );
}
