'use client';

import Link from 'next/link';
import { useSettings, type AppSettings, type TranslationKey } from '@/lib/settings-context';
import { Icon, type IconName } from '@/components/ui/icons';

// ── Reusable primitives ────────────────────────────────────────────────────────

function SectionCard({ children }: { children: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-[var(--border)] bg-[var(--background)] p-6 flex flex-col gap-5 shadow-sm">
      {children}
    </div>
  );
}

function SectionHeader({ icon, titleKey }: { icon: React.ReactNode; titleKey: TranslationKey }) {
  const { t } = useSettings();
  return (
    <div className="flex items-center gap-2.5 pb-1 border-b border-[var(--border)]">
      <span className="text-[var(--primary)] flex items-center">{icon}</span>
      <h2 className="text-base font-semibold text-[var(--foreground)]">{t(titleKey)}</h2>
    </div>
  );
}

function SettingRow({ label, description, children }: {
  label: React.ReactNode;
  description?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="flex items-center justify-between gap-4">
      <div className="flex flex-col gap-0.5 min-w-0">
        <span className="text-sm font-medium text-[var(--foreground)]">{label}</span>
        {description && (
          <span className="text-xs text-[var(--muted)]">{description}</span>
        )}
      </div>
      <div className="shrink-0">{children}</div>
    </div>
  );
}

// ── Toggle Switch ──────────────────────────────────────────────────────────────

function Toggle({ checked, onChange }: { checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <button
      role="switch"
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className="relative w-11 h-6 rounded-full transition-colors duration-200 focus:outline-none focus-visible:ring-2 focus-visible:ring-[var(--primary)]"
      style={{ background: checked ? 'var(--primary)' : 'var(--surface-2)' }}
    >
      <span
        className="absolute top-0.5 left-0.5 w-5 h-5 rounded-full bg-white shadow transition-transform duration-200"
        style={{ transform: checked ? 'translateX(20px)' : 'translateX(0)' }}
      />
    </button>
  );
}

// ── Theme Picker ───────────────────────────────────────────────────────────────

function ThemePicker() {
  const { settings, updateSettings, t } = useSettings();

  const options: Array<{ value: AppSettings['theme']; icon: React.ReactNode; key: TranslationKey }> = [
    { value: 'light', icon: <Icon.sun size={16} />,  key: 'light' },
    { value: 'dark',  icon: <Icon.moon size={16} />, key: 'dark'  },
  ];

  return (
    <div className="flex gap-2">
      {options.map(opt => {
        const active = settings.theme === opt.value;
        return (
          <button
            key={opt.value}
            onClick={() => updateSettings({ theme: opt.value })}
            className="flex items-center gap-2 px-4 py-2.5 rounded-lg border-2 text-sm font-medium transition-all duration-150"
            style={{
              borderColor: active ? 'var(--primary)' : 'var(--border)',
              background: active ? 'var(--primary-light)' : 'var(--surface)',
              color: active ? 'var(--primary)' : 'var(--muted)',
              transform: active ? 'scale(1.03)' : 'scale(1)',
            }}
          >
            <span className="flex items-center">{opt.icon}</span>
            <span>{t(opt.key)}</span>
          </button>
        );
      })}
    </div>
  );
}

// ── Language Picker ────────────────────────────────────────────────────────────

function LanguagePicker() {
  const { settings, updateSettings } = useSettings();

  const options: Array<{ value: AppSettings['language']; label: string }> = [
    { value: 'en', label: 'English' },
    { value: 'he', label: 'עברית'   },
  ];

  return (
    <div className="flex gap-2">
      {options.map(opt => {
        const active = settings.language === opt.value;
        return (
          <button
            key={opt.value}
            onClick={() => updateSettings({ language: opt.value })}
            className="flex items-center gap-2 px-4 py-2.5 rounded-lg border-2 text-sm font-medium transition-all duration-150"
            style={{
              borderColor: active ? 'var(--primary)' : 'var(--border)',
              background: active ? 'var(--primary-light)' : 'var(--surface)',
              color: active ? 'var(--primary)' : 'var(--muted)',
              transform: active ? 'scale(1.03)' : 'scale(1)',
            }}
          >
            <span className="flex items-center"><Icon.globe size={16} /></span>
            <span>{opt.label}</span>
          </button>
        );
      })}
    </div>
  );
}

// ── Page ───────────────────────────────────────────────────────────────────────

type NotificationKey = {
  key: keyof AppSettings['notifications'];
  labelKey: TranslationKey;
  descKey: TranslationKey;
  icon: IconName;
  color: string;
};

const NOTIFICATION_GROUPS: Array<{
  groupKey: TranslationKey;
  keys: NotificationKey[];
}> = [
  {
    groupKey: 'taskUpdatesGroup',
    keys: [
      { key: 'taskCompleted',      labelKey: 'taskCompleted',      descKey: 'taskCompletedDesc',      icon: 'check',   color: 'var(--status-done)' },
      { key: 'approvalRequired',   labelKey: 'approvalRequired',   descKey: 'approvalRequiredDesc',   icon: 'clock',   color: 'var(--status-approval)' },
      { key: 'taskFailed',         labelKey: 'taskFailed',         descKey: 'taskFailedDesc',         icon: 'x',       color: 'var(--status-failed)' },
      { key: 'taskBlocked',        labelKey: 'taskBlocked',        descKey: 'taskBlockedDesc',        icon: 'ban',     color: 'var(--status-blocked)' },
      { key: 'dependencyResolved', labelKey: 'dependencyResolved', descKey: 'dependencyResolvedDesc', icon: 'unlock',  color: 'var(--status-working)' },
    ],
  },
  {
    groupKey: 'agentUpdatesGroup',
    keys: [
      { key: 'agentCreated', labelKey: 'agentCreated', descKey: 'agentCreatedDesc', icon: 'robot', color: 'var(--primary)' },
      { key: 'agentDeleted', labelKey: 'agentDeleted', descKey: 'agentDeletedDesc', icon: 'robot', color: 'var(--status-failed)' },
    ],
  },
];

export default function SettingsPage() {
  const { settings, updateNotification, t } = useSettings();

  return (
    <div className="min-h-full bg-[var(--surface)] pb-16">
      {/* Page header */}
      <div className="bg-[var(--background)] border-b border-[var(--border)] px-8 py-4 flex items-center gap-4 sticky top-0 z-10">
        <Link href="/boards" className="text-[var(--muted)] hover:text-[var(--foreground)] transition-colors">
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
            <path d="M19 12H5M12 19l-7-7 7-7" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
        </Link>
        <div>
          <h1 className="text-lg font-semibold text-[var(--foreground)]">{t('settingsTitle')}</h1>
          <p className="text-xs text-[var(--muted)] mt-0.5">{t('settingsSubtitle')}</p>
        </div>
        <div className="flex-1" />
        <span className="text-xs text-[var(--muted)] flex items-center gap-1">
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none">
            <path d="M20 6L9 17l-5-5" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
          {t('saveNote')}
        </span>
      </div>

      {/* Content */}
      <div className="max-w-2xl mx-auto px-6 py-8 flex flex-col gap-6">

        {/* Appearance */}
        <SectionCard>
          <SectionHeader icon={<Icon.palette size={18} />} titleKey="appearance" />
          <p className="text-xs text-[var(--muted)] -mt-2">{t('appearanceDesc')}</p>
          <SettingRow label={t('theme')}>
            <ThemePicker />
          </SettingRow>
        </SectionCard>

        {/* Language */}
        <SectionCard>
          <SectionHeader icon={<Icon.globe size={18} />} titleKey="languageSection" />
          <p className="text-xs text-[var(--muted)] -mt-2">{t('languageDesc')}</p>
          <SettingRow label={t('language')}>
            <LanguagePicker />
          </SettingRow>
          {settings.language === 'he' && (
            <div className="flex items-center gap-2 px-3 py-2 rounded-lg bg-[var(--primary-light)] text-xs text-[var(--primary)]">
              <Icon.refresh size={14} />
              <span>{t('directionNote')}</span>
            </div>
          )}
        </SectionCard>

        {/* Notifications */}
        <SectionCard>
          <SectionHeader icon={<Icon.bell size={18} />} titleKey="notifications" />
          <p className="text-xs text-[var(--muted)] -mt-2">{t('notificationsDesc')}</p>
          <div className="flex flex-col gap-6">
            {NOTIFICATION_GROUPS.map(({ groupKey, keys }) => (
              <div key={groupKey} className="flex flex-col gap-3">
                <p className="text-xs font-semibold uppercase tracking-wider text-[var(--muted)]">
                  {t(groupKey)}
                </p>
                <div className="flex flex-col gap-4">
                  {keys.map(({ key, labelKey, descKey, icon, color }) => {
                    const I = Icon[icon];
                    return (
                      <SettingRow
                        key={key}
                        label={
                          <span className="inline-flex items-center gap-2">
                            <span className="flex items-center" style={{ color }}><I size={16} /></span>
                            {t(labelKey)}
                          </span>
                        }
                        description={t(descKey)}
                      >
                        <Toggle
                          checked={settings.notifications[key]}
                          onChange={v => updateNotification(key, v)}
                        />
                      </SettingRow>
                    );
                  })}
                </div>
              </div>
            ))}
          </div>
        </SectionCard>

        {/* About */}
        <SectionCard>
          <SectionHeader icon={<Icon.info size={18} />} titleKey="about" />
          <div className="flex flex-col gap-2 text-sm">
            <div className="flex items-center justify-between">
              <span className="text-[var(--muted)]">{t('version')}</span>
              <span className="font-mono text-xs bg-[var(--surface)] px-2 py-1 rounded text-[var(--foreground)]">0.1.0</span>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-[var(--muted)]">{t('poweredBy')}</span>
              <span className="inline-flex items-center gap-1 text-xs font-semibold text-[var(--primary)]">
                Tectika AI <Icon.bolt size={13} />
              </span>
            </div>
          </div>
        </SectionCard>

      </div>
    </div>
  );
}
