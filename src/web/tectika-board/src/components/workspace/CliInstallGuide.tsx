'use client';

import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { Icon } from '@/components/ui/icons';
import { toast } from '@/lib/toast';

type OS = 'mac' | 'linux' | 'windows';

function detectOS(): OS {
  if (typeof navigator === 'undefined') return 'mac';
  const ua = navigator.userAgent.toLowerCase();
  if (ua.includes('win')) return 'windows';
  if (ua.includes('linux') && !ua.includes('android')) return 'linux';
  return 'mac';
}

const INSTALL: Record<OS, { label: string; primary: { note: string; cmd: string }; alt?: { note: string; cmd: string } }> = {
  mac: {
    label: 'macOS',
    primary: { note: 'Homebrew', cmd: 'brew install tectika/tap/agentboard' },
    alt: { note: 'or with npm', cmd: 'npm install -g @tectika/agentboard-cli' },
  },
  linux: {
    label: 'Linux',
    primary: { note: 'Install script', cmd: 'curl -fsSL https://get.tectika.com/cli | sh' },
    alt: { note: 'or with npm', cmd: 'npm install -g @tectika/agentboard-cli' },
  },
  windows: {
    label: 'Windows',
    primary: { note: 'winget', cmd: 'winget install Tectika.AgentBoard' },
    alt: { note: 'or PowerShell', cmd: 'iwr https://get.tectika.com/cli.ps1 | iex' },
  },
};

export function CliInstallGuide({ open, onClose, taskId, runId }: { open: boolean; onClose: () => void; taskId: string; runId: string }) {
  const [os, setOs] = useState<OS>('mac');
  useEffect(() => { if (open) setOs(detectOS()); }, [open]);
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  if (!open) return null;
  const cfg = INSTALL[os];

  return createPortal(
    <div className="fixed inset-0 z-[1400] flex items-center justify-center p-4" style={{ background: 'rgba(0,0,0,0.45)' }} onMouseDown={onClose}>
      <div className="bg-[var(--background)] rounded-2xl shadow-2xl w-full max-w-[560px] max-h-[88vh] flex flex-col overflow-hidden" onMouseDown={e => e.stopPropagation()}>
        {/* header */}
        <div className="flex items-center gap-2.5 px-5 py-4 border-b border-[var(--border)]">
          <span className="w-8 h-8 rounded-lg flex items-center justify-center text-white shrink-0" style={{ background: 'linear-gradient(135deg, #0073ea, #00c875)' }}><Icon.flow size={17} /></span>
          <div>
            <h2 className="text-base font-bold text-[var(--foreground)] leading-tight">Install the AgentBoard CLI</h2>
            <p className="text-xs text-[var(--muted)]">Stream a local agent (Claude Code, Cursor, any terminal loop) into a task.</p>
          </div>
          <button onClick={onClose} aria-label="Close" className="ml-auto w-8 h-8 flex items-center justify-center rounded-md text-[var(--muted)] hover:bg-[var(--surface)] shrink-0"><Icon.x size={18} /></button>
        </div>

        <div className="flex-1 overflow-auto px-5 py-4 flex flex-col gap-5">
          {/* 1 · install */}
          <Step n={1} title="Install">
            <div className="flex gap-1 mb-2">
              {(Object.keys(INSTALL) as OS[]).map(k => (
                <button key={k} onClick={() => setOs(k)}
                  className={`px-3 py-1.5 rounded-lg text-[13px] font-medium border transition-colors ${os === k ? 'border-[var(--primary)] bg-[var(--primary-light)] text-[var(--primary)]' : 'border-[var(--border)] text-[var(--muted)] hover:text-[var(--foreground)]'}`}>
                  {INSTALL[k].label}
                </button>
              ))}
            </div>
            <CmdLabel text={cfg.primary.note} />
            <Cmd cmd={cfg.primary.cmd} />
            {cfg.alt && <><CmdLabel text={cfg.alt.note} /><Cmd cmd={cfg.alt.cmd} /></>}
          </Step>

          {/* 2 · authenticate */}
          <Step n={2} title="Authenticate">
            <p className="text-xs text-[var(--muted)] mb-2">Sign in with your Entra ID (opens a browser). For a self-hosted workspace, point the CLI at your API first.</p>
            <Cmd cmd="agentboard login" />
            <CmdLabel text="self-hosted only" />
            <Cmd cmd="agentboard config set api https://your-agentboard.example.com" />
          </Step>

          {/* 3 · link */}
          <Step n={3} title="Link this task & start streaming">
            <p className="text-xs text-[var(--muted)] mb-2">Run this in your project directory. Your stdout, git diffs and artifacts then stream into this task, its board row and the canvas — live.</p>
            <Cmd cmd={`agentboard link --task-id ${taskId} --run-id ${runId}`} />
            <div className="mt-2 flex flex-wrap gap-1.5">
              {['Claude Code', 'Cursor', 'Aider', 'plain shell'].map(t => (
                <span key={t} className="text-[11px] px-2 py-0.5 rounded-full bg-[var(--surface)] text-[var(--muted)] border border-[var(--border)]">{t}</span>
              ))}
            </div>
          </Step>
        </div>

        {/* footer links */}
        <div className="shrink-0 border-t border-[var(--border)] px-5 py-3 flex items-center gap-4 text-[12px]">
          <a href="https://docs.tectika.com/cli" target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 text-[var(--primary)] hover:underline"><Icon.file size={13} /> Full documentation</a>
          <a href="https://github.com/tectika/agentboard-cli/releases" target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 text-[var(--primary)] hover:underline"><Icon.duplicate size={13} /> Releases</a>
          <span className="ml-auto text-[var(--muted-2)] text-[11px]">requires Node 18+ or a native build</span>
        </div>
      </div>
    </div>,
    document.body,
  );
}

function Step({ n, title, children }: { n: number; title: string; children: React.ReactNode }) {
  return (
    <div className="flex gap-3">
      <span className="w-6 h-6 rounded-full bg-[var(--primary)] text-white text-xs font-bold flex items-center justify-center shrink-0 mt-0.5">{n}</span>
      <div className="flex-1 min-w-0">
        <h3 className="text-sm font-semibold text-[var(--foreground)] mb-1.5">{title}</h3>
        {children}
      </div>
    </div>
  );
}
function CmdLabel({ text }: { text: string }) {
  return <div className="text-[10px] uppercase tracking-wide text-[var(--muted-2)] font-semibold mt-2 mb-1">{text}</div>;
}
function Cmd({ cmd }: { cmd: string }) {
  const copy = () => navigator.clipboard?.writeText(cmd).then(() => toast('Copied', 'success')).catch(() => {});
  return (
    <div className="group/cmd flex items-center gap-2 rounded-lg px-3 py-2" style={{ background: '#0f1117' }}>
      <span className="text-[#42d392] font-mono text-[12px] shrink-0">$</span>
      <code className="flex-1 font-mono text-[12px] text-white/90 overflow-x-auto whitespace-nowrap">{cmd}</code>
      <button onClick={copy} title="Copy" className="shrink-0 text-white/40 hover:text-white transition-colors"><Icon.duplicate size={14} /></button>
    </div>
  );
}
