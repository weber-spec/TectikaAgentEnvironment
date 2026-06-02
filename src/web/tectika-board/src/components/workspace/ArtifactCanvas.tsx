'use client';

import { useState } from 'react';
import type { Artifact } from '@/lib/types';

interface Props {
  artifact?: Artifact;
  onEdit?: (content: string) => void;
}

export function ArtifactCanvas({ artifact, onEdit }: Props) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(artifact?.content ?? '');

  if (!artifact) {
    return (
      <div className="flex-1 flex items-center justify-center text-gray-400 bg-gray-50 rounded-xl">
        <p className="text-sm">No artifact yet — agent is working...</p>
      </div>
    );
  }

  const originBadge = {
    Agent: '🤖 Agent',
    HumanEdit: '✏️ Human Edit',
    CliBridge: '💻 CLI',
  }[artifact.origin];

  return (
    <div className="flex-1 flex flex-col border rounded-xl overflow-hidden">
      {/* Header */}
      <div className="flex items-center justify-between px-4 py-2 bg-gray-50 border-b">
        <div className="flex items-center gap-2">
          <span className="text-xs text-gray-500">Artifact v{artifact.version}</span>
          <span className="text-xs bg-gray-200 px-2 py-0.5 rounded">{artifact.contentType}</span>
          <span className="text-xs text-gray-400">{originBadge}</span>
        </div>
        {onEdit && (
          <button
            className="text-xs px-2 py-1 rounded bg-blue-50 text-blue-600 hover:bg-blue-100"
            onClick={() => {
              if (editing) { onEdit(draft); setEditing(false); }
              else { setDraft(artifact.content); setEditing(true); }
            }}
          >
            {editing ? 'Save' : 'Edit'}
          </button>
        )}
      </div>

      {/* Content */}
      {editing ? (
        <textarea
          className="flex-1 p-4 font-mono text-sm resize-none focus:outline-none"
          value={draft}
          onChange={e => setDraft(e.target.value)}
        />
      ) : (
        <pre className="flex-1 p-4 font-mono text-sm overflow-auto bg-white whitespace-pre-wrap">
          {artifact.content}
        </pre>
      )}

      {/* Internal logs */}
      {artifact.internalLogs.length > 0 && (
        <details className="border-t">
          <summary className="px-4 py-2 text-xs text-gray-400 cursor-pointer hover:text-gray-600">
            Agent reasoning ({artifact.internalLogs.length} steps)
          </summary>
          <div className="px-4 pb-3 space-y-1">
            {artifact.internalLogs.map((log, i) => (
              <p key={i} className="text-xs text-gray-500">{log}</p>
            ))}
          </div>
        </details>
      )}
    </div>
  );
}
