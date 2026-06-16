'use client';

import { useEffect, useRef, useState } from 'react';
import { PHRASE_POOLS, pickPhrase, type PhraseContext } from '@/lib/thinking-phrases';

/**
 * The chat "agent is working" live edge: a presence orb + a shimmering,
 * context-seeded phrase that rotates every ~3s, an always-ticking elapsed timer,
 * and the real cumulative token count. Structurally never looks stuck because the
 * phrase + timer keep producing new information even when no event has arrived.
 */
export function LiveEdge({
  agentName, context, anchorAt, tokens,
}: {
  agentName?: string;
  context: PhraseContext;
  anchorAt?: string;   // ISO timestamp the elapsed timer counts from
  tokens: number;
}) {
  const [phrase, setPhrase] = useState(() => pickPhrase(PHRASE_POOLS[context], null));
  const lastRef = useRef(phrase);
  const [elapsed, setElapsed] = useState(0);

  // Rotate the phrase every 3s; swap immediately when the context changes.
  useEffect(() => {
    const swap = () => {
      const p = pickPhrase(PHRASE_POOLS[context], lastRef.current);
      lastRef.current = p;
      setPhrase(p);
    };
    swap();
    const id = setInterval(swap, 3000);
    return () => clearInterval(id);
  }, [context]);

  // Elapsed timer — smooth and always truthful.
  useEffect(() => {
    const start = anchorAt ? new Date(anchorAt).getTime() : Date.now();
    const tick = () => setElapsed(Math.max(0, Math.floor((Date.now() - start) / 1000)));
    tick();
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  }, [anchorAt]);

  const mm = Math.floor(elapsed / 60);
  const ss = String(elapsed % 60).padStart(2, '0');

  return (
    <div className="flex items-center gap-2.5 px-3 py-2.5 live-edge"
      role="status" aria-live="polite" aria-label={`${agentName ?? 'Agent'} is working`}>
      <span className="live-orb" aria-hidden />
      <div className="flex-1 min-w-0">
        {/* key={phrase} remounts the span so the fade-in replays on each swap */}
        <span key={phrase} className="live-phrase" aria-hidden>{phrase}</span>
        <div className="flex gap-2.5 mt-0.5 text-[10.5px] font-mono text-[var(--muted-2)]">
          <span>{mm}:{ss}</span>
          {tokens > 0 && <span className="text-[var(--muted)]">{tokens.toLocaleString()} tokens</span>}
        </div>
      </div>
    </div>
  );
}
