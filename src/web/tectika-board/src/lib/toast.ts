'use client';

// Tiny global toast store (no deps). Subscribe with useToasts(); push with toast().

import { useSyncExternalStore } from 'react';

export type ToastKind = 'success' | 'error' | 'info';
export interface Toast {
  id: string;
  kind: ToastKind;
  message: string;
  action?: { label: string; onClick: () => void };
}

let toasts: Toast[] = [];
const listeners = new Set<() => void>();
let n = 0;

function emit() {
  // new array reference for useSyncExternalStore change detection
  toasts = [...toasts];
  listeners.forEach(l => l());
}

export function toast(message: string, kind: ToastKind = 'info', action?: Toast['action']): string {
  const id = `t${++n}`;
  toasts.push({ id, kind, message, action });
  emit();
  if (kind !== 'error') setTimeout(() => dismissToast(id), 4000);
  else setTimeout(() => dismissToast(id), 7000);
  return id;
}

export function dismissToast(id: string) {
  toasts = toasts.filter(t => t.id !== id);
  listeners.forEach(l => l());
}

function subscribe(cb: () => void) {
  listeners.add(cb);
  return () => { listeners.delete(cb); };
}

export function useToasts(): Toast[] {
  return useSyncExternalStore(subscribe, () => toasts, () => toasts);
}
