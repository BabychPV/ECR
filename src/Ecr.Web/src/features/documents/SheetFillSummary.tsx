import type { JSX } from 'react';
import { Badge, Group, Text } from '@mantine/core';
import { summarize, useTableStatus } from '@/features/documents/api';

/** Який документ і за який період підсумовувати. */
export interface SheetFillSummaryProps {
  /** Документ. */
  documentId: number;

  /** Період; екземпляри таблиць існують окремо на кожен (`R-A6`). */
  periodKey: number;
}

/**
 * «68 / 91» над деревом аркушів і крапка помилки (`BE-10`).
 *
 * ⚠ Підпис навмисно ЧИСЛОВИЙ, без жодного слова. Рядки інтерфейсу живуть у
 * каталозі (`ui.*` у сіді), а новий ключ означав би правку
 * `09-seed.sql` — файла, який зараз змінює інший відкритий PR. Прозовий
 * підпис («68 of 91 tables filled») приходить разом із деревом аркушів у
 * `DIRECTIVE-15-FRONTEND.md`; дріб читається й без нього, а вигадувати
 * ключ, якого немає в каталозі, означало б показати `⟦document.tablesFilled⟧`.
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
        {summary.filled} / {summary.total}
      </Text>

      {summary.hasErrors === true && (
        <Badge size="xs" circle color="statusError" data-testid="sheet-fill-error-dot">
          !
        </Badge>
      )}
    </Group>
  );
}
