import type { JSX, ReactNode } from 'react';
import { Skeleton } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { columnDraftOf, type ColumnDraft } from './column';
import { columnKey, getColumn } from './columnApi';

/**
 * Завантажує НАЯВНУ колонку цілком і лише тоді віддає форму правки (X-02).
 *
 * ⛔ Форма не відкривається на неповних даних: доти вона стартувала з бідного
 * опису структури й попереджала «Saving will clear them» — тобто пропонувала
 * дію, яка мовчки стирала одиницю, точність, довідник і стиль. Порядок той
 * самий, що в `AsyncBoundary`: `error` → `isPending` → дані.
 *
 * ⚠ `gcTime: 0`: чернетка береться з відповіді один раз при монтуванні
 * (`LocalDraft`), і кешована з минулого відкриття відповідь підставила б у
 * форму значення, яких на сервері вже немає.
 */
export function ExistingColumn({
  templateVersionId,
  tableId,
  code,
  children,
}: {
  templateVersionId: number;
  tableId: number;
  code: string;
  children: (draft: ColumnDraft) => ReactNode;
}): JSX.Element {
  const column = useQuery({
    queryKey: columnKey(templateVersionId, tableId, code),
    queryFn: () => getColumn(templateVersionId, tableId, code),
    gcTime: 0,
  });

  if (column.error !== null) {
    return <ErrorAlert error={column.error} onRetry={() => void column.refetch()} />;
  }

  if (column.isPending) {
    return <Skeleton height={320} radius="sm" data-column-edit="pending" />;
  }

  return <>{children(columnDraftOf(column.data))}</>;
}
