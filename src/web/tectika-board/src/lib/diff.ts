export type DiffLineType = 'add' | 'del' | 'context' | 'hunk';
export interface DiffLine { type: DiffLineType; text: string; }

/** Parse a unified-diff patch (GitHub's per-file `patch`) into typed lines.
 * Null/empty patch -> []. The leading +/-/space marker is stripped from `text`;
 * hunk headers (@@) are kept verbatim. */
export function parseUnifiedDiff(patch: string | null | undefined): DiffLine[] {
  if (!patch) return [];
  return patch.split('\n').map(line => {
    if (line.startsWith('@@')) return { type: 'hunk' as const, text: line };
    if (line.startsWith('+')) return { type: 'add' as const, text: line.slice(1) };
    if (line.startsWith('-')) return { type: 'del' as const, text: line.slice(1) };
    return { type: 'context' as const, text: line.startsWith(' ') ? line.slice(1) : line };
  });
}
