import type { AgentTaskStatus } from '@/lib/types';

const STATUS_CONFIG: Record<AgentTaskStatus, { label: string; className: string }> = {
  Backlog:          { label: 'Backlog',             className: 'bg-[#232a3b] text-[#8892aa] border border-[#2d3651]' },
  InProgress:       { label: '⚡ Working',          className: 'bg-indigo-500/15 text-indigo-300 border border-indigo-500/30 animate-pulse' },
  AwaitingApproval: { label: '⏳ Awaiting Approval', className: 'bg-amber-500/15 text-amber-300 border border-amber-500/30' },
  Blocked:          { label: '🔴 Blocked',          className: 'bg-red-500/15 text-red-400 border border-red-500/30' },
  Review:           { label: '👁 Review',           className: 'bg-cyan-500/15 text-cyan-300 border border-cyan-500/30' },
  Done:             { label: '✓ Done',              className: 'bg-emerald-500/15 text-emerald-300 border border-emerald-500/30' },
  Failed:           { label: '✗ Failed',            className: 'bg-red-600/20 text-red-400 border border-red-600/30' },
};

export function StatusBadge({ status }: { status: AgentTaskStatus }) {
  const { label, className } = STATUS_CONFIG[status];
  return (
    <span className={`inline-flex items-center text-xs font-medium px-2 py-0.5 rounded-full whitespace-nowrap ${className}`}>
      {label}
    </span>
  );
}
