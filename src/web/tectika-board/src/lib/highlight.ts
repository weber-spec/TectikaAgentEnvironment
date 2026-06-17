// Syntax highlighting for the code viewer. languageForPath is pure (unit-tested);
// highlightToHtml lazy-loads Shiki on the client.

const EXT_TO_LANG: Record<string, string> = {
  ts: 'typescript', tsx: 'tsx', js: 'javascript', jsx: 'jsx', mjs: 'javascript', cjs: 'javascript',
  json: 'json', css: 'css', scss: 'scss', html: 'html', md: 'markdown', mdx: 'markdown',
  py: 'python', cs: 'csharp', go: 'go', rs: 'rust', java: 'java', rb: 'ruby', php: 'php',
  c: 'c', h: 'c', cpp: 'cpp', hpp: 'cpp', sh: 'bash', bash: 'bash', yml: 'yaml', yaml: 'yaml',
  toml: 'toml', xml: 'xml', sql: 'sql', kt: 'kotlin', swift: 'swift', dockerfile: 'docker',
};

export function languageForPath(path: string): string {
  const name = path.split('/').pop() ?? '';
  if (name.toLowerCase() === 'dockerfile') return 'docker';
  const dot = name.lastIndexOf('.');
  if (dot <= 0) return 'text';
  const ext = name.slice(dot + 1).toLowerCase();
  return EXT_TO_LANG[ext] ?? 'text';
}

/** Highlight code to HTML using Shiki. Returns null on failure (caller shows a plain <pre>). */
export async function highlightToHtml(code: string, lang: string): Promise<string | null> {
  try {
    const { codeToHtml } = await import('shiki');
    return await codeToHtml(code, { lang: lang === 'text' ? 'text' : lang, theme: 'github-dark' });
  } catch {
    return null;
  }
}
