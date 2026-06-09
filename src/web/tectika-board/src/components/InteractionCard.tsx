'use client';

import { useState } from 'react';
import type { HumanInteraction } from '@/lib/types';
import { api } from '@/lib/api';
import { Button, Spinner } from '@/components/ui/primitives';
import { SearchResultItemCard } from '@/components/SearchResultItemCard';
import { relativeTime } from '@/lib/format';
import { Icon } from '@/components/ui/icons';

interface InteractionCardProps {
  interaction: HumanInteraction;
  onResponded: () => void;
}

// ── Shared card chrome ────────────────────────────────────────────────────────

function CardShell({ interaction, children }: { interaction: HumanInteraction; children: React.ReactNode }) {
  return (
    <div
      className="bg-[var(--background)] rounded-xl border border-[var(--border)] p-4 flex flex-col gap-3"
      style={{ borderLeft: '4px solid var(--primary)' }}
    >
      {/* Header */}
      <div className="flex items-start gap-3">
        <span className="w-9 h-9 rounded-lg flex items-center justify-center shrink-0"
          style={{ background: 'color-mix(in srgb, var(--primary) 15%, transparent)', color: 'var(--primary)' }}>
          <Icon.warning size={18} />
        </span>
        <div className="flex-1 min-w-0">
          <p className="text-sm font-semibold text-[var(--foreground)]">{interaction.actionDescription}</p>
          <div className="flex items-center gap-3 mt-1.5 text-[11px] text-[var(--muted)] flex-wrap">
            <span className="inline-flex items-center gap-1">
              <Icon.clock size={12} /> requested {relativeTime(interaction.requestedAt)}
            </span>
            <span className="inline-flex items-center gap-1">
              task {interaction.taskId.slice(0, 8)}
            </span>
          </div>
        </div>
      </div>

      {/* Variant-specific content */}
      {children}
    </div>
  );
}

// ── Error banner ──────────────────────────────────────────────────────────────

function ErrorBanner({ message }: { message: string }) {
  return (
    <p className="text-xs text-[#e2445c] bg-[#e2445c11] border border-[#e2445c33] rounded-lg px-3 py-2">
      {message}
    </p>
  );
}

// ── Approval variant ──────────────────────────────────────────────────────────

function ApprovalVariant({ interaction, onResponded }: InteractionCardProps) {
  const [notes, setNotes] = useState('');
  const [submitting, setSubmitting] = useState<'approve' | 'reject' | null>(null);
  const [error, setError] = useState<string | null>(null);

  const submit = async (action: 'approve' | 'reject') => {
    setSubmitting(action);
    setError(null);
    try {
      await api.interactions.respond(interaction.id, interaction.runId, { approved: action === 'approve', notes: notes || undefined });
      onResponded();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to submit decision. Please try again.');
    } finally {
      setSubmitting(null);
    }
  };

  return (
    <CardShell interaction={interaction}>
      {/* Notes textarea */}
      <div className="flex flex-col gap-1.5">
        <label className="text-xs font-medium text-[var(--muted)]" htmlFor={`notes-${interaction.id}`}>
          Notes
        </label>
        <textarea
          id={`notes-${interaction.id}`}
          value={notes}
          onChange={e => setNotes(e.target.value)}
          disabled={submitting !== null}
          placeholder="Optional notes..."
          rows={2}
          className="w-full rounded-lg border border-[var(--border)] bg-[var(--surface)] text-sm text-[var(--foreground)] placeholder:text-[var(--muted)] px-3 py-2 resize-none focus:outline-none focus:ring-2 focus:ring-[var(--primary)] disabled:opacity-50"
        />
      </div>

      {error && <ErrorBanner message={error} />}

      {/* Action buttons */}
      <div className="flex items-center justify-end gap-2">
        <Button
          variant="danger"
          size="sm"
          disabled={submitting !== null}
          onClick={() => submit('reject')}
        >
          {submitting === 'reject' ? <Spinner size={13} /> : <Icon.x size={15} />}
          Reject
        </Button>
        <Button
          variant="primary"
          size="sm"
          disabled={submitting !== null}
          onClick={() => submit('approve')}
        >
          {submitting === 'approve' ? <Spinner size={13} /> : <Icon.check size={15} />}
          Approve
        </Button>
      </div>
    </CardShell>
  );
}

// ── Selection variant ─────────────────────────────────────────────────────────

function SelectionVariant({ interaction, onResponded }: InteractionCardProps) {
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const items = interaction.items ?? [];

  const submit = async () => {
    if (selectedIndex === null) return;
    setIsSubmitting(true);
    setError(null);
    try {
      await api.interactions.respond(interaction.id, interaction.runId, { selectedIndex });
      onResponded();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to submit selection. Please try again.');
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <CardShell interaction={interaction}>
      {/* Item list */}
      <div
        role="radiogroup"
        aria-label={interaction.actionDescription}
        className="flex flex-col gap-2"
      >
        {items.map((item, i) => (
          <SearchResultItemCard
            key={i}
            item={item}
            index={i}
            selected={selectedIndex === i}
            onSelect={idx => !isSubmitting && setSelectedIndex(idx)}
          />
        ))}
        {items.length === 0 && (
          <p className="text-sm text-[var(--muted)] py-2">No items to select.</p>
        )}
      </div>

      {error && <ErrorBanner message={error} />}

      {/* Submit */}
      <div className="flex items-center justify-end">
        <Button
          variant="primary"
          size="sm"
          disabled={selectedIndex === null || isSubmitting}
          onClick={submit}
        >
          {isSubmitting && <Spinner size={13} />}
          Confirm Selection
        </Button>
      </div>
    </CardShell>
  );
}

// ── Question variant ──────────────────────────────────────────────────────────

function QuestionVariant({ interaction, onResponded }: InteractionCardProps) {
  const [answer, setAnswer] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const hasOptions = Array.isArray(interaction.questionOptions) && interaction.questionOptions.length > 0;

  const submit = async () => {
    if (!answer.trim()) return;
    setIsSubmitting(true);
    setError(null);
    try {
      await api.interactions.respond(interaction.id, interaction.runId, { answer });
      onResponded();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to submit answer. Please try again.');
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <CardShell interaction={interaction}>
      {/* Question text */}
      {interaction.question && (
        <p className="text-sm font-medium text-[var(--foreground)] bg-[var(--surface)] rounded-lg px-3 py-2.5 border border-[var(--border)]">
          {interaction.question}
        </p>
      )}

      {/* Radio options or free-text textarea */}
      {hasOptions ? (
        <div className="flex flex-col gap-2" role="radiogroup" aria-label="Select an answer">
          {interaction.questionOptions!.map((opt, i) => {
            const id = `q-${interaction.id}-opt-${i}`;
            const isSelected = answer === opt;
            return (
              <label
                key={i}
                htmlFor={id}
                className={[
                  'flex items-center gap-3 rounded-lg border px-3 py-2.5 cursor-pointer transition-all select-none',
                  isSelected
                    ? 'border-[var(--primary)] bg-[color-mix(in_srgb,var(--primary)_8%,transparent)]'
                    : 'border-[var(--border)] hover:border-[var(--muted-2)] hover:bg-[var(--surface)]',
                  isSubmitting ? 'opacity-50 cursor-not-allowed' : '',
                ].join(' ')}
              >
                <input
                  type="radio"
                  id={id}
                  name={`q-${interaction.id}`}
                  value={opt}
                  checked={isSelected}
                  disabled={isSubmitting}
                  onChange={() => setAnswer(opt)}
                  className="sr-only"
                />
                {/* Custom radio indicator */}
                <span
                  aria-hidden="true"
                  className={[
                    'w-4 h-4 rounded-full border-2 flex items-center justify-center shrink-0 transition-colors',
                    isSelected ? 'border-[var(--primary)] bg-[var(--primary)]' : 'border-[var(--muted-2)] bg-transparent',
                  ].join(' ')}
                >
                  {isSelected && <span className="w-1.5 h-1.5 rounded-full bg-white block" />}
                </span>
                <span className="text-sm text-[var(--foreground)]">{opt}</span>
              </label>
            );
          })}
        </div>
      ) : (
        <div className="flex flex-col gap-1.5">
          <label className="text-xs font-medium text-[var(--muted)]" htmlFor={`answer-${interaction.id}`}>
            Your answer
          </label>
          <textarea
            id={`answer-${interaction.id}`}
            value={answer}
            onChange={e => setAnswer(e.target.value)}
            disabled={isSubmitting}
            placeholder="Type your answer..."
            rows={3}
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--surface)] text-sm text-[var(--foreground)] placeholder:text-[var(--muted)] px-3 py-2 resize-none focus:outline-none focus:ring-2 focus:ring-[var(--primary)] disabled:opacity-50"
          />
        </div>
      )}

      {error && <ErrorBanner message={error} />}

      {/* Submit */}
      <div className="flex items-center justify-end">
        <Button
          variant="primary"
          size="sm"
          disabled={!answer.trim() || isSubmitting}
          onClick={submit}
        >
          {isSubmitting && <Spinner size={13} />}
          Send Answer
        </Button>
      </div>
    </CardShell>
  );
}

// ── Public component — switches on interaction.type ───────────────────────────

export function InteractionCard({ interaction, onResponded }: InteractionCardProps) {
  switch (interaction.type) {
    case 'Approval':
      return <ApprovalVariant interaction={interaction} onResponded={onResponded} />;
    case 'Selection':
      return <SelectionVariant interaction={interaction} onResponded={onResponded} />;
    case 'Question':
      return <QuestionVariant interaction={interaction} onResponded={onResponded} />;
    default:
      return null;
  }
}
