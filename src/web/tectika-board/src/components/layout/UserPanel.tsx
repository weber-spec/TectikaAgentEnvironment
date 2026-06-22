'use client';

import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { useRouter } from 'next/navigation';
import { useSettings } from '@/lib/settings-context';
import { CURRENT_USER } from '@/lib/collaboration';
import { Avatar } from '@/components/ui/primitives';
import { Icon, type IconName } from '@/components/ui/icons';
import { api } from '@/lib/api';
import { toast } from '@/lib/toast';

const QUICK_LINKS: Array<{ href: string; label: string; icon: IconName; badge?: 'approvals' }> = [
  { href: '/boards', label: 'Boards', icon: 'board' },
  { href: '/agents', label: 'Agents', icon: 'robot' },
  { href: '/interactions', label: 'Interactions', icon: 'approvals', badge: 'approvals' },
  { href: '/dashboards', label: 'Dashboards', icon: 'chart' },
  { href: '/analytics', label: 'Analytics', icon: 'chart' },
  { href: '/settings', label: 'All settings', icon: 'settings' },
];

export function UserPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const router = useRouter();
  const { settings, updateSettings } = useSettings();
  const [pending, setPending] = useState(0);

  useEffect(() => {
    if (!open) return;
    api.interactions.pending().then(l => setPending(l.length)).catch(() => {});
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  if (!open) return null;

  const go = (href: string) => { router.push(href); onClose(); };

  return createPortal(
    <div className="fixed inset-0 z-[1300] flex justify-end" style={{ background: 'rgba(0,0,0,0.35)' }} onMouseDown={onClose}>
      <div className="bg-[var(--background)] h-full w-full max-w-[372px] shadow-2xl flex flex-col animate-slide-in-right" onMouseDown={e => e.stopPropagation()}>
        {/* header / banner */}
        <div className="relative shrink-0">
          <div className="h-20" style={{ background: 'linear-gradient(135deg, #0073ea, #00c875)' }} />
          <button onClick={onClose} aria-label="Close" className="absolute top-3 right-3 w-8 h-8 flex items-center justify-center rounded-full bg-black/15 text-white hover:bg-black/30 transition-colors"><Icon.x size={18} /></button>
          <div className="px-5 -mt-9 flex items-end gap-3">
            <div className="ring-4 ring-[var(--background)] rounded-full"><Avatar person={CURRENT_USER} size={64} /></div>
            <div className="pb-1 min-w-0">
              <h2 className="text-base font-bold text-[var(--foreground)] truncate">{CURRENT_USER.name}</h2>
              <p className="text-xs text-[var(--muted)] truncate">{CURRENT_USER.id}</p>
            </div>
          </div>
          <div className="px-5 mt-2">
            <span className="inline-flex items-center gap-1 text-[11px] font-medium px-2 py-0.5 rounded-full bg-[var(--primary-light)] text-[var(--primary)]">
              <Icon.user size={11} /> {CURRENT_USER.title ?? 'Member'}
            </span>
          </div>
        </div>

        <div className="flex-1 overflow-auto px-5 py-4 flex flex-col gap-5">
          {/* Appearance */}
          <Section title="Appearance">
            <Row label="Theme">
              <Segmented value={settings.theme}
                options={[{ v: 'light', icon: <Icon.sun size={14} />, label: 'Light' }, { v: 'dark', icon: <Icon.moon size={14} />, label: 'Dark' }]}
                onChange={v => updateSettings({ theme: v as 'light' | 'dark' })} />
            </Row>
            <Row label="Language">
              <Segmented value={settings.language}
                options={[{ v: 'en', label: 'EN' }, { v: 'he', label: 'עברית' }]}
                onChange={v => updateSettings({ language: v as 'en' | 'he' })} />
            </Row>
          </Section>

          {/* Workspace shortcuts */}
          <Section title="Workspace">
            <div className="flex flex-col -mx-1">
              {QUICK_LINKS.map(l => {
                const I = Icon[l.icon];
                return (
                  <button key={l.href} onClick={() => go(l.href)} className="flex items-center gap-3 px-1.5 py-2 rounded-lg hover:bg-[var(--surface)] transition-colors text-left">
                    <span className="w-7 h-7 rounded-lg bg-[var(--surface)] flex items-center justify-center text-[var(--muted)] shrink-0"><I size={15} /></span>
                    <span className="flex-1 text-sm text-[var(--foreground)]">{l.label}</span>
                    {l.badge === 'approvals' && pending > 0 && <span className="min-w-[18px] h-[18px] px-1 rounded-full bg-[#e2445c] text-white text-[10px] font-bold flex items-center justify-center">{pending}</span>}
                    <Icon.chevronRight size={15} className="text-[var(--muted-2)]" />
                  </button>
                );
              })}
            </div>
          </Section>

          {/* Account */}
          <Section title="Account">
            <Info label="Organization" value="Tectika" />
            <Info label="Plan" value={<span className="inline-flex items-center gap-1 text-[var(--primary)] font-semibold"><Icon.star size={12} /> Enterprise</span>} />
            <Info label="Tenant" value="default" />
            <div className="flex items-center justify-between text-xs mt-1">
              <span className="text-[var(--muted)]">Command palette</span>
              <kbd className="font-mono text-[10px] bg-[var(--surface)] border border-[var(--border)] rounded px-1.5 py-0.5 text-[var(--muted)]">⌘K</kbd>
            </div>
          </Section>
        </div>

        {/* footer */}
        <div className="shrink-0 border-t border-[var(--border)] p-4 flex items-center justify-between">
          <span className="text-[10px] text-[var(--muted-2)]">AgentBoard v0.1.0</span>
          <button onClick={() => { toast('Signed out', 'info'); onClose(); }}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[13px] font-medium text-[#e2445c] border border-[#e2445c]/30 hover:bg-[#e2445c11] transition-colors">
            <Icon.x size={14} /> Sign out
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div>
      <h3 className="text-[10px] uppercase tracking-widest font-semibold text-[var(--muted)] mb-2">{title}</h3>
      <div className="flex flex-col gap-2.5">{children}</div>
    </div>
  );
}
function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return <div className="flex items-center justify-between gap-3"><span className="text-sm text-[var(--foreground)]">{label}</span>{children}</div>;
}
function Info({ label, value }: { label: string; value: React.ReactNode }) {
  return <div className="flex items-center justify-between text-xs"><span className="text-[var(--muted)]">{label}</span><span className="text-[var(--foreground)] font-medium">{value}</span></div>;
}
function Segmented<T extends string>({ value, options, onChange }: { value: T; options: Array<{ v: T; label: string; icon?: React.ReactNode }>; onChange: (v: T) => void }) {
  return (
    <div className="inline-flex p-0.5 rounded-lg bg-[var(--surface)] border border-[var(--border)]">
      {options.map(o => {
        const active = o.v === value;
        return (
          <button key={o.v} onClick={() => onChange(o.v)}
            className={`inline-flex items-center gap-1 px-2.5 py-1 rounded-md text-[12px] font-medium transition-colors ${active ? 'bg-[var(--background)] text-[var(--primary)] shadow-sm' : 'text-[var(--muted)] hover:text-[var(--foreground)]'}`}>
            {o.icon}{o.label}
          </button>
        );
      })}
    </div>
  );
}
