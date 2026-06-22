// Color system — monday.com-inspired. A fixed, well-spaced palette with semantic
// status/priority mappings used across every view, the canvas, and dashboards.

/** The full label/color-picker palette (≈40 swatches), spanning the rainbow. */
export const PALETTE: { name: string; hex: string }[] = [
  { name: 'Grass green', hex: '#037f4c' },
  { name: 'Done green', hex: '#00c875' },
  { name: 'Bright green', hex: '#9cd326' },
  { name: 'Saladish', hex: '#cab641' },
  { name: 'Egg yolk', hex: '#ffcb00' },
  { name: 'Working orange', hex: '#fdab3d' },
  { name: 'Dark orange', hex: '#ff642e' },
  { name: 'Peach', hex: '#ffadad' },
  { name: 'Sunset', hex: '#ff7575' },
  { name: 'Stuck red', hex: '#e2445c' },
  { name: 'Dark red', hex: '#bb3354' },
  { name: 'Sofia pink', hex: '#ff5ac4' },
  { name: 'Lipstick', hex: '#ff158a' },
  { name: 'Bubble', hex: '#faa1f1' },
  { name: 'Purple', hex: '#a25ddc' },
  { name: 'Dark purple', hex: '#784bd1' },
  { name: 'Berry', hex: '#7e3b8a' },
  { name: 'Dark indigo', hex: '#401694' },
  { name: 'Indigo', hex: '#5559df' },
  { name: 'Navy', hex: '#225091' },
  { name: 'Bright blue', hex: '#0086c0' },
  { name: 'Dark blue', hex: '#0073ea' },
  { name: 'Aquamarine', hex: '#4eccc6' },
  { name: 'Chili blue', hex: '#66ccff' },
  { name: 'River', hex: '#68a1bd' },
  { name: 'Winter', hex: '#9aadbd' },
  { name: 'Explosive', hex: '#c4c4c4' },
  { name: 'American gray', hex: '#808080' },
  { name: 'Blackish', hex: '#333333' },
  { name: 'Brown', hex: '#7f5347' },
  { name: 'Orchid', hex: '#d974b3' },
  { name: 'Tan', hex: '#bca58a' },
  { name: 'Sky', hex: '#a1e3f5' },
  { name: 'Coffee', hex: '#bb9990' },
  { name: 'Royal', hex: '#5934b0' },
  { name: 'Teal', hex: '#175a63' },
  { name: 'Lavender', hex: '#bda8f9' },
  { name: 'Steel', hex: '#5b7e95' },
  { name: 'Pecan', hex: '#563e3e' },
  { name: 'Lilac', hex: '#9d99b9' },
];

/** Returns readable text color (#fff or dark) for a given background hex. */
export function textOn(hex: string): string {
  const c = hex.replace('#', '');
  const r = parseInt(c.slice(0, 2), 16);
  const g = parseInt(c.slice(2, 4), 16);
  const b = parseInt(c.slice(4, 6), 16);
  // perceived luminance
  const lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
  return lum > 0.62 ? '#323338' : '#ffffff';
}

/** Translucent version of a hex color for soft backgrounds. */
export function alpha(hex: string, a: number): string {
  const c = hex.replace('#', '');
  const r = parseInt(c.slice(0, 2), 16);
  const g = parseInt(c.slice(2, 4), 16);
  const b = parseInt(c.slice(4, 6), 16);
  return `rgba(${r}, ${g}, ${b}, ${a})`;
}

// ── Status ──────────────────────────────────────────────────────────────────

import type { AgentTaskStatus, TaskPriority } from './types';

export interface LabelConfig {
  label: string;
  hex: string;
}

export const STATUS_CONFIG: Record<AgentTaskStatus, LabelConfig> = {
  Backlog:               { label: 'Backlog',               hex: '#c4c4c4' },
  InProgress:            { label: 'Working on it',         hex: '#fdab3d' },
  AwaitingInteraction:   { label: 'Awaiting Interaction',  hex: '#66ccff' },
  Blocked:               { label: 'Stuck',                 hex: '#ff642e' },
  Review:                { label: 'In Review',             hex: '#579bfc' },
  Done:                  { label: 'Done',                  hex: '#00c875' },
  Failed:                { label: 'Failed',                hex: '#e2445c' },
};

export const STATUS_ORDER: AgentTaskStatus[] = [
  'Backlog', 'InProgress', 'AwaitingInteraction', 'Review', 'Blocked', 'Done', 'Failed',
];

export const PRIORITY_CONFIG: Record<TaskPriority, LabelConfig> = {
  Critical: { label: 'Critical', hex: '#333333' },
  High:     { label: 'High',     hex: '#e2445c' },
  Medium:   { label: 'Medium',   hex: '#fdab3d' },
  Low:      { label: 'Low',      hex: '#579bfc' },
};

export const PRIORITY_ORDER: TaskPriority[] = ['Critical', 'High', 'Medium', 'Low'];

/** Deterministic color for a person/string (avatars, tags). */
const AVATAR_COLORS = [
  '#0073ea', '#00c875', '#fdab3d', '#e2445c', '#a25ddc',
  '#ff642e', '#0086c0', '#9cd326', '#ff5ac4', '#784bd1',
  '#4eccc6', '#bb3354',
];

export function colorFor(key: string): string {
  let hash = 0;
  for (let i = 0; i < key.length; i++) hash = key.charCodeAt(i) + ((hash << 5) - hash);
  return AVATAR_COLORS[Math.abs(hash) % AVATAR_COLORS.length];
}
