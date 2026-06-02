'use client';

import { useEffect, useRef, useState } from 'react';
import { api } from '@/lib/api';
import type { AgentEvent, AgentTask, AgentRole } from '@/lib/types';

interface Props {
  task: AgentTask;
  role?: AgentRole;
  runId?: string;
  onArtifactUpdate?: (artifactId: string) => void;
}

export function ExecutionThread({ task, role, runId, onArtifactUpdate }: Props) {
  const [events, setEvents] = useState<AgentEvent[]>([]);
  const [cliConnected, setCliConnected] = useState(false);
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!runId) return;
    const unsubscribe = api.streamRun(runId, (event) => {
      setEvents(prev => [...prev, event]);
      if (event.type === 'artifact_updated' && event.artifactId)
        onArtifactUpdate?.(event.artifactId);
      if (event.type === 'cli_connected') setCliConnected(true);
      if (event.type === 'cli_disconnected') setCliConnected(false);
    });
    return unsubscribe;
  }, [runId, onArtifactUpdate]);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [events]);

  return (
    <div className="flex flex-col h-full">
      {/* Agent info */}
      <div className="px-4 py-3 border-b bg-gray-50">
        <p className="font-semibold text-sm">{role?.displayName ?? task.assignee.id}</p>
        <p className="text-xs text-gray-500 mt-0.5 line-clamp-2">{role?.systemPrompt}</p>
        {cliConnected && (
          <span className="inline-flex items-center gap-1 mt-1 text-xs text-green-600 bg-green-50 px-2 py-0.5 rounded-full">
            <span className="w-1.5 h-1.5 bg-green-500 rounded-full animate-pulse" />
            CLI Connected
          </span>
        )}
      </div>

      {/* Event stream */}
      <div className="flex-1 overflow-auto p-4 space-y-2 font-mono text-xs">
        {events.length === 0 && (
          <p className="text-gray-400 text-center py-8">Waiting for agent to start...</p>
        )}
        {events.map((event, i) => (
          <EventLine key={i} event={event} />
        ))}
        <div ref={bottomRef} />
      </div>
    </div>
  );
}

function EventLine({ event }: { event: AgentEvent }) {
  const icons: Record<string, string> = {
    run_started: '▶',
    step_started: '→',
    agent_thinking: '💭',
    tool_call: '🔧',
    artifact_updated: '📄',
    approval_required: '⏸',
    step_completed: '✓',
    run_completed: '✅',
    run_failed: '✗',
    cli_output: '💻',
    cli_connected: '🔌',
    cli_disconnected: '🔌',
  };

  const colorClass = event.type.includes('fail') || event.type.includes('failed')
    ? 'text-red-600'
    : event.type.includes('completed') || event.type.includes('success')
    ? 'text-green-600'
    : event.type === 'approval_required'
    ? 'text-yellow-600'
    : 'text-gray-700';

  return (
    <div className={`flex gap-2 ${colorClass}`}>
      <span className="opacity-60">{icons[event.type] ?? '·'}</span>
      <span className="opacity-50 shrink-0">
        {new Date(event.timestamp).toLocaleTimeString()}
      </span>
      <span>{event.content ?? event.type}</span>
    </div>
  );
}
