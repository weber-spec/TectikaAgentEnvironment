'use client';

import React, { useState } from 'react';
import { useBoard } from '@/lib/board-context';
import { STATUS_CONFIG } from '@/lib/palette';
import { formatDateShort } from '@/lib/format';

const ROW_H = 40;
const HEADER_H = 36;
const LABEL_W = 240;
const DAY = 86400000;

export function TimelineView() {
  const { groups, openTask, downstreamIds } = useBoard();
  const [pxPerDay, setPxPerDay] = useState(26);
  const [now] = useState(() => Date.now());

  // flatten rows (group header + tasks), track y per task
  const rows: ({ type: 'group'; label: string; hex: string } | { type: 'task'; task: typeof groups[0]['tasks'][0] })[] = [];
  for (const g of groups) {
    if (g.key !== '__all__') rows.push({ type: 'group', label: g.label, hex: g.hex });
    for (const t of g.tasks) rows.push({ type: 'task', task: t });
  }

  const allTasks = groups.flatMap(g => g.tasks);
  const dates = allTasks.flatMap(t => [new Date(t.createdAt).getTime(), t.dueAt ? new Date(t.dueAt).getTime() : new Date(t.createdAt).getTime() + 3 * DAY]);
  const minD = dates.length ? Math.min(...dates) : now;
  const maxD = dates.length ? Math.max(...dates) : now + 14 * DAY;
  const start = new Date(minD - 2 * DAY); start.setHours(0, 0, 0, 0);
  const end = new Date(maxD + 3 * DAY);
  const totalDays = Math.ceil((end.getTime() - start.getTime()) / DAY);
  const width = totalDays * pxPerDay;

  const xFor = (d: number) => ((d - start.getTime()) / DAY) * pxPerDay;

  // y position per task
  const taskY: Record<string, number> = {};
  rows.forEach((r, i) => { if (r.type === 'task') taskY[r.task.id] = i * ROW_H + ROW_H / 2; });

  // month ticks
  const months: { x: number; label: string }[] = [];
  const cur = new Date(start.getFullYear(), start.getMonth(), 1);
  while (cur.getTime() < end.getTime()) {
    months.push({ x: xFor(Math.max(cur.getTime(), start.getTime())), label: cur.toLocaleDateString(undefined, { month: 'short', year: '2-digit' }) });
    cur.setMonth(cur.getMonth() + 1);
  }
  const todayX = xFor(now);

  // dependency arrows — downstreamIds contains only Dependency edges (QaFeedback loops excluded by design)
  const arrows: { x1: number; y1: number; x2: number; y2: number }[] = [];
  for (const t of allTasks) {
    const fromEnd = xFor((t.dueAt ? new Date(t.dueAt) : new Date(new Date(t.createdAt).getTime() + 3 * DAY)).getTime());
    for (const downId of downstreamIds[t.id] ?? []) {
      if (taskY[downId] == null) continue;
      const down = allTasks.find(x => x.id === downId)!;
      const toStart = xFor(new Date(down.createdAt).getTime());
      arrows.push({ x1: fromEnd, y1: taskY[t.id], x2: toStart, y2: taskY[downId] });
    }
  }

  const contentH = rows.length * ROW_H;

  return (
    <div className="h-full flex flex-col">
      <div className="flex items-center gap-2 px-4 py-2 border-b border-[var(--border)]">
        <span className="text-xs text-[var(--muted)]">Zoom</span>
        <button onClick={() => setPxPerDay(p => Math.max(8, p - 6))} className="w-7 h-7 rounded border border-[var(--border)] text-[var(--muted)] hover:bg-[var(--surface)]">−</button>
        <button onClick={() => setPxPerDay(p => Math.min(80, p + 6))} className="w-7 h-7 rounded border border-[var(--border)] text-[var(--muted)] hover:bg-[var(--surface)]">+</button>
        <span className="text-xs text-[var(--muted-2)] ml-2">Dependency arrows show the agent pipeline order</span>
      </div>
      <div className="flex-1 overflow-auto">
        <div className="flex" style={{ minWidth: LABEL_W + width }}>
          {/* labels */}
          <div className="sticky left-0 z-10 bg-[var(--background)] border-r border-[var(--border)]" style={{ width: LABEL_W }}>
            <div style={{ height: HEADER_H }} className="border-b border-[var(--border)]" />
            {rows.map((r, i) => r.type === 'group' ? (
              <div key={i} style={{ height: ROW_H }} className="flex items-center gap-2 px-3 font-semibold text-sm" >
                <span className="w-2 h-2 rounded-full" style={{ background: r.hex }} /><span style={{ color: r.hex }}>{r.label}</span>
              </div>
            ) : (
              <button key={i} onClick={() => openTask(r.task.id)} style={{ height: ROW_H }} className="w-full flex items-center px-3 text-sm text-[var(--foreground)] hover:bg-[var(--surface)] truncate text-left border-b border-[var(--border)]/40">{r.task.title}</button>
            ))}
          </div>
          {/* timeline */}
          <div className="relative" style={{ width }}>
            {/* header */}
            <div className="sticky top-0 z-[5] bg-[var(--surface)] border-b border-[var(--border)]" style={{ height: HEADER_H }}>
              {months.map((m, i) => (
                <div key={i} className="absolute top-0 h-full flex items-center px-2 text-[11px] font-semibold text-[var(--muted)] border-l border-[var(--border)]" style={{ left: m.x }}>{m.label}</div>
              ))}
            </div>
            {/* today line */}
            {todayX >= 0 && todayX <= width && <div className="absolute top-0 z-[2] w-0.5 bg-[#e2445c]/60" style={{ left: todayX, height: HEADER_H + contentH }} title="Today" />}
            {/* arrows */}
            <svg className="absolute pointer-events-none" style={{ top: HEADER_H, left: 0, width, height: contentH, overflow: 'visible' }}>
              <defs><marker id="tl-arrow" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0,0 L6,3 L0,6 Z" fill="#a25ddc" /></marker></defs>
              {arrows.map((a, i) => (
                <path key={i} d={`M ${a.x1} ${a.y1} C ${a.x1 + 24} ${a.y1}, ${a.x2 - 24} ${a.y2}, ${a.x2} ${a.y2}`} fill="none" stroke="#a25ddc" strokeWidth={1.5} markerEnd="url(#tl-arrow)" opacity={0.7} />
              ))}
            </svg>
            {/* bars */}
            <div className="relative" style={{ height: contentH }}>
              {rows.map((r, i) => {
                if (r.type === 'group') return <div key={i} style={{ height: ROW_H, top: i * ROW_H }} className="absolute w-full" />;
                const t = r.task;
                const x = xFor(new Date(t.createdAt).getTime());
                const x2 = xFor((t.dueAt ? new Date(t.dueAt) : new Date(new Date(t.createdAt).getTime() + 3 * DAY)).getTime());
                const st = STATUS_CONFIG[t.status];
                return (
                  <div key={i} className="absolute" style={{ top: i * ROW_H + 8, left: x, width: Math.max(20, x2 - x), height: ROW_H - 16 }}>
                    <button onClick={() => openTask(t.id)} title={`${t.title} · ${formatDateShort(t.createdAt)} – ${t.dueAt ? formatDateShort(t.dueAt) : '?'}`}
                      className="h-full w-full rounded-md flex items-center px-2 text-[11px] font-medium text-white truncate hover:brightness-110 shadow-sm" style={{ background: st.hex }}>
                      {t.title}
                    </button>
                  </div>
                );
              })}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
