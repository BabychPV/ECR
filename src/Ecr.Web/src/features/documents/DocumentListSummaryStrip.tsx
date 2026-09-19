import type { JSX } from 'react';
import { Group, Stack, Text } from '@mantine/core';
import { useDocumentListSummary } from '@/features/documents/api';
import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';
import { statusKey } from '@/shared/ui/StatusBadge';

/** За який період зводити; `null` — період не обрано. */
export interface DocumentListSummaryStripProps {
  readonly periodKey: number | null;
}

/**
 * Смуга лічильників над переліком документів (`BE-09`).
 *
 * ⚠ Лічильники — НЕ кнопки: фільтра `state` на сервері ще немає (наступний
 * PR), а кнопка, яка нічого не фільтрує, — обіцянка, якої екран не виконує.
 *
 * ⚠ Підписи станів — ті самі рядки каталогу, що й у `<StatusBadge>`
 * (`status.sheet.*`): число над таблицею і бейдж у ній мусять називати стан
 * одним словом.
 *
 * ⛔ Без періоду, під час завантаження і при відмові смуги НЕМАЄ зовсім: нулі
 * на її місці читалися б як «документів немає».
 */
export function DocumentListSummaryStrip({ periodKey }: DocumentListSummaryStripProps): JSX.Element | null {
  const summary = useDocumentListSummary(periodKey);

  if (periodKey === null || summary.data === undefined) {
    return null;
  }

  const counters: readonly (readonly [string, string, number])[] = [
    ['draft', t(statusKey('sheet', 'Draft')), summary.data.draft],
    ['submitted', t(statusKey('sheet', 'Submitted')), summary.data.submitted],
    ['approved', t(statusKey('sheet', 'Approved')), summary.data.approved],
    ['withIssues', t('documents.summaryWithIssues'), summary.data.withIssues],
  ];

  return (
    <Group gap="xl" mb="md" role="group" aria-label={t('documents.summaryLabel')}>
      {counters.map(([id, label, count]) => (
        <Stack key={id} gap="xs" data-summary-counter={id}>
          <Text size="xl" fw={600}>
            {formatNumber(count)}
          </Text>
          <Text size="xs" c="dimmed">
            {label}
          </Text>
        </Stack>
      ))}
    </Group>
  );
}
