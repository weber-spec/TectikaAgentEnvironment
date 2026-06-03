'use client';

import Image from 'next/image';
import Link from 'next/link';

export function Navbar() {
  return (
    <header className="sticky top-0 z-50 h-12 bg-white border-b border-[#e6e9ef] flex items-center px-4 gap-3 shadow-[0_1px_4px_rgba(0,0,0,0.08)]">
      {/* Logo */}
      <Link href="/boards" className="flex items-center gap-2 shrink-0 mr-2">
        <Image
          src="https://i.ibb.co/LJ1H14k/Tectika-ai-icon-only.png"
          alt="Tectika"
          width={26}
          height={26}
          className="rounded-md"
          unoptimized
        />
        <span className="font-semibold text-sm text-[#323338] tracking-tight">
          AgentBoard
        </span>
      </Link>

      {/* Search */}
      <div className="flex-1 max-w-xs">
        <div className="flex items-center gap-2 bg-[#f5f6f8] border border-[#e6e9ef] rounded-full px-3 py-1.5 text-sm text-[#676879] cursor-text hover:border-[#0073ea] transition-colors">
          <svg width="14" height="14" viewBox="0 0 20 20" fill="none" className="shrink-0">
            <circle cx="9" cy="9" r="6" stroke="#676879" strokeWidth="2"/>
            <path d="m15 15 3 3" stroke="#676879" strokeWidth="2" strokeLinecap="round"/>
          </svg>
          <span>Search</span>
        </div>
      </div>

      <div className="flex-1" />

      {/* Right side: bell + avatar */}
      <div className="flex items-center gap-3">
        {/* Notification bell */}
        <button className="w-8 h-8 flex items-center justify-center rounded-full text-[#676879] hover:bg-[#f5f6f8] transition-colors">
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none">
            <path d="M15 17h5l-1.405-1.405A2.032 2.032 0 0 1 18 14.158V11a6 6 0 0 0-5-5.917V4a1 1 0 1 0-2 0v1.083A6 6 0 0 0 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 1 1-6 0v-1m6 0H9" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
        </button>
        {/* User avatar */}
        <div className="w-8 h-8 rounded-full bg-[#0073ea] flex items-center justify-center text-white text-xs font-bold cursor-pointer select-none">
          T
        </div>
      </div>
    </header>
  );
}
