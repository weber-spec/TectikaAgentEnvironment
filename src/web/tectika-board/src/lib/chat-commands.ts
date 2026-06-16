import { api } from './api';
import type { IconName } from '@/components/ui/icons';

/** Everything a command needs from the chat to do its job. */
export interface ChatCommandContext {
  boardId: string;
  taskId: string;
  isRunning: boolean;              // a run is active
  lastUserText?: string;           // last human message (for /retry)
  refreshTask: () => void;         // re-fetch the task (pick up chatClearedAt / thread reset)
  resend: (text: string) => void;  // re-send a chat message
  openHelp: () => void;            // show the /help list
  toast: (msg: string, kind?: 'success' | 'error') => void;
}

export interface ChatCommand {
  name: string;                    // without the slash, e.g. "clear"
  description: string;
  icon: IconName;
  enabled: (ctx: ChatCommandContext) => boolean;
  run: (ctx: ChatCommandContext) => void | Promise<void>;
}

export const chatCommands: ChatCommand[] = [
  {
    name: 'clear', description: 'Clear the conversation — fresh context for the agent',
    icon: 'refresh', enabled: () => true,
    run: async (c) => {
      try { await api.tasks.clear(c.boardId, c.taskId); c.refreshTask(); c.toast('Conversation cleared'); }
      catch { c.toast('Could not clear the conversation', 'error'); }
    },
  },
  {
    name: 'compact', description: 'Summarize, then start fresh — keeps the gist, drops the bulk',
    icon: 'edit', enabled: () => true,
    run: async (c) => {
      try {
        const r = await api.tasks.compact(c.boardId, c.taskId); c.refreshTask();
        c.toast(r.summarized ? 'Conversation compacted' : 'Couldn’t summarize — cleared instead');
      } catch { c.toast('Could not compact the conversation', 'error'); }
    },
  },
  {
    name: 'stop', description: 'Stop the agent (cancel the current run)',
    icon: 'x', enabled: (c) => c.isRunning,
    run: async (c) => {
      try { await api.tasks.stop(c.boardId, c.taskId); c.refreshTask(); c.toast('Run stopped'); }
      catch { c.toast('Could not stop the run', 'error'); }
    },
  },
  {
    name: 'retry', description: 'Re-send your last message',
    icon: 'refresh', enabled: (c) => !c.isRunning && !!c.lastUserText,
    run: (c) => { if (c.lastUserText) c.resend(c.lastUserText); },
  },
  {
    name: 'help', description: 'Show available commands',
    icon: 'robot', enabled: () => true,
    run: (c) => c.openHelp(),
  },
];

/** Fuzzy-filter the registry by the text typed after "/". */
import { fuzzyScore } from './format';
export function filterCommands(query: string): ChatCommand[] {
  if (!query) return chatCommands;
  return chatCommands
    .map(c => ({ c, s: Math.max(fuzzyScore(c.name, query), fuzzyScore(c.description, query)) }))
    .filter(x => x.s > 0)
    .sort((a, b) => b.s - a.s)
    .map(x => x.c);
}
