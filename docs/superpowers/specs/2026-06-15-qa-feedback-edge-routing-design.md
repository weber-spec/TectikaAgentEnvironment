# QA feedback edge routing — design

**Date:** 2026-06-15
**Component:** `src/web/tectika-board/src/components/board/canvas/CanvasView.tsx` (`PipelineEdge`)
**Status:** Approved (design)

## Problem

On the Flow Canvas, a QA feedback edge (`TaskEdge.kind === 'QaFeedback'`) is a back-edge: it
links a later node to an earlier node, which usually sits to the left at roughly the same
vertical level. It is currently drawn with `getBezierPath` — the same helper used for forward
(Dependency) edges. Because the two handles are nearly horizontal and the target is to the left,
the bezier collapses onto the same horizontal lane as the forward edges and overlaps them,
producing a tangled, hard-to-read line.

The canvas is freely draggable ("playable"), so source and target can be at arbitrary relative
positions and there can be unrelated nodes occupying the space between them.

## Goal

Route QA feedback edges so they read as a distinct loop that is visually separated from the
forward-edge lane, and that stays clear of nodes sitting between the two endpoints, at any
relative position — without resorting to full obstacle-avoidance pathfinding.

## Scope

- **In scope:** rendering of feedback edges only, inside `PipelineEdge`.
- **Out of scope:** forward (Dependency) edges keep `getBezierPath` unchanged; no changes to
  handles, connection logic, edge data model, persistence, or the live-sync layer.
- **Rejected alternatives:**
  - *Simple fixed-depth bow* — cheapest, but clips intermediate nodes.
  - *Full orthogonal / A\* obstacle avoidance* — most robust, but heavy on a draggable canvas
    and a large amount of new code. YAGNI for this board.

## Approach: adaptive-depth downward arc

When `data.feedback` is true, replace `getBezierPath` with a custom arc.

### Direction
The arc **always bows downward** (below the nodes), for every feedback edge, regardless of the
relative position of source and target. Anchoring to the two handles and dipping below handles
the "different horizontal positions" case naturally (target level, higher, or lower all work).
Always-downward is chosen over per-edge up/down for visual consistency and predictability.

### Path shape
A single cubic bezier:

- Start: source handle `(sourceX, sourceY)` (the source node's right handle).
- Control 1: `(sourceX + K, clearY)` — pulls right and down out of the source.
- Control 2: `(targetX - K, clearY)` — pulls left and down into the target.
- End: target handle `(targetX, targetY)` (the target node's left handle).

`K` is a horizontal offset that gives the curve a smooth shoulder, e.g.
`K = clamp(0.3 * |sourceX - targetX|, 40, 200)` (tunable during verification).

### Adaptive depth (`clearY`)
- Read all node bounding boxes from the React Flow store via `useStore` (absolute position +
  measured width/height). This is read on every render, so it tracks live dragging.
- Select nodes whose horizontal span `[n.x, n.x + n.width]` overlaps the edge's x-range
  `[min(sourceX, targetX), max(sourceX, targetX)]`, **excluding the source and target nodes**.
- `obstacleBottom = max(n.y + n.height)` over those nodes (or `-Infinity` if none).
- `clearY = max(obstacleBottom, sourceY, targetY) + MARGIN`, with `MARGIN ≈ 40px`.
- Because a cubic bezier's lowest point sits above its control-point Y, apply a small overshoot
  so the *curve* (not just the control points) clears the obstacles — e.g. push the control Y to
  `clearY + (clearY - max(sourceY, targetY)) * 0.33`, or simply fold extra into MARGIN. Exact
  constant tuned during visual verification.

Result: no nodes between → a shallow, tidy curve; a tall stack between → a deeper arc that dips
below it.

### Label / iteration badge
`getFeedbackPath` returns `[path, labelX, labelY]` — the same tuple shape as `getBezierPath` — so
the existing `EdgeLabelRenderer` block (label pill, `↻ current/max` badge, delete button) is
reused unchanged.

- `labelX = (sourceX + targetX) / 2`
- `labelY` = the bezier point at `t = 0.5` (the arc's approximate lowest point), so the label sits
  on the curve rather than floating in the old horizontal lane.

### Styling
Unchanged: `FEEDBACK_COLOR` (orange), dashed stroke, `FEEDBACK_MARKER`, and the existing
hover/selected highlight (`show` → thicker stroke). Forward-edge styling is untouched.

## Implementation shape

All within `CanvasView.tsx`:

1. Add a pure helper `getFeedbackPath({ sourceX, sourceY, targetX, targetY, nodeBoxes })` returning
   `[string, number, number]`.
2. In `PipelineEdge`, call `useStore` unconditionally to obtain node boxes (cheap selector returning
   `{x, y, width, height}` per node); only feedback edges use the result.
3. Branch: `const [path, labelX, labelY] = feedback ? getFeedbackPath(...) : getBezierPath(...)`.
4. Everything downstream (`BaseEdge`, label renderer) stays as-is.

## Limitations (accepted)

- Clears nodes **horizontally between** source and target only. It will not dodge a node parked
  directly beneath the target/source (outside the between-span); the arc still reads as clearly
  separated from the forward lane. This is the deliberate boundary of the "level 1" choice over
  full pathfinding.
- Multiple feedback edges with nearly identical spans can overlap each other. A small per-edge
  depth stagger (hash of edge id) would fix this; **deferred — not in v1**.

## Verification

Use the same "drive the running app with Playwright" method as the prior canvas fix:

- A feedback edge with no nodes between → shallow curve below the lane, not overlapping forward edges.
- Drag a node into the between-span → the arc deepens to dip below it.
- Source/target at clearly different vertical levels → arc still bows downward and connects cleanly.
- The label pill + `↻ n/max` badge render on the curve; editing the label still works (regression
  check against the earlier remount fix).
- Forward edges visually unchanged.
