'use client';

import { useState, useEffect } from 'react';
import { Modal } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';
import { api, ApiError } from '@/lib/api';
import type { Board, Connection } from '@/lib/types';
import { toast } from '@/lib/toast';

// Inline GitHub Octocat mark (monochrome SVG)
function GitHubIcon({ size = 20 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 98 96" aria-hidden="true" fill="currentColor">
      <path fillRule="evenodd" clipRule="evenodd"
        d="M48.854 0C21.839 0 0 22 0 49.217c0 21.756 13.993 40.172 33.405 46.69 2.427.49
           3.316-1.059 3.316-2.362 0-1.141-.08-5.052-.08-9.127-13.59 2.934-16.42-5.867-16.42-5.867-2.184-5.704-5.42-7.17-5.42-7.17-4.448-3.015.324-3.015.324-3.015
           4.934.326 7.523 5.052 7.523 5.052 4.367 7.496 11.404 5.378 14.235 4.074.404-3.178
           1.699-5.378 3.074-6.6-10.839-1.141-22.243-5.378-22.243-24.283 0-5.378 1.94-9.778
           5.014-13.2-.485-1.222-2.184-6.275.486-13.038 0 0 4.125-1.304 13.426 5.052a46.97
           46.97 0 0 1 12.214-1.63c4.125 0 8.33.571 12.213 1.63 9.302-6.356 13.427-5.052
           13.427-5.052 2.67 6.763.97 11.816.485 13.038 3.155 3.422 5.015 7.822 5.015
           13.2 0 18.905-11.404 23.06-22.324 24.283 1.78 1.548 3.316 4.481 3.316 9.126
           0 6.6-.08 11.897-.08 13.526 0 1.304.89 2.853 3.316 2.364 19.412-6.52 33.405-24.935
           33.405-46.691C97.707 22 75.788 0 48.854 0z" />
    </svg>
  );
}

function extractConnectError(err: ApiError): string {
  const brace = err.message.indexOf('{');
  if (brace >= 0) {
    try {
      const body = JSON.parse(err.message.slice(brace));
      if (body?.error) return String(body.error);
    } catch { /* not JSON — fall through */ }
  }
  return 'This repository is already connected to another board.';
}

interface Props {
  board: Board;
  onClose: () => void;
  onUpdated: (board: Board) => void;
}

export function GitHubConnectModal({ board, onClose, onUpdated }: Props) {
  const connected = board.github;
  const [repoUrl, setRepoUrl] = useState(connected?.repoUrl ?? '');
  const [ghConns, setGhConns] = useState<Connection[]>([]);
  const [connectionId, setConnectionId] = useState('');
  const [saving, setSaving] = useState(false);
  const [disconnecting, setDisconnecting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.connections.list()
      .then(cs => {
        const gh = cs.filter(c => c.catalogId === 'github');
        setGhConns(gh);
        if (gh.length === 1) setConnectionId(gh[0].id);   // default to the only one
      })
      .catch(() => setGhConns([]));
  }, []);

  const handleConnect = async () => {
    if (!repoUrl.trim() || !connectionId) return;
    setSaving(true);
    setError(null);
    try {
      const updated = await api.boards.connectGitHub(board.id, connectionId, repoUrl.trim());
      onUpdated(updated);
      toast('GitHub repo connected', 'success');
      onClose();
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setError(extractConnectError(err));
      } else {
        toast('Could not connect repo', 'error');
      }
    } finally {
      setSaving(false);
    }
  };

  const handleDisconnect = async () => {
    setDisconnecting(true);
    try {
      const updated = await api.boards.disconnectGitHub(board.id);
      onUpdated(updated);
      toast('GitHub repo disconnected', 'success');
      onClose();
    } catch {
      toast('Could not disconnect repo', 'error');
    } finally {
      setDisconnecting(false);
    }
  };

  return (
    <Modal
      open
      onClose={onClose}
      width={480}
      title={
        <span className="flex items-center gap-2 text-[var(--foreground)]">
          <GitHubIcon size={18} />
          Connect GitHub Repository
        </span>
      }
      footer={
        <div className="flex items-center justify-between w-full">
          <div>
            {connected && (
              <Button variant="danger" onClick={handleDisconnect} disabled={disconnecting}>
                {disconnecting ? 'Disconnecting…' : 'Disconnect'}
              </Button>
            )}
          </div>
          <div className="flex gap-2">
            <Button onClick={onClose} disabled={saving || disconnecting}>Cancel</Button>
            <Button variant="primary" onClick={handleConnect} disabled={saving || disconnecting || !repoUrl.trim() || !connectionId}>
              {saving ? 'Connecting…' : connected ? 'Update' : 'Connect'}
            </Button>
          </div>
        </div>
      }
    >
      <div className="flex flex-col gap-4">
        {error && (
          <div className="text-sm text-[#e2445c] bg-[#e2445c11] px-3 py-2 rounded-lg">{error}</div>
        )}
        {connected && (
          <div className="flex items-center gap-2 text-sm text-emerald-600 dark:text-emerald-400 bg-emerald-500/10 px-3 py-2 rounded-lg">
            <GitHubIcon size={14} />
            Connected: <span className="font-semibold">{connected.owner}/{connected.repo}</span>
          </div>
        )}

        <label className="block">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">
            Repository URL
          </span>
          <input
            className="inp mt-1 w-full"
            placeholder="https://github.com/org/repo"
            value={repoUrl}
            onChange={e => { setRepoUrl(e.target.value); setError(null); }}
          />
        </label>

        <label className="block">
          <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">
            GitHub connection
          </span>
          {ghConns.length === 0 ? (
            <p className="text-[12px] text-[var(--muted)] mt-1">
              No GitHub connection yet. Create one on the{' '}
              <a href="/connections" className="underline text-[var(--primary)]">Connections</a> page (its token is
              shared across boards), then pick the repo here.
            </p>
          ) : (
            <>
              <select
                className="inp mt-1 w-full"
                value={connectionId}
                onChange={e => { setConnectionId(e.target.value); setError(null); }}
              >
                <option value="">Select a GitHub connection…</option>
                {ghConns.map(c => <option key={c.id} value={c.id}>{c.displayName}</option>)}
              </select>
              <p className="text-[11px] text-[var(--muted)] mt-1">
                The token lives on the connection. Manage tokens on the{' '}
                <a href="/connections" className="underline text-[var(--primary)]">Connections</a> page.
              </p>
            </>
          )}
        </label>
      </div>
    </Modal>
  );
}
