import { useState, type JSX } from 'react';
import { Button } from '@mantine/core';
import type { TableSliceDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
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
        {...(deletable ? {} : { title: t('grid.deleteRowHint') })}
        onClick={() => {
          if (row === undefined || !deletable) return;
          const label = row.label ?? row.rowKey;
          setTarget({ rowKey: row.rowKey, label });
        }}
      >
        {t('grid.deleteRow')}
      </Button>

      <ConfirmModal
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
