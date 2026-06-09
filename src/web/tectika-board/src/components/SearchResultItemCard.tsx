'use client';

import type { SearchResultItem } from '@/lib/types';

interface SearchResultItemCardProps {
  item: SearchResultItem;
  index: number;
  selected: boolean;
  onSelect: (index: number) => void;
}

export function SearchResultItemCard({ item, index, selected, onSelect }: SearchResultItemCardProps) {
  return (
    <div
      role="button"
      tabIndex={0}
      onClick={() => onSelect(index)}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect(index); } }}
      className={[
        'relative flex gap-3 rounded-xl border bg-[var(--background)] p-3.5 cursor-pointer transition-all select-none',
        'hover:shadow-md focus:outline-none focus-visible:ring-2 focus-visible:ring-[var(--primary)]',
        selected
          ? 'border-[#0073ea] ring-2 ring-[#0073ea] shadow-sm'
          : 'border-[var(--border)] hover:border-[var(--muted-2)]',
      ].join(' ')}
      aria-pressed={selected}
    >
      {/* Radio indicator */}
      <div className="shrink-0 mt-0.5">
        <div
          aria-label={`Select ${item.title}`}
          role="radio"
          aria-checked={selected}
          className={[
            'w-4 h-4 rounded-full border-2 flex items-center justify-center transition-colors',
            selected ? 'border-[#0073ea] bg-[#0073ea]' : 'border-[var(--muted-2)] bg-transparent',
          ].join(' ')}
        >
          {selected && (
            <span className="w-1.5 h-1.5 rounded-full bg-white block" />
          )}
        </div>
      </div>

      {/* Optional thumbnail image */}
      {item.imageUrl && (
        <img
          src={item.imageUrl}
          alt={item.title}
          className="shrink-0 w-16 h-16 rounded-lg object-cover border border-[var(--border)]"
        />
      )}

      {/* Main content */}
      <div className="flex-1 min-w-0">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            {/* Title */}
            <p className="text-sm font-semibold text-[var(--foreground)] leading-snug truncate">
              {item.title}
            </p>

            {/* Subtitle */}
            {item.subtitle && (
              <p className="text-xs text-[var(--muted)] mt-0.5 truncate">
                {item.subtitle}
              </p>
            )}
          </div>

          {/* Price — top-right, prominent */}
          {item.price && (
            <span className="shrink-0 text-sm font-bold text-[var(--foreground)] whitespace-nowrap">
              {item.price}
            </span>
          )}
        </div>

        {/* Detail bullets */}
        {item.details && item.details.length > 0 && (
          <ul className="mt-2 flex flex-col gap-0.5">
            {item.details.map((detail, i) => (
              <li key={i} className="flex items-start gap-1.5 text-xs text-[var(--muted)]">
                <span className="mt-1 w-1 h-1 rounded-full bg-[var(--muted-2)] shrink-0" />
                <span>{detail}</span>
              </li>
            ))}
          </ul>
        )}

        {/* External link */}
        {item.link && (
          <a
            href={item.link}
            target="_blank"
            rel="noopener noreferrer"
            onClick={e => e.stopPropagation()}
            className="inline-flex items-center gap-1 mt-2.5 text-xs font-medium text-[#0073ea] hover:underline"
            aria-label={`View ${item.title} (opens in new tab)`}
          >
            View
            <svg width="10" height="10" viewBox="0 0 24 24" fill="none" aria-hidden="true">
              <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" />
              <polyline points="15 3 21 3 21 9" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" />
              <line x1="10" y1="14" x2="21" y2="3" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </a>
        )}
      </div>
    </div>
  );
}
