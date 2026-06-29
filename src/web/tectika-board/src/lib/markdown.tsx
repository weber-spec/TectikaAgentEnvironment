import React from 'react';

// minimal markdown: headings, lists, code fences, bold
export function Markdown({ text }: { text: string }) {
  const lines = text.split('\n');
  const out: React.ReactNode[] = [];
  let code: string[] | null = null;
  lines.forEach((ln, i) => {
    if (ln.startsWith('```')) { if (code) { out.push(<pre key={i} className="font-mono text-[12px] bg-[var(--background)] border border-[var(--border)] rounded p-2 my-2 overflow-auto">{code.join('\n')}</pre>); code = null; } else code = []; return; }
    if (code) { code.push(ln); return; }
    if (ln.startsWith('### ')) out.push(<h4 key={i} className="font-semibold text-[var(--foreground)] mt-2">{ln.slice(4)}</h4>);
    else if (ln.startsWith('## ')) out.push(<h3 key={i} className="font-bold text-[var(--foreground)] text-base mt-3">{ln.slice(3)}</h3>);
    else if (ln.startsWith('# ')) out.push(<h2 key={i} className="font-bold text-[var(--foreground)] text-lg mt-3">{ln.slice(2)}</h2>);
    else if (ln.startsWith('- ')) out.push(<li key={i} className="text-[13px] text-[var(--foreground)] ml-4 list-disc">{inlineBold(ln.slice(2))}</li>);
    else if (ln.trim() === '') out.push(<div key={i} className="h-2" />);
    else out.push(<p key={i} className="text-[13px] text-[var(--foreground)]">{inlineBold(ln)}</p>);
  });
  if (code) out.push(<pre key="last" className="font-mono text-[12px] bg-[var(--background)] border border-[var(--border)] rounded p-2 my-2 overflow-auto">{(code as string[]).join('\n')}</pre>);
  return <div>{out}</div>;
}

function inlineBold(s: string) {
  return s.split(/(\*\*[^*]+\*\*|`[^`]+`)/g).map((p, i) => p.startsWith('**') ? <b key={i}>{p.slice(2, -2)}</b> : p.startsWith('`') ? <code key={i} className="font-mono bg-[var(--surface)] rounded px-1 text-[12px]">{p.slice(1, -1)}</code> : <span key={i}>{p}</span>);
}
