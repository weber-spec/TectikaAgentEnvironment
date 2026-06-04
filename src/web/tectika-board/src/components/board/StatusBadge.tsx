'use client';

import { useState, useRef, useEffect } from 'react';
import type { AgentTaskStatus } from '@/lib/types';

const STATUS_CONFIG: Record<AgentTaskStatus, { label: string; bg: string; color: string }> = {
  Backlog:          { label: 'Backlog',           bg: '#c4c4c4', color: '#323338' },
  InProgress:       { label: 'Working on it',     bg: '#fdab3d', color: '#ffffff' },
  AwaitingApproval: { label: 'Awaiting Approval', bg: '#a25ddc', color: '#ffffff' },
  Blocked:          { label: 'Blocked',           bg: '#ff642e', color: '#ffffff' },
  Review:           { label: 'In Review',         bg: '#66ccff', color: '#323338' },
  Done:             { label: 'Done',              bg: '#00c875', color: '#ffffff' },
  Failed:           { label: 'Failed',            bg: '#e2445c', color: '#ffffff' },
};

const ALL_STATUSES = Object.keys(STATUS_CONFIG) as AgentTaskStatus[];

interface Props {
  status: AgentTaskStatus;
  onStatusChange?: (status: AgentTaskStatus) => void;
}

export function StatusBadge({ status, onStatusChange }: Props) {
  const [open, setOpen] = useState(false);
  const [current, setCurrent] = useState(status);
  const [flash, setFlash] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    setCurrent(status);
  }, [status]);

  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    if (open) document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [open]);

  const { label, bg, color } = STATUS_CONFIG[current];

  function handleSelect(s: AgentTaskStatus) {
    setCurrent(s);
    setOpen(false);
    setFlash(true);
    setTimeout(() => setFlash(false), 800);
    onStatusChange?.(s);
  }

  return (
    <div ref={ref} className="relative inline-block">
      <button
        onClick={() => onStatusChange ? setOpen(v => !v) : undefined}
        className="monday-status transition-all duration-200"
        style={{
          background: bg,
          color,
          cursor: onStatusChange ? 'pointer' : 'default',
          outline: open ? `2px solid ${bg}` : 'none',
          outlineOffset: '2px',
          boxShadow: flash ? `0 0 0 4px ${bg}44` : undefined,
          transform: flash ? 'scale(1.04)' : undefined,
        }}
      >
        {label}
        {onStatusChange && (
          <svg
            width="10" height="10" viewBox="0 0 24 24" fill="none"
            className="ml-1 inline-block opacity-60"
          >
            <path d="M6 9l6 6 6-6" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
        )}
      </button>

      {open && (
        <div className="absolute left-0 top-[calc(100%+4px)] z-50 bg-white rounded-lg shadow-2xl border border-[#e6e9ef] overflow-hidden min-w-[160px] animate-scale-in">
          {ALL_STATUSES.map(s => {
            const cfg = STATUS_CONFIG[s];
            return (
              <button
                key={s}
                onClick={() => handleSelect(s)}
                className="w-full flex items-center gap-2 px-3 py-2 hover:bg-[#f5f6f8] transition-colors text-left"
              >
                <span
                  className="w-3 h-3 rounded-sm shrink-0"
                  style={{ background: cfg.bg }}
                />
                <span className="text-xs font-medium text-[#323338]">{cfg.label}</span>
                {s === current && (
                  <svg width="12" height="12" viewBox="0 0 24 24" fill="none" className="ml-auto text-[#0073ea]">
                    <path d="M20 6L9 17l-5-5" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"/>
                  </svg>
                )}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
