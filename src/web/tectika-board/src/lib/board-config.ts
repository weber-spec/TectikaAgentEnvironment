// Per-board UI config is persisted in localStorage under this key (mirrors board-context.tsx
// `storageKey`). Cloning a board copies the source's views/columns/etc. to the new board so the
// clone opens with the same layout.
function storageKey(boardId: string): string { return `tectika:board:${boardId}`; }

/** Copy the source board's saved UI config to the destination board key. No-op if none saved.
 * `store` defaults to window.localStorage; injectable for tests. */
export function cloneBoardConfig(srcBoardId: string, dstBoardId: string, store?: Storage): void {
  const ls = store ?? (typeof window !== 'undefined' ? window.localStorage : undefined);
  if (!ls) return;
  try {
    const raw = ls.getItem(storageKey(srcBoardId));
    if (raw) ls.setItem(storageKey(dstBoardId), raw);
  } catch { /* quota / unavailable — non-fatal */ }
}
