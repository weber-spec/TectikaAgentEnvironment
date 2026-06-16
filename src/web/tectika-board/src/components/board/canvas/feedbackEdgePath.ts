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
  // Solve ctrlY so the curve passes through clearY at t=0.5. For level endpoints
  // that is the arc's lowest point; for offset endpoints the arc dips at least that far.
  const ctrlY = (clearY - 0.125 * (sourceY + targetY)) / 0.75;

  // Shoulders follow the handle directions (source handle on the right, target handle
  // on the left) regardless of the nodes' relative positions.
  const shoulder = Math.min(Math.max((xHi - xLo) * 0.3, MIN_SHOULDER), MAX_SHOULDER);
  const c1x = sourceX + shoulder; // leave the source's right handle going right
  const c2x = targetX - shoulder; // approach the target's left handle from the left

  const path = `M${sourceX},${sourceY} C${c1x},${ctrlY} ${c2x},${ctrlY} ${targetX},${targetY}`;
  const labelX = (sourceX + targetX) / 2;
  const labelY = clearY; // the curve passes through clearY at t=0.5

  return [path, labelX, labelY];
}
