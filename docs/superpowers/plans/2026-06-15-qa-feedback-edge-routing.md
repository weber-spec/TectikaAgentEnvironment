# QA Feedback Edge Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render QA feedback edges (`kind === 'QaFeedback'`) as a downward arc whose depth adapts to clear nodes sitting between the two endpoints, instead of a straight bezier that tangles with the forward-edge lane.

**Architecture:** Extract a pure geometry function `getFeedbackPath` into its own module, unit-tested with Node's built-in test runner (zero new dependencies). The custom edge component `PipelineEdge` reads live node bounding boxes from the React Flow store and calls `getFeedbackPath` for feedback edges only; forward edges keep `getBezierPath`.

**Tech Stack:** TypeScript, React, `@xyflow/react` v12.11, Next.js (dev/build), Node 22 `node --test` (TS via native type-stripping), Playwright for visual verification.

**Spec:** `docs/superpowers/specs/2026-06-15-qa-feedback-edge-routing-design.md`

**Branch note:** The repo is on the default branch `main`. Before Task 1, create a feature branch:
`git checkout -b feat/qa-feedback-edge-routing`. All commits below land on that branch.

**Working directory:** All paths are relative to `src/web/tectika-board/`. Run all `npm`/`node` commands from there.

---

## File Structure

- **Create** `src/components/board/canvas/feedbackEdgePath.ts` — pure geometry: obstacle selection + arc path string + label anchor. No React / no `@xyflow` imports, so it runs under `node --test`.
- **Create** `src/components/board/canvas/feedbackEdgePath.test.ts` — unit tests for the helper.
- **Modify** `src/components/board/canvas/CanvasView.tsx` — `PipelineEdge` calls the helper for feedback edges; add `useStore` import and node-box reading.
- **Modify** `package.json` — add a `test` script.
- **Modify** `tsconfig.json` — exclude `**/*.test.ts` (test files import with a `.ts` extension, which `moduleResolution: "bundler"` rejects under `tsc`/`next build`).
- **Modify** `eslint.config.mjs` — ignore `**/*.test.ts` (they are Node test files, not app code).

---

## Task 1: Test harness setup (no new dependencies)

**Files:**
- Modify: `package.json`
- Modify: `tsconfig.json:33`
- Modify: `eslint.config.mjs`

- [ ] **Step 1: Add the `test` script to `package.json`**

In the `"scripts"` block, add a `test` entry alongside the existing scripts:

```json
"scripts": {
  "dev": "next dev",
  "dev:webpack": "next dev --webpack",
  "build": "next build",
  "start": "next start",
  "lint": "eslint",
  "test": "node --test"
}
```

- [ ] **Step 2: Exclude test files from TypeScript**

In `tsconfig.json`, change the `exclude` array (currently `["node_modules"]`) to:

```json
"exclude": ["node_modules", "**/*.test.ts"]
```

- [ ] **Step 3: Ignore test files in ESLint**

In `eslint.config.mjs`, add `"**/*.test.ts"` to the `globalIgnores([...])` list:

```js
  globalIgnores([
    // Default ignores of eslint-config-next:
    ".next/**",
    "out/**",
    "build/**",
    "next-env.d.ts",
    // Node test files (run via `npm test`, not part of the app build):
    "**/*.test.ts",
  ]),
```

- [ ] **Step 4: Verify the test runner works with no tests yet**

Run: `npm test`
Expected: exits 0 with `# tests 0` (no test files found yet). It must NOT error on flags or TS.

- [ ] **Step 5: Commit**

```bash
git add package.json tsconfig.json eslint.config.mjs
git commit -m "chore(web): add node --test runner + exclude test files from build/lint"
```

---

## Task 2: `getFeedbackPath` pure geometry helper (TDD)

**Files:**
- Create: `src/components/board/canvas/feedbackEdgePath.ts`
- Test: `src/components/board/canvas/feedbackEdgePath.test.ts`

- [ ] **Step 1: Write the failing tests**

Create `src/components/board/canvas/feedbackEdgePath.test.ts`:

```ts
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { getFeedbackPath, type NodeBox } from './feedbackEdgePath.ts';

// Source (later node) on the right, target (earlier node) on the left, level.
const base = { sourceX: 300, sourceY: 100, targetX: 0, targetY: 100, sourceId: 'S', targetId: 'T' };

test('returns a single cubic that starts at the source and dips downward', () => {
  const [path, labelX, labelY] = getFeedbackPath({ ...base, nodeBoxes: [] });
  assert.ok(path.startsWith('M300,100'), `path should start at source: ${path}`);
  assert.match(path, /C/); // cubic bezier
  assert.equal(labelX, 150); // midpoint of the two endpoints
  assert.ok(labelY > 100, 'label/arc dips below the handle lane');
});

test('dip clears an obstacle node sitting horizontally between the endpoints', () => {
  const obstacle: NodeBox = { id: 'M', x: 120, y: 100, width: 110, height: 200 }; // bottom = 300
  const [, , labelY] = getFeedbackPath({ ...base, nodeBoxes: [obstacle] });
  assert.ok(labelY >= 300, `expected dip below obstacle bottom 300, got ${labelY}`);
});

test('ignores nodes outside the horizontal span between the endpoints', () => {
  const far: NodeBox = { id: 'F', x: 500, y: 100, width: 110, height: 400 }; // right of x-range [0,300]
  const [, , withFar] = getFeedbackPath({ ...base, nodeBoxes: [far] });
  const [, , without] = getFeedbackPath({ ...base, nodeBoxes: [] });
  assert.equal(withFar, without);
});

test('excludes the source and target nodes from the dip calculation', () => {
  const tallTarget: NodeBox = { id: 'T', x: 0, y: 100, width: 110, height: 400 };
  const [, , withTarget] = getFeedbackPath({ ...base, nodeBoxes: [tallTarget] });
  const [, , without] = getFeedbackPath({ ...base, nodeBoxes: [] });
  assert.equal(withTarget, without);
});

test('a deeper obstacle produces a deeper dip', () => {
  const shallow: NodeBox = { id: 'M', x: 120, y: 100, width: 50, height: 60 };
  const deep: NodeBox = { id: 'M', x: 120, y: 100, width: 50, height: 260 };
  const [, , yShallow] = getFeedbackPath({ ...base, nodeBoxes: [shallow] });
  const [, , yDeep] = getFeedbackPath({ ...base, nodeBoxes: [deep] });
  assert.ok(yDeep > yShallow, `deep ${yDeep} should exceed shallow ${yShallow}`);
});

test('works when endpoints are at different vertical levels', () => {
  const [path, , labelY] = getFeedbackPath({ ...base, targetY: 250, nodeBoxes: [] });
  assert.ok(path.startsWith('M300,100'));
  assert.ok(labelY > 250, 'dips below the lower of the two endpoints');
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `npm test`
Expected: FAIL — cannot resolve `./feedbackEdgePath.ts` (module does not exist yet).

- [ ] **Step 3: Implement the helper**

Create `src/components/board/canvas/feedbackEdgePath.ts`:

```ts
/** Axis-aligned bounding box for a canvas node, in flow (absolute) coordinates. */
export interface NodeBox {
  id: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface FeedbackPathParams {
  sourceX: number;
  sourceY: number;
  targetX: number;
  targetY: number;
  sourceId: string;
  targetId: string;
  /** All canvas node boxes; the source and target are skipped internally. */
  nodeBoxes: NodeBox[];
}

/** Vertical clearance kept below whatever the arc must pass under. */
const MARGIN = 40;
/** Horizontal "shoulder" of the curve as it leaves/enters the handles. */
const MIN_SHOULDER = 40;
const MAX_SHOULDER = 200;

/**
 * Route a QA feedback (back-)edge as a downward arc that bows below any nodes
 * sitting horizontally between the two endpoints. Returns the same
 * `[svgPath, labelX, labelY]` tuple shape as React Flow's `getBezierPath`,
 * so the edge label/toolbar renderer is reused unchanged.
 */
export function getFeedbackPath(p: FeedbackPathParams): [string, number, number] {
  const { sourceX, sourceY, targetX, targetY, sourceId, targetId, nodeBoxes } = p;

  const xLo = Math.min(sourceX, targetX);
  const xHi = Math.max(sourceX, targetX);

  // Lowest point (largest y) of any node whose x-span overlaps the edge's x-span,
  // excluding the edge's own endpoints.
  let obstacleBottom = -Infinity;
  for (const n of nodeBoxes) {
    if (n.id === sourceId || n.id === targetId) continue;
    const left = n.x;
    const right = n.x + n.width;
    if (right < xLo || left > xHi) continue; // no horizontal overlap
    obstacleBottom = Math.max(obstacleBottom, n.y + n.height);
  }

  const baseY = Math.max(sourceY, targetY);
  const floor = Math.max(baseY, obstacleBottom); // -Infinity drops out via Math.max
  const clearY = floor + MARGIN;

  // A cubic bezier's point at t=0.5 is 0.125*(P0+P3) + 0.375*(C1+C2). With both
  // control Ys equal to ctrlY, y(0.5) = 0.125*(sourceY+targetY) + 0.75*ctrlY.
  // Solve ctrlY so the curve's lowest point lands exactly at clearY.
  const ctrlY = (clearY - 0.125 * (sourceY + targetY)) / 0.75;

  const shoulder = Math.min(Math.max((xHi - xLo) * 0.3, MIN_SHOULDER), MAX_SHOULDER);
  const c1x = sourceX + shoulder; // leave the source's right handle going right
  const c2x = targetX - shoulder; // approach the target's left handle from the left

  const path = `M${sourceX},${sourceY} C${c1x},${ctrlY} ${c2x},${ctrlY} ${targetX},${targetY}`;
  const labelX = (sourceX + targetX) / 2;
  const labelY = 0.125 * (sourceY + targetY) + 0.75 * ctrlY; // == clearY by construction

  return [path, labelX, labelY];
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `npm test`
Expected: PASS — `# pass 6  # fail 0`.

- [ ] **Step 5: Commit**

```bash
git add src/components/board/canvas/feedbackEdgePath.ts src/components/board/canvas/feedbackEdgePath.test.ts
git commit -m "feat(web): add adaptive-depth getFeedbackPath geometry helper"
```

---

## Task 3: Wire the helper into `PipelineEdge`

**Files:**
- Modify: `src/components/board/canvas/CanvasView.tsx` (imports + `PipelineEdge`, around lines 4-9 and 178-184)

- [ ] **Step 1: Add `useStore` to the `@xyflow/react` import**

In the import block at `src/components/board/canvas/CanvasView.tsx:4-9`, add `useStore` to the named imports (next to `useReactFlow`):

```tsx
import {
  ReactFlow, ReactFlowProvider, Background, Controls, MiniMap, Panel,
  reconnectEdge, useNodesState, useEdgesState, useReactFlow, useStore,
  Handle, Position, BaseEdge, EdgeLabelRenderer, getBezierPath, MarkerType, BackgroundVariant,
  type Node, type Edge, type Connection, type NodeChange, type NodeProps, type EdgeProps,
} from '@xyflow/react';
```

- [ ] **Step 2: Import the geometry helper**

Add this import after the existing local imports (e.g., after the `toast` import near line 18):

```tsx
import { getFeedbackPath, type NodeBox } from './feedbackEdgePath';
```

- [ ] **Step 3: Update `PipelineEdge` to branch on feedback**

Replace the current head of `PipelineEdge` (lines 178-184):

```tsx
function PipelineEdge({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, markerEnd, selected, data }: EdgeProps) {
  const ui = useContext(EdgeUiCtx);
  const [path, labelX, labelY] = getBezierPath({ sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition });
  const feedback = !!(data as { feedback?: boolean })?.feedback;
  const label = ((data as { label?: string })?.label) ?? '';
  const currentIterations = (data as { currentIterations?: number })?.currentIterations;
  const maxIterations = (data as { maxIterations?: number })?.maxIterations;
```

with (note `source, target` added to the destructure, and the path now branches):

```tsx
function PipelineEdge({ id, source, target, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, markerEnd, selected, data }: EdgeProps) {
  const ui = useContext(EdgeUiCtx);
  const feedback = !!(data as { feedback?: boolean })?.feedback;
  // Read live node boxes from the store so the feedback arc can dip below nodes
  // that sit between its endpoints. nodeLookup changes identity when nodes move.
  const nodeLookup = useStore((s) => s.nodeLookup);
  const nodeBoxes = useMemo<NodeBox[]>(
    () => Array.from(nodeLookup.values()).map((n) => ({
      id: n.id,
      x: n.internals.positionAbsolute.x,
      y: n.internals.positionAbsolute.y,
      width: n.measured.width ?? 0,
      height: n.measured.height ?? 0,
    })),
    [nodeLookup],
  );
  const [path, labelX, labelY] = feedback
    ? getFeedbackPath({ sourceX, sourceY, targetX, targetY, sourceId: source, targetId: target, nodeBoxes })
    : getBezierPath({ sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition });
  const label = ((data as { label?: string })?.label) ?? '';
  const currentIterations = (data as { currentIterations?: number })?.currentIterations;
  const maxIterations = (data as { maxIterations?: number })?.maxIterations;
```

(Everything below — `editing`, `hovered`, `BaseEdge`, the `EdgeLabelRenderer` block — is unchanged; `path`, `labelX`, `labelY` keep the same names.)

- [ ] **Step 4: Typecheck and lint**

Run: `npx tsc --noEmit`
Expected: no errors.

Run: `npx eslint src/components/board/canvas/feedbackEdgePath.ts src/components/board/canvas/CanvasView.tsx`
Expected: no errors.

- [ ] **Step 5: Re-run unit tests (guard against regressions)**

Run: `npm test`
Expected: PASS — `# pass 6  # fail 0`.

- [ ] **Step 6: Commit**

```bash
git add src/components/board/canvas/CanvasView.tsx
git commit -m "feat(web): route QA feedback edges with adaptive-depth arc"
```

---

## Task 4: Visual verification against the running app

This project has no committed browser test suite; verify by driving the running app with Playwright (same method as the prior canvas fix), then delete the scratch script.

**Files:**
- Create (scratch, deleted at end): `qa/verify-feedback-edge.mjs`
- Screenshots go to `qa/shots/` (gitignored).

- [ ] **Step 1: Ensure the app is running**

```bash
curl -s -o /dev/null -w "api:%{http_code}\n" localhost:5138/api/boards
curl -s -o /dev/null -w "web:%{http_code}\n" localhost:3000/boards
```
Expected: both `200`. If not, start them:
- API: from `src/api/TectikaAgents.Api`, `dotnet run` (background).
- Web: from `src/web/tectika-board`, `npm run dev` (background). Wait until `:3000/boards` returns 200.

- [ ] **Step 2: Ensure Playwright chromium + WSL libs are available**

```bash
npx playwright install chromium
mkdir -p /tmp/pwlibs && cd /tmp/pwlibs && apt-get download libnspr4 libnss3 libasound2t64 \
  && for f in *.deb; do dpkg-deb -x "$f" extracted; done
```
(Only needed once per machine; skip if already present.)

- [ ] **Step 3: Write the verification script**

Create `src/web/tectika-board/qa/verify-feedback-edge.mjs`:

```js
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';
mkdirSync('qa/shots', { recursive: true });

const browser = await chromium.launch({ args: ['--no-sandbox', '--disable-dev-shm-usage'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
await page.goto('http://localhost:3000/boards/board-003/canvas', { waitUntil: 'domcontentloaded' });
await page.waitForTimeout(6000);

// board-003 has a QaFeedback edge: task-x10 -> task-x12.
const edge = page.locator('g[data-testid="rf__edge-task-x10->task-x12"] path.react-flow__edge-path');
await edge.waitFor({ timeout: 8000 });
const d = await edge.getAttribute('d');
console.log('feedback edge path d =', d);

// Parse all y coordinates out of the path and confirm the arc dips clearly downward.
const ys = [...d.matchAll(/[-\d.]+,([-\d.]+)/g)].map(m => parseFloat(m[1]));
const startY = ys[0];
const maxY = Math.max(...ys);
console.log('startY =', startY, 'maxY =', maxY, 'dip =', (maxY - startY).toFixed(1));
console.log('ARC_DIPS_DOWN:', maxY - startY > 30 ? 'YES' : 'NO');
console.log('IS_CUBIC:', /C/.test(d) ? 'YES' : 'NO');

await page.screenshot({ path: 'qa/shots/feedback-edge.png' });
await browser.close();
```

- [ ] **Step 4: Run the verification**

Run:
```bash
cd src/web/tectika-board && LD_LIBRARY_PATH=/tmp/pwlibs/extracted/usr/lib/x86_64-linux-gnu node qa/verify-feedback-edge.mjs
```
Expected output includes:
- `IS_CUBIC: YES`
- `ARC_DIPS_DOWN: YES`

- [ ] **Step 5: Eyeball the screenshot**

Open `qa/shots/feedback-edge.png` and confirm the orange dashed feedback edge bows below the node row and no longer overlaps the purple forward edges, with its label sitting on the curve.

- [ ] **Step 6: Clean up the scratch script**

```bash
rm -f qa/verify-feedback-edge.mjs
rmdir qa 2>/dev/null || true
```
(Leave `qa/shots/` contents; the dir is gitignored.) No commit needed for this task.

---

## Self-Review

**1. Spec coverage**
- Adaptive downward arc → Task 2 (`getFeedbackPath`) + Task 3 (wiring). ✓
- Always-downward direction → arc always uses `clearY = floor + MARGIN` below endpoints. ✓
- Depth clears nodes horizontally between endpoints → obstacle loop, tested. ✓
- Different relative positions → "different vertical levels" test + Task 4 (general geometry). ✓
- Excludes source/target nodes → tested. ✓
- Label/iteration badge on the curve → `labelX/labelY` returned in helper, reused by existing renderer (Task 3). ✓
- Forward edges unchanged → `feedback ? getFeedbackPath : getBezierPath`. ✓
- Styling unchanged → no edits to `BaseEdge` style / markers. ✓
- Accepted limitations (node directly under endpoint; multi-edge stagger deferred) → no task required by design. ✓
- Verification via running-app Playwright → Task 4. ✓

**2. Placeholder scan:** No TBD/TODO; every code/command step has concrete content. ✓

**3. Type consistency:** `NodeBox` and `getFeedbackPath` signatures are identical across the helper, its tests, and the `PipelineEdge` call site (`sourceId: source, targetId: target`, `nodeBoxes`). The helper returns `[string, number, number]` matching the existing `[path, labelX, labelY]` destructure. `useStore`/`useMemo` are both imported (`useMemo` already present at line 3; `useStore` added in Task 3 Step 1). ✓
