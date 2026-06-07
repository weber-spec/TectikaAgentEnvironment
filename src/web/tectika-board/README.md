# AgentBoard — Tectika

A Monday.com-inspired, enterprise-grade front-end for orchestrating compound AI-agent
systems. Tasks are modular AI workers chained together inside a governed workspace, with
a rich, multi-view board experience layered over the .NET / Cosmos backend.

Built with **Next.js 16** (App Router, Turbopack), **React 19**, **Tailwind CSS v4**.

## Feature overview

### Board views (switchable per board, saved as views)
- **Table** — grouped sections with colored headers, inline cell editing, per-column
  group summaries (sum / avg / min / max / median / count / distribution battery),
  column resize / hide / reorder / add, row selection + batch actions.
- **Kanban** — drag cards between columns to change status (or any group-by field).
- **Timeline / Gantt** — date bars with a today marker, zoom, and **dependency arrows**
  that visualize the agent pipeline order.
- **Calendar** — month grid by due date with an "unscheduled" rail.
- **Cards** — gallery view.
- **Chart** — column / bar / pie / donut / line, grouped by any field, count/sum/avg.
- **Flow Canvas** — n8n-style node editor (@xyflow) for wiring agent input→output ports,
  the "Lego-style" pipeline from the PRD.

### Columns (Monday-style)
20+ column kinds: status, priority, people, date, timeline, numbers, text, tags,
dropdown, progress/battery, rating, checkbox, link, dependency, tokens, cost, trigger,
created/last-updated, item id, auto-number, and **formula** (with a safe expression
evaluator). Custom columns persist client-side.

### Productivity
- Toolbar: search, quick person filter, **advanced AND/OR filter builder**, multi-sort,
  group-by, show/hide columns.
- **Command palette** (⌘K / Ctrl-K), toasts, skeleton loaders, empty states, dark mode,
  and English / Hebrew (RTL) localization.

### Agent workspace (item panel)
Dual-pane slide-over: left = execution thread (updates + @mentions), activity log, and
agent/run configuration; right = the **evolving artifact canvas** with version history
and in-place human edits.

### Other surfaces
- **Boards gallery** with per-board completion battery.
- **Agents** — manage reusable agent roles (prompt, model, tools, permissions).
- **Approvals** — human-in-the-loop inbox (approve / reject).
- **Dashboards** & **Analytics** — cross-board KPIs, charts, workload, agent leaderboard.
- **Automations** — "When X, then Y" recipe builder with templates.

## Getting started

The front-end talks to the .NET API (`src/api`). For local development the API ships a
toggleable **in-memory mock database** (no Azure needed) — set `"MockDatabase": { "Enabled": true }`
in `src/api/.../appsettings.json` (the default), then:

```bash
# 1. backend (serves seeded mock data on http://localhost:5000)
cd src/api/TectikaAgents.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --urls http://localhost:5000

# 2. front-end
cd src/web/tectika-board
npm install
npm run dev          # Turbopack dev server on http://localhost:3000
# npm run dev:webpack to use webpack instead
```

Point the front-end at a different API with `NEXT_PUBLIC_API_URL`.

## QA

Playwright smoke + interaction suites live in `qa/`:

```bash
npm run build && npm run start          # serve the production build on :3000
node qa/qa.mjs                          # screenshots across every route/view
node qa/qa2.mjs                          # interactions: status change & add-item
                                         # (verified persisted via the API), dependency
                                         # edges, dark mode, RTL
```

The scripts launch Chrome via `executablePath` (`/root/chrome/chrome-linux64/chrome` in
the dev container); adjust the path for your environment. Screenshots are written to
`qa/shots/` (gitignored).
