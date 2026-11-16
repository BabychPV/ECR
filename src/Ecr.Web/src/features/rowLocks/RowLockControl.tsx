import { lazy, Suspense, useState, type JSX } from 'react';
import { Button, VisuallyHidden } from '@mantine/core';
import type { TableSliceDto } from '@/api/types';
import type { RowLockDto } from '@/features/documents/rowLocks/api';
import { useFocusedCell } from '@/features/grid/focusStore';
import { useSelectedRange } from '@/features/grid/selectionStore';
import { rowDisplayName } from '@/features/permits/history/rowName';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { pickedRowKeys, type LockTable } from './rowLockModel';

// ⛔ D-132: діалог і перелік — окремі чанки, з першим відкриттям (сітка документа їх не тягне).
const RowLockDialog = lazy(() => import('./RowLockDialog'));
const RowLocksPanel = lazy(() => import('./RowLocksPanel'));

/** Право, з яким сервер приймає блокування й зняття будь-чийого блокування (`Document.RowLock`, B75). */
export const RowLockPermission = 'Document.RowLock';

interface RowLockControlProps {
  readonly documentId: number;
  readonly periodKey: number;
  readonly tableInstanceId: number;
  readonly table: LockTable;
  readonly slice: TableSliceDto;
  /** Блокування цієї таблиці (чинні й ще не початі). */
  readonly locks: readonly RowLockDto[];
}

/**
 * ✎ B139 (B75): дії блокування рядків у рядку кнопок сітки.
 *
 * - «Заблокувати рядки…» — лише з правом `Document.RowLock` (`/me`); діє на виділені рядки (діапазон або рядок
 *   фокусної комірки). Без виділення кнопка вимкнена з причиною (видно й читалці).
 * - «Заблоковані рядки: N» — перелік блокувань таблиці зі «Зняти»; лише коли блокування є.
 *
 * ⚠ Власник документа без права теж може блокувати (сервер, B75 «Lock»), але `/me` власності не знає — кнопки
 * для нього немає; спроба без права однаково дала б `403 rowLockDenied`.
 */
export function RowLockControl({ documentId, periodKey, tableInstanceId, table, slice, locks }: RowLockControlProps): JSX.Element | null {
  const session = useSession();
  const allowed = can(session.data, RowLockPermission);
  const focus = useFocusedCell(tableInstanceId, periodKey);
  const range = useSelectedRange(tableInstanceId, periodKey);
  const [open, setOpen] = useState<'lock' | 'list' | null>(null);

  if (!allowed && locks.length === 0) return null;

  const picked = pickedRowKeys(slice.rows, range, focus?.rowIndex ?? null);
  const hintId = `row-lock-hint-${String(tableInstanceId)}`;
  const rowName = (rowKey: string): string => rowDisplayName(slice, rowKey);

  return (
    <>
      {allowed && (
        <Button
          variant="default"
          disabled={picked.length === 0}
          {...(picked.length === 0 ? { title: t('rowLock.pickHint'), 'aria-describedby': hintId } : {})}
          onClick={() => {
            if (picked.length > 0) setOpen('lock');
          }}
          data-row-lock-open
        >
          {t('rowLock.lockAction')}
        </Button>
      )}
      {allowed && picked.length === 0 && <VisuallyHidden id={hintId}>{t('rowLock.pickHint')}</VisuallyHidden>}

      {locks.length > 0 && (
        <Button variant="subtle" onClick={() => setOpen('list')} data-row-locks-open data-row-locks-count={locks.length}>
          {t('rowLock.listAction', { count: locks.length })}
        </Button>
      )}

      <Suspense fallback={null}>
        {open === 'lock' && (
          <RowLockDialog
            documentId={documentId}
            periodKey={periodKey}
            table={table}
            rows={picked.map((rowKey) => ({ rowKey, label: rowName(rowKey) }))}
            onClose={() => setOpen(null)}
          />
        )}
        {open === 'list' && (
          <RowLocksPanel
            documentId={documentId}
            periodKey={periodKey}
            locks={locks}
            rowName={rowName}
            onClose={() => setOpen(null)}
          />
        )}
      </Suspense>
    </>
  );
}
