import type { Metadata } from 'next';
import { Poppins } from 'next/font/google';
import './globals.css';
import { Navbar } from '@/components/layout/Navbar';
import { Sidebar } from '@/components/layout/Sidebar';

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
    <html lang="en" className={`h-full ${poppins.className}`}>
      <body className="min-h-full flex flex-col bg-white text-[#323338]">
        <Navbar />
        <div className="flex flex-1 overflow-hidden">
          <Sidebar />
          <main className="flex-1 overflow-auto bg-white">{children}</main>
        </div>
      </body>
    </html>
  );
}
