import type { JSX } from 'react';
import { Badge, Group, Text } from '@mantine/core';
import { summarize, useTableStatus } from '@/features/documents/api';
import { t } from '@/shared/i18n';

/** Який документ і за який період підсумовувати. */
export interface SheetFillSummaryProps {
  /** Документ. */
  documentId: number;

  /** Період; екземпляри таблиць існують окремо на кожен (`R-A6`). */
  periodKey: number;
}

/**
 * «Tables filled completely: 68 of 91» над деревом аркушів і крапка помилки
 * (`BE-10`).
 *
 * ✎ `U-06`: до цього коміту тут стояв ГОЛИЙ дріб, без жодного слова, і
 * причина була процесна — новий ключ каталогу означав би правку
 * `09-seed.sql`, яку тоді тримав інший відкритий PR. Причина відпала, а
 * борг лишався, і коштував він рівно того, чого й мав: на документі,
 * заповненому на ~90 %, головний показник читався як «не введено нічого».
 * Дріб рахує ТАБЛИЦІ, ЗАПОВНЕНІ ПОВНІСТЮ, — таблиця з 710 заповненими
 * комірками з 774 у чисельник не потрапляє взагалі, і без цих слів «0 / 91»
 * не можна прочитати правильно навіть здогадом. Тому підпис називає не
 * «заповненість», а саме те, що рахується: `document.tablesFilled`.
 *
 * ⛔ Крапка помилки НЕ малюється, доки документ не перевіряли. Сірий стан і
 * зелений — різні відповіді: «не знаємо» і «порушень немає». Показати друге
 * замість першого — та сама неправда, що й `A7-28`.
 */
export function SheetFillSummary({ documentId, periodKey }: SheetFillSummaryProps): JSX.Element | null {
  const status = useTableStatus(documentId, periodKey);

  if (status.data === undefined) {
    return null;
  }

  const summary = summarize(status.data);

  return (
    <Group gap="xs" data-testid="sheet-fill-summary">
      <Text size="sm" fw={600} data-testid="sheet-fill-count">
        {t('document.tablesFilled', { filled: summary.filled, total: summary.total })}
      </Text>

      {summary.hasErrors === true && (
        <Badge size="xs" circle color="statusError" data-testid="sheet-fill-error-dot">
          !
        </Badge>
      )}
    </Group>
  );
}
