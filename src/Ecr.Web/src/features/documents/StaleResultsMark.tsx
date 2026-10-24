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
 * ⛔ Малюється ЛИШЕ за `resultsStale === true`: `null` (звужений доступ) і `false` бейджа не мають
 * (✎ D-446: звужений доступ пояснює `StaleResultsUndeterminedMark` нижче).
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

/**
 * ✎ D-446: позначка «актуальність результатів не визначається» — `DocumentSummary.resultsStaleUndetermined ===
 * 'PartiallyHidden'`: застарілість за період порахована, але частину документа читачеві закрито (документ-надання
 * зі схованими частинами або звуження ролі в проєкті), тож сервер її не віддає (`resultsStale === null`).
 *
 * ⚠ Тон `muted`, а не `warning`: це не тривога «застаріло», а пояснення, чому позначки немає; пара з `toneFills`
 * (та сама таблиця, що в `<StatusBadge>`). Будова й місце — як у `StaleResultsMark`: той самий бейдж-слово поруч із
 * часом, тож рядок не стрибає; пояснення — через `<Hint focusable>` (з клавіатури й для читалки), не `title`.
 * ⛔ Без періоду (`resultsStaleUndetermined === null`) позначки немає: «немає періоду» клієнт пояснює деінде.
 */
export function StaleResultsUndeterminedMark(): JSX.Element {
  const fill = toneFills.muted;

  return (
    <Hint label={t('documents.staleUndeterminedHint')} focusable>
      <Badge
        size="sm"
        miw="fit-content"
        variant="transparent"
        c={fill.text}
        styles={{ root: { textTransform: 'none', letterSpacing: 'normal', fontWeight: 400, paddingInline: 0 } }}
        data-results-stale-undetermined="PartiallyHidden"
      >
        {t('documents.staleUndetermined')}
      </Badge>
    </Hint>
  );
}
