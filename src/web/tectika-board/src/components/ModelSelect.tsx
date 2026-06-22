'use client';

import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import { Icon } from '@/components/ui/icons';
import { buildModelOptions, type ModelFetchStatus } from '@/lib/model-options';

/** Fetches the available model list once on mount. Errors surface as status 'error'
 *  (the backend returns 502 when the live Foundry catalog is unreachable). */
function useModels(): { models: string[]; status: ModelFetchStatus } {
  const [models, setModels] = useState<string[]>([]);
  const [status, setStatus] = useState<ModelFetchStatus>('loading');
  useEffect(() => {
    let cancelled = false;
    api.models.list()
      .then(m => { if (!cancelled) { setModels(m); setStatus('ready'); } })
      .catch(() => { if (!cancelled) setStatus('error'); });
    return () => { cancelled = true; };
  }, []);
  return { models, status };
}

/**
 * Model picker backed by the live model list. `value` is the role's modelOverride
 * (undefined/'' ⇒ project default); `onChange` receives '' for the Default option.
 * On fetch failure it shows an error and offers only Default + the saved value.
 */
export function ModelSelect({
  value, onChange, selectClassName, wrapperClassName, defaultLabel = 'Default', chevron = false,
}: {
  value?: string;
  onChange: (value: string) => void;
  selectClassName?: string;
  wrapperClassName?: string;
  defaultLabel?: string;
  chevron?: boolean;
}) {
  const { models, status } = useModels();
  const saved = value ?? '';
  const options = buildModelOptions({ models, saved, status });

  const select = (
    <select
      value={saved}
      onChange={e => onChange(e.target.value)}
      disabled={status === 'loading'}
      className={selectClassName}
      aria-label="Model"
    >
      {options.map(o => (
        <option key={o.value || '__default'} value={o.value}>
          {o.value === '' ? (status === 'loading' ? 'Loading models…' : defaultLabel) : o.label}
        </option>
      ))}
    </select>
  );

  return (
    <div className={wrapperClassName}>
      {chevron ? (
        <div className="relative">
          {select}
          <Icon.chevronDown size={14} className="absolute right-2.5 top-1/2 -translate-y-1/2 text-[var(--muted)] pointer-events-none" />
        </div>
      ) : select}
      {status === 'error' && (
        <p className="mt-1 text-[11px] text-[#e2445c]">
          Couldn’t load models from Foundry — showing your saved model only.
        </p>
      )}
    </div>
  );
}
