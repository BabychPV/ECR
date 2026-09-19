import type { JSX } from 'react';
import { Group, Stack, Text } from '@mantine/core';
import { useDocumentListSummary } from '@/features/documents/api';
import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';
import { statusKey, statusTone, toneFills, type StatusTone } from '@/shared/ui/StatusBadge';

/** За який період зводити; `null` — період не обрано. */
export interface DocumentListSummaryStripProps {
  readonly periodKey: number | null;
}

/** Лічильник смуги: ідентифікатор, підпис, число, тон (`null` — без кольору). */
type Counter = readonly [string, string, number, StatusTone | null];

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

  /*
   * ⛔ Rejected — лише коли відхилені Є (рішення людини, 2026-09-19). У
   * спокійному стані смуга тримає чотири числа без кольору; «0 відхилено»
   * червоним привчало б не дивитися на червоне. Тон — той самий, яким
   * `<StatusBadge>` малює `sheet/Rejected`, з тієї ж таблиці, не власний.
   */
  const rejected: readonly Counter[] =
    summary.data.rejected > 0
      ? [['rejected', t(statusKey('sheet', 'Rejected')), summary.data.rejected, statusTone('sheet', 'Rejected')]]
      : [];

  const counters: readonly Counter[] = [
    ['draft', t(statusKey('sheet', 'Draft')), summary.data.draft, null],
    ['submitted', t(statusKey('sheet', 'Submitted')), summary.data.submitted, null],
    ...rejected,
    ['approved', t(statusKey('sheet', 'Approved')), summary.data.approved, null],
    ['withIssues', t('documents.summaryWithIssues'), summary.data.withIssues, null],
  ];

  return (
    <Group gap="xl" mb="md" role="group" aria-label={t('documents.summaryLabel')}>
      {counters.map(([id, label, count, tone]) => (
        <Stack key={id} gap="xs" data-summary-counter={id} data-summary-tone={tone ?? undefined}>
          <Text size="xl" fw={600} {...(tone === null ? {} : { c: toneFills[tone].text })}>
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
