import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import type { PipelineStepState } from '@/features/pipeline/pipelineSteps';
import { t } from '@/shared/i18n';

/**
 * Бейдж стану кроку конвеєра — ЛОКАЛЬНИЙ, а не `shared/ui/StatusBadge`.
 *
 * ⚠ Стан кроку обчислює КЛІЄНТ (`pipelineSteps.ts`), це не член `enum`
 * сервера, тож до таблиці `kind × state` спільного бейджа він не належить; а
 * `shared/ui/**` — гарячий файл, який ця задача не чіпає. Токени кольору — ті
 * самі `statusWarning`/`statusError` теми, що й у `CollectionRunStateBadge`.
 *
 * ⛔ Зеленого тону немає (`KIT.md` §1.3): нормальний стан нейтральний.
 *
 * ⛔ Підпис — літеральними `t(...)` у кожній гілці: сторож
 * `EndpointCoverageTests` бачить ключ зі змінної як динамічний виклик.
 */
function stateLabel(state: PipelineStepState): string {
  switch (state) {
    case 'ok':
      return t('pipeline.state.ok');
    case 'zero':
      return t('pipeline.state.zero');
    case 'idle':
      return t('pipeline.state.idle');
    case 'warn':
      return t('pipeline.state.warn');
    case 'off':
      return t('pipeline.state.off');
    case 'error':
      return t('pipeline.state.error');
  }
}

const Colors: Readonly<Record<PipelineStepState, string>> = {
  ok: 'gray',
  zero: 'statusError',
  idle: 'gray',
  warn: 'statusWarning',
  off: 'gray',
  error: 'statusError',
};

export function PipelineStepBadge({ state }: { readonly state: PipelineStepState }): JSX.Element {
  return (
    <Badge size="sm" miw="fit-content" variant="light" color={Colors[state]} data-step-state={state}>
      {stateLabel(state)}
    </Badge>
  );
}
