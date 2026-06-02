'use client';

import Image from 'next/image';
import Link from 'next/link';
import { usePathname } from 'next/navigation';

const NAV_LINKS = [
  { href: '/boards',    label: 'Boards' },
  { href: '/approvals', label: 'Approvals' },
];

export function Navbar() {
  const pathname = usePathname();

  return (
    <header className="sticky top-0 z-50 border-b border-[#2d3651] bg-[#0f1117]/80 backdrop-blur-md">
      <div className="max-w-screen-xl mx-auto px-6 h-14 flex items-center gap-6">

        {/* Logo */}
        <Link href="/boards" className="flex items-center gap-2.5 shrink-0">
          <Image
            src="https://i.ibb.co/LJ1H14k/Tectika-ai-icon-only.png"
            alt="Tectika"
            width={28}
            height={28}
            className="rounded-lg"
            unoptimized
          />
          <span
            className="font-semibold text-sm tracking-wide gradient-text"
            style={{ fontFamily: 'var(--font-sora), sans-serif' }}
          >
            AgentBoard
          </span>
        </Link>

        {/* Divider */}
        <div className="h-5 w-px bg-[#2d3651]" />

        {/* Nav links */}
        <nav className="flex items-center gap-1">
          {NAV_LINKS.map(({ href, label }) => {
            const active = pathname.startsWith(href);
            return (
              <Link
                key={href}
                href={href}
                className={`px-3 py-1.5 rounded-lg text-sm font-medium transition-colors ${
                  active
                    ? 'bg-[#232a3b] text-[#e8ecf4]'
                    : 'text-[#8892aa] hover:text-[#e8ecf4] hover:bg-[#1a1f2e]'
                }`}
              >
                {label}
              </Link>
            );
          })}
        </nav>

        {/* Spacer */}
        <div className="flex-1" />

        {/* Status dot — placeholder for auth */}
        <div className="flex items-center gap-2 text-xs text-[#8892aa]">
          <span className="w-1.5 h-1.5 rounded-full bg-[#10b981] animate-pulse" />
          <span>Connected</span>
        </div>
      </div>
    </header>
  );
}
