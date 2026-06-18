'use client';

import React, { createContext, useContext, useEffect, useState } from 'react';

// ── Types ──────────────────────────────────────────────────────────────────────

export interface AppSettings {
  theme: 'light' | 'dark';
  language: 'en' | 'he';
  notifications: {
    // Task updates
    taskCompleted: boolean;
    approvalRequired: boolean;
    taskFailed: boolean;
    taskBlocked: boolean;
    dependencyResolved: boolean;
    // Agent updates
    agentCreated: boolean;
    agentDeleted: boolean;
  };
}

const DEFAULT_SETTINGS: AppSettings = {
  theme: 'light',
  language: 'en',
  notifications: {
    taskCompleted: true,
    approvalRequired: true,
    taskFailed: true,
    taskBlocked: false,
    dependencyResolved: false,
    agentCreated: true,
    agentDeleted: true,
  },
};

// ── Translations ───────────────────────────────────────────────────────────────

export const TRANSLATIONS = {
  en: {
    // Nav
    boards: 'Boards',
    agents: 'Agents',
    approvals: 'Approvals',
    analytics: 'Analytics',
    dashboards: 'Dashboards',
    settings: 'Settings',
    insights: 'Insights',
    search: 'Search',
    notifications: 'Notifications',
    pricing: 'Pricing',
    // Boards page
    newBoard: 'New board',
    noBoardsYet: 'No boards yet',
    noBoardsDesc: 'Create your first board to start assigning tasks to AI agents',
    colName: 'Name',
    colCreated: 'Created',
    openBoard: 'Open board',
    canvas: 'Canvas',
    // Settings page
    settingsTitle: 'Settings',
    settingsSubtitle: 'Manage your preferences for AgentBoard',
    appearance: 'Appearance',
    appearanceDesc: 'Choose how AgentBoard looks',
    theme: 'Theme',
    light: 'Light',
    dark: 'Dark',
    languageSection: 'Language & Region',
    languageDesc: 'Set your language and text direction',
    language: 'Language',
    directionNote: 'Hebrew automatically switches layout to right-to-left',
    notificationsDesc: 'Choose which actions send you a notification',
    taskUpdatesGroup: 'Task Updates',
    taskCompleted: 'Task Completed',
    taskCompletedDesc: 'When an agent finishes a task successfully',
    approvalRequired: 'Approval Required',
    approvalRequiredDesc: 'When a task needs your sign-off to continue',
    taskFailed: 'Task Failed',
    taskFailedDesc: 'When an agent run encounters an error',
    taskBlocked: 'Task Blocked',
    taskBlockedDesc: 'When a task is stuck waiting on a dependency',
    dependencyResolved: 'Dependency Resolved',
    dependencyResolvedDesc: 'When a blocker upstream is cleared',
    agentUpdatesGroup: 'Agent Updates',
    agentCreated: 'Agent Created',
    agentCreatedDesc: 'When a new agent is added to the workspace',
    agentDeleted: 'Agent Deleted',
    agentDeletedDesc: 'When an agent is removed from the workspace',
    about: 'About',
    version: 'Version',
    poweredBy: 'Powered by Tectika AI',
    saveNote: 'Changes are saved automatically',
  },
  he: {
    // Nav
    boards: 'לוחות',
    agents: 'סוכנים',
    approvals: 'אישורים',
    analytics: 'ניתוח',
    dashboards: 'לוחות מחוונים',
    settings: 'הגדרות',
    insights: 'תובנות',
    search: 'חיפוש',
    notifications: 'התראות',
    pricing: 'תמחור',
    // Boards page
    newBoard: 'לוח חדש',
    noBoardsYet: 'אין לוחות עדיין',
    noBoardsDesc: 'צור את הלוח הראשון שלך כדי להתחיל להקצות משימות לסוכני AI',
    colName: 'שם',
    colCreated: 'נוצר',
    openBoard: 'פתח לוח',
    canvas: 'קנבס',
    // Settings page
    settingsTitle: 'הגדרות',
    settingsSubtitle: 'נהל את ההעדפות שלך ב-AgentBoard',
    appearance: 'מראה',
    appearanceDesc: 'בחר את נראות ה-AgentBoard',
    theme: 'ערכת נושא',
    light: 'בהיר',
    dark: 'כהה',
    languageSection: 'שפה ואזור',
    languageDesc: 'הגדר שפה וכיוון טקסט',
    language: 'שפה',
    directionNote: 'עברית עוברת אוטומטית לפריסה מימין לשמאל',
    notificationsDesc: 'בחר על אילו פעולות לקבל התראה',
    taskUpdatesGroup: 'עדכוני משימות',
    taskCompleted: 'משימה הושלמה',
    taskCompletedDesc: 'כשסוכן מסיים משימה בהצלחה',
    approvalRequired: 'נדרש אישור',
    approvalRequiredDesc: 'כשמשימה צריכה את אישורך כדי להמשיך',
    taskFailed: 'משימה נכשלה',
    taskFailedDesc: 'כשסוכן נתקל בשגיאה',
    taskBlocked: 'משימה חסומה',
    taskBlockedDesc: 'כשמשימה ממתינה לתלות שלא הושלמה',
    dependencyResolved: 'תלות נפתרה',
    dependencyResolvedDesc: 'כשחסם בזרם עולה הוסר',
    agentUpdatesGroup: 'עדכוני סוכנים',
    agentCreated: 'סוכן נוצר',
    agentCreatedDesc: 'כשסוכן חדש מתווסף לסביבת העבודה',
    agentDeleted: 'סוכן נמחק',
    agentDeletedDesc: 'כשסוכן מוסר מסביבת העבודה',
    about: 'אודות',
    version: 'גרסה',
    poweredBy: 'מופעל על ידי Tectika AI',
    saveNote: 'השינויים נשמרים אוטומטית',
  },
} as const;

export type TranslationKey = keyof typeof TRANSLATIONS.en;

// ── Context ────────────────────────────────────────────────────────────────────

interface SettingsContextValue {
  settings: AppSettings;
  updateSettings: (partial: Partial<AppSettings>) => void;
  updateNotification: (key: keyof AppSettings['notifications'], value: boolean) => void;
  t: (key: TranslationKey) => string;
}

const SettingsContext = createContext<SettingsContextValue | null>(null);

// ── Provider ───────────────────────────────────────────────────────────────────

const STORAGE_KEY = 'tectika-settings';

function loadSettings(): AppSettings {
  if (typeof window === 'undefined') return DEFAULT_SETTINGS;
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return DEFAULT_SETTINGS;
    return { ...DEFAULT_SETTINGS, ...JSON.parse(raw) };
  } catch {
    return DEFAULT_SETTINGS;
  }
}

export function SettingsProvider({ children }: { children: React.ReactNode }) {
  const [settings, setSettings] = useState<AppSettings>(DEFAULT_SETTINGS);
  const [mounted, setMounted] = useState(false);

  // Load from localStorage on mount
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- hydrate persisted settings after mount (SSR-safe)
    setSettings(loadSettings());
    setMounted(true);
  }, []);

  // Apply settings to document
  useEffect(() => {
    if (!mounted) return;
    const root = document.documentElement;
    root.setAttribute('data-theme', settings.theme);
    root.lang = settings.language;
    root.dir = settings.language === 'he' ? 'rtl' : 'ltr';
    // Persist
    localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
  }, [settings, mounted]);

  function updateSettings(partial: Partial<AppSettings>) {
    setSettings(prev => ({ ...prev, ...partial }));
  }

  function updateNotification(key: keyof AppSettings['notifications'], value: boolean) {
    setSettings(prev => ({
      ...prev,
      notifications: { ...prev.notifications, [key]: value },
    }));
  }

  function t(key: TranslationKey): string {
    return TRANSLATIONS[settings.language][key] as string;
  }

  return (
    <SettingsContext.Provider value={{ settings, updateSettings, updateNotification, t }}>
      {children}
    </SettingsContext.Provider>
  );
}

// ── Hook ───────────────────────────────────────────────────────────────────────

export function useSettings(): SettingsContextValue {
  const ctx = useContext(SettingsContext);
  if (!ctx) throw new Error('useSettings must be used inside SettingsProvider');
  return ctx;
}
