import type { Metadata } from 'next';
import { Sora, Inter } from 'next/font/google';
import './globals.css';
import { Navbar } from '@/components/layout/Navbar';

const sora = Sora({
  subsets: ['latin'],
  weight: ['300', '400', '500', '600', '700'],
  variable: '--font-sora',
  display: 'swap',
});

const inter = Inter({
  subsets: ['latin'],
  weight: ['300', '400', '500', '600', '700'],
  variable: '--font-inter',
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
    <html lang="en" className={`h-full ${sora.variable} ${inter.variable}`}>
      <body className="min-h-full flex flex-col bg-[#0f1117] text-[#e8ecf4]">
        <Navbar />
        <main className="flex-1 flex flex-col">{children}</main>
      </body>
    </html>
  );
}
