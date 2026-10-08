import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { formatDateTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { Hint } from '@/shared/ui/Hint';
import { toneFills } from '@/shared/ui/StatusBadge';

interface StaleResultsMarkProps {
  /** Відколи застаріло (`DocumentSummary.resultsStaleSince`); `null` — сервер не називає. */
  readonly since?: string | null | undefined;
}

/**
 * Бейдж «Results stale» у рядку переліку (RC14-D, `DocumentSummary.resultsStale === true`).
 *
 * ⚠ Будова — як у `LateEditsMark`: слово тоном `warning` з `toneFills` (пара вже виміряна на контраст в обох
 * темах), пояснення через `<Hint focusable>`, а не `title`. Підпис — той самий ключ, що в картці документа
 * (`documents.methodologyResultsStale`): одне явище — одне слово на всіх екранах.
 * ⛔ Малюється ЛИШЕ за `resultsStale === true`: `null` (звужений доступ) і `false` бейджа не мають.
 * ⚠ Макет `docs/design/hybrid/` бейджа застарілості не має (прогалина макета): дефолт — тон рядка стану.
 */
export function StaleResultsMark({ since }: StaleResultsMarkProps): JSX.Element {
  const fill = toneFills.warning;
  const hint =
    since === null || since === undefined
      ? t('document.staleResults.hint')
      : t('document.staleResults.hintSince', { date: formatDateTime(since) });

  return (
    <Hint label={hint} focusable>
      <Badge
        size="sm"
        miw="fit-content"
        variant="transparent"
        c={fill.text}
        styles={{ root: { textTransform: 'none', letterSpacing: 'normal', fontWeight: 500, paddingInline: 0 } }}
        data-results-stale="true"
      >
        {t('documents.methodologyResultsStale')}
      </Badge>
    </Hint>
  );
}
