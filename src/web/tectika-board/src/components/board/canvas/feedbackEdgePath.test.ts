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

test('label y equals baseY + MARGIN (40) when there are no obstacles', () => {
  const [, , labelY] = getFeedbackPath({ ...base, nodeBoxes: [] });
  assert.equal(labelY, 140); // max(sourceY,targetY)=100 + MARGIN=40
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
