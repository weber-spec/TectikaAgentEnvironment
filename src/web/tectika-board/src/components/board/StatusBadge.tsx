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

export function StatusBadge({ status }: { status: AgentTaskStatus }) {
  const { label, bg, color } = STATUS_CONFIG[status];
  return (
    <span
      className="monday-status"
      style={{ background: bg, color }}
    >
      {label}
    </span>
  );
}
