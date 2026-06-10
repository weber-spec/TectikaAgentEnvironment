import type { Metadata } from 'next';
import { Poppins } from 'next/font/google';
import './globals.css';
import { Navbar } from '@/components/layout/Navbar';
import { Sidebar } from '@/components/layout/Sidebar';
import { SettingsProvider } from '@/lib/settings-context';
import { Toaster } from '@/components/common/Toaster';
import { CommandPalette } from '@/components/command/CommandPalette';
import { TelemetryProvider } from '@/components/observability/TelemetryProvider';
import { connection } from 'next/server';

const poppins = Poppins({
  subsets: ['latin'],
  weight: ['300', '400', '500', '600', '700'],
  display: 'swap',
});

export const metadata: Metadata = {
  title: 'AgentBoard — Tectika',
  description: 'AI Agent task management platform by Tectika',
};

export default async function RootLayout({ children }: { children: React.ReactNode }) {
  await connection(); // opt out of static prerendering so env is read at request time
  const aiConn = process.env.APPLICATIONINSIGHTS_CONNECTION_STRING ?? '';
  const logSensitive = (process.env['Logging__LogSensitiveContent'] ?? 'true') !== 'false';

  return (
    <html lang="en" className={`h-full ${poppins.className}`} suppressHydrationWarning>
      <body className="h-full overflow-hidden flex flex-col bg-[var(--background)] text-[var(--foreground)]">
        <TelemetryProvider connectionString={aiConn} logSensitiveContent={logSensitive}>
          <SettingsProvider>
            <Navbar />
            <div className="flex flex-1 min-h-0 overflow-hidden">
              <Sidebar />
              <main className="flex-1 min-w-0 overflow-y-auto bg-[var(--background)]">{children}</main>
            </div>
            <Toaster />
            <CommandPalette />
          </SettingsProvider>
        </TelemetryProvider>
      </body>
    </html>
  );
}
