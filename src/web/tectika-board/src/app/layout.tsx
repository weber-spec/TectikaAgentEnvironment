import type { Metadata } from 'next';
import { Poppins } from 'next/font/google';
import './globals.css';
import { Navbar } from '@/components/layout/Navbar';
import { Sidebar } from '@/components/layout/Sidebar';
import { SettingsProvider } from '@/lib/settings-context';
import { Toaster } from '@/components/common/Toaster';
import { CommandPalette } from '@/components/command/CommandPalette';

const poppins = Poppins({
  subsets: ['latin'],
  weight: ['300', '400', '500', '600', '700'],
  display: 'swap',
});

export const metadata: Metadata = {
  title: 'AgentBoard — Tectika',
  description: 'AI Agent task management platform by Tectika',
  icons: {
    icon: 'https://i.ibb.co/LJ1H14k/Tectika-ai-icon-only.png',
  },
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className={`h-full ${poppins.className}`} suppressHydrationWarning>
      <body className="min-h-full flex flex-col bg-[var(--background)] text-[var(--foreground)]">
        <SettingsProvider>
          <Navbar />
          <div className="flex flex-1 overflow-hidden">
            <Sidebar />
            <main className="flex-1 overflow-hidden bg-[var(--background)]">{children}</main>
          </div>
          <Toaster />
          <CommandPalette />
        </SettingsProvider>
      </body>
    </html>
  );
}
