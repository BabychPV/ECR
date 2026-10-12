import { useState, type JSX } from 'react';
import { Button, VisuallyHidden } from '@mantine/core';
import type { TableSliceDto } from '@/api/types';
import { t } from '@/shared/i18n';
// ⛔ D-132: діалог вантажиться при першому відкритті, а не разом зі сторінкою документа (бюджет чанка).
import { LazyConfirmModal } from '@/features/workflow/lazyDialogs';
import { publishFocus, useFocusedCell } from './focusStore';
import { isDeletableRow, useDeleteRow } from './useDeleteRow';

interface DeleteRowControlProps {
  readonly documentId: number;
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly slice: TableSliceDto;

  /** Рядок видалено на сервері — зріз треба перечитати. */
  readonly onDeleted: () => void;
}

/** Людська мітка рядка для діалогу: підпис, інакше ключ; службовий GUID (32 hex) — «рядок №n». */
function rowLabel(row: {
  readonly label?: string | null | undefined;
  readonly rowKey: string;
  readonly ordinal: number;
}): string {
  if (row.label !== undefined && row.label !== null && row.label !== '') return row.label;
  if (/^[0-9a-f]{32}$/i.test(row.rowKey)) return t('grid.deleteRowOrdinalLabel', { n: row.ordinal });

  return row.rowKey;
}

/**
 * «Видалити рядок» динамічної таблиці (`D3-A`): діє на рядок фокусної комірки, підтвердження — `ConfirmDialog`
 * макета (`KIT.md` §6.9: назва об'єкта в заголовку, наслідки, дієслово на кнопці, фокус на «Скасувати»).
 *
 * ⛔ Кнопка вимкнена, доки в сітці немає фокуса на рядку, доданому користувачем (рядок шаблону сервер однаково
 * відхилить). Причини для видалення не питаємо: рішення D3-A — лише підтвердження.
 */
export function DeleteRowControl(props: DeleteRowControlProps): JSX.Element {
  const { documentId, tableInstanceId, periodKey, slice, onDeleted } = props;

  const focus = useFocusedCell(tableInstanceId, periodKey);
  const row = focus === null ? undefined : slice.rows[focus.rowIndex];
  const deletable = isDeletableRow(row);
  const hintId = `delete-row-hint-${String(tableInstanceId)}`;

  const [target, setTarget] = useState<{ readonly rowKey: string; readonly label: string } | null>(null);

  const remove = useDeleteRow(documentId, tableInstanceId, periodKey, () => {
    // Фокус стояв на видаленому рядку; після перечитання там опинився б сусідній.
    publishFocus(tableInstanceId, periodKey, null);
    onDeleted();
  });

  return (
    <>
      <Button
        variant="default"
        disabled={!deletable}
        // ⚠ Пояснення не лише `title` (його не бачать із клавіатури й читалки): прихований текст за `aria-describedby`.
        {...(deletable ? {} : { title: t('grid.deleteRowHint'), 'aria-describedby': hintId })}
        onClick={() => {
          if (row === undefined || !deletable) return;
          setTarget({ rowKey: row.rowKey, label: rowLabel(row) });
        }}
      >
        {t('grid.deleteRow')}
      </Button>
      {!deletable && <VisuallyHidden id={hintId}>{t('grid.deleteRowHint')}</VisuallyHidden>}

      <LazyConfirmModal
        opened={target !== null}
        title={t('grid.deleteRowTitle', { row: target?.label ?? '' })}
        consequences={[t('grid.deleteRowConsequence'), { text: t('grid.deleteRowNote'), note: true }]}
        verb={t('grid.deleteRowVerb')}
        isPending={remove.isPending}
        onConfirm={() => {
          if (target === null || remove.isPending) return;
          remove.mutate(target.rowKey, { onSettled: () => setTarget(null) });
        }}
        onClose={() => {
          if (!remove.isPending) setTarget(null);
        }}
      />
    </>
  );
}
