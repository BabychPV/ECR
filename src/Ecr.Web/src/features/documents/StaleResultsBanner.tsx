import type { JSX } from 'react';
import { Alert, Button, Group, Text } from '@mantine/core';
import { formatDateTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';

interface StaleResultsBannerProps {
  /** Відколи застаріло (перша правка входу після прогону); `null` — сервер не називає. */
  readonly since: string | null;
  /**
   * Дія «Перерахувати» — та сама, що в «More» і F9 (`useSheetActions`); `null` — ролі її не дано
   * (немає права чи гранта): тоді банер лише повідомляє.
   */
  readonly recalculate: { readonly loading: boolean; readonly running: boolean; readonly run: () => void } | null;
}

/**
 * Банер «результати методологій застаріли» над сіткою (рішення людини: автоперерахунку немає — банер і кнопка).
 *
 * ⛔ Значення береться лише з `DocumentSummary.resultsStale`, яке сервер ВИВОДИТЬ (правка входу після найновішого
 * прогону); читачу, чий доступ звужено нижче проєкту, воно `null` — банера немає (не оракул про схований вхід).
 * Після успішного перерахунку `SheetActions` перечитує картку документа, і банер зникає сам.
 *
 * ⚠ Мінімальний, на `Alert`, як сусідній `DocumentSheetBanner`: макет `docs/design/hybrid/` окремого банера
 * застарілості не має (прогалина макета, дефолт — тон `statusWarning` рядка стану аркуша).
 */
export function StaleResultsBanner({ since, recalculate }: StaleResultsBannerProps): JSX.Element {
  return (
    <Alert
      color="statusWarning"
      variant="light"
      role="status"
      data-testid="document-stale-results"
      title={t('document.staleResults.title')}
    >
      <Group justify="space-between" align="center" wrap="wrap" gap="xs">
        <Text size="sm" data-testid="document-stale-results-hint">
          {since === null
            ? t('document.staleResults.hint')
            : t('document.staleResults.hintSince', { date: formatDateTime(since) })}
        </Text>
        {recalculate !== null && (
          <Button
            variant="default"
            size="xs"
            loading={recalculate.loading}
            disabled={recalculate.running}
            onClick={recalculate.run}
            data-testid="document-stale-results-recalculate"
          >
            {recalculate.running ? t('workflow.recalcRunning') : t('workflow.recalculate')}
          </Button>
        )}
      </Group>
    </Alert>
  );
}
