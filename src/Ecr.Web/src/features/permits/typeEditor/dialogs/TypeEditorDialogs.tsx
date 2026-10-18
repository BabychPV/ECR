import type { JSX } from 'react';
import type { PermitType } from '@/features/permits/api/types';
import { withRule } from '../checks/ruleOps';
import { useUrlState } from '@/shared/ui/useUrlState';
import { AddColumnDialog } from './AddColumnDialog';
import { AddRuleDialog } from './AddRuleDialog';
import { AddSeriesConfirm } from './AddSeriesConfirm';
import { AddTableWizard } from './AddTableWizard';
import { DeleteTypeConfirm } from './DeleteTypeConfirm';
import { NewVersionConfirm } from './NewVersionConfirm';
import { PublishTypeDialog } from './PublishTypeDialog';
import { withColumns, withTable } from './applyOps';
import { findMonthUnit } from './names';
import { useTypeLifecycle } from './useTypeLifecycle';
import { useUnitOptions } from './useUnitOptions';

/** Ідентифікатори діалогів у `?dialog=` (ті самі, що в мокапі). */
export const TypeEditorDialogIds = {
  addTable: 'add-table',
  addColumn: 'add-column',
  addSeries: 'add-series',
  addRule: 'add-rule',
  publish: 'publish-type',
  newVersion: 'new-version',
  deleteType: 'delete-type',
} as const;

/**
 * Хост діалогів конструктора типу дозволу (Permits, PM-08c): читає `?dialog=<id>` і показує
 * відповідний діалог. Діалоги самодостатні; хост лише зводить їх зі станом сторінки.
 *
 * Контракт зі сторінкою редактора:
 * - `onChangeType(next)` — нове визначення ПІСЛЯ локальної правки (таблиця, стовпці, перевірка);
 *   сторінка кладе його у свою чернетку. Сервер тут не викликається.
 * - `onTypeReplaced(next)` — відповідь сервера після публікації / відкриття нової версії.
 * - `onReload()` — `409 ECR-PRM-4093`: визначення змінили, його треба перечитати.
 * - `onDeleted()` — тип видалено, сторінку треба покинути.
 * - `onSelectTable(code)` — щойно додану таблицю варто вибрати в дереві.
 *
 * ⚠ `onSelectTable` викликається в НАСТУПНОМУ такті (`setTimeout 0`) після закриття діалогу:
 * `setSearchParams` у react-router, викликаний двічі в одному такті, губить ОБИДВІ зміни
 * (`useUrlState`: UI-аудит, lane 3), а закриття діалогу — теж зміна адреси.
 */
export interface TypeEditorDialogsProps {
  readonly type: PermitType;

  /** Вибрана в дереві таблиця (для «Add column» / «Add 12 months»); `null` — вибрано шапку. */
  readonly selectedTableCode: string | null;

  /** Кількість наявних дозволів типу, якщо сторінка її знає (рядок переліку). */
  readonly permitCount?: number | undefined;
  readonly onChangeType: (next: PermitType) => void;
  readonly onTypeReplaced?: ((next: PermitType) => void) | undefined;
  readonly onReload?: (() => void) | undefined;
  readonly onDeleted?: (() => void) | undefined;
  readonly onSelectTable?: ((code: string) => void) | undefined;
}

export function TypeEditorDialogs({
  type,
  selectedTableCode,
  permitCount,
  onChangeType,
  onTypeReplaced,
  onReload,
  onDeleted,
  onSelectTable,
}: TypeEditorDialogsProps): JSX.Element | null {
  const [dialog, setDialog] = useUrlState('dialog');
  const close = (): void => {
    setDialog(null);
  };

  const needsUnits =
    dialog === TypeEditorDialogIds.addTable ||
    dialog === TypeEditorDialogIds.addColumn ||
    dialog === TypeEditorDialogIds.addSeries;
  const { units, isPending: unitsPending } = useUnitOptions(needsUnits);

  const lifecycle = useTypeLifecycle(type, {
    onPublished: (next) => {
      close();
      onTypeReplaced?.(next);
    },
    onVersionOpened: (next) => {
      close();
      onTypeReplaced?.(next);
    },
    onDeleted: () => {
      close();
      onDeleted?.();
    },
    onConflict: () => {
      close();
      onReload?.();
    },
  });

  const table = type.tables.find((entry) => entry.code === selectedTableCode);

  switch (dialog) {
    case TypeEditorDialogIds.addTable:
      return (
        <AddTableWizard
          opened
          type={type}
          units={units}
          onApply={(added) => {
            // ⚠ Закриває сам майстер (`api.close` → `onClose`): другий `close()` у тому ж такті
            // губив би обидві зміни адреси.
            onChangeType(withTable(type, added));
            if (onSelectTable !== undefined) {
              setTimeout(() => {
                onSelectTable(added.code);
              }, 0);
            }
          }}
          onClose={close}
        />
      );

    case TypeEditorDialogIds.addColumn:
      return table === undefined ? null : (
        <AddColumnDialog
          opened
          tableCode={table.code}
          existingCodes={table.columns.map((column) => column.code)}
          units={units}
          onApply={(column) => {
            onChangeType(withColumns(type, table.code, [column]));
          }}
          onClose={close}
        />
      );

    case TypeEditorDialogIds.addSeries:
      return table === undefined || unitsPending ? null : (
        <AddSeriesConfirm
          opened
          tableCode={table.code}
          existingCodes={table.columns.map((column) => column.code)}
          unit={findMonthUnit(units)}
          onApply={(columns) => {
            onChangeType(withColumns(type, table.code, columns));
          }}
          onClose={close}
        />
      );

    case TypeEditorDialogIds.addRule:
      return (
        <AddRuleDialog
          opened
          type={type}
          defaultTableCode={selectedTableCode}
          onApply={(rule, where) => {
            onChangeType(withRule(type, rule, where));
          }}
          onClose={close}
        />
      );

    case TypeEditorDialogIds.publish:
      return (
        <PublishTypeDialog
          opened
          type={type}
          permitCount={permitCount}
          isPending={lifecycle.publish.isPending}
          onSubmit={(reason) => {
            lifecycle.publish.mutate({ reason, rowVersion: type.rowVersion });
          }}
          onAddTable={() => {
            setDialog(TypeEditorDialogIds.addTable);
          }}
          onClose={close}
        />
      );

    case TypeEditorDialogIds.newVersion:
      return (
        <NewVersionConfirm
          opened
          type={type}
          isPending={lifecycle.openVersion.isPending}
          onConfirm={() => {
            lifecycle.openVersion.mutate();
          }}
          onClose={close}
        />
      );

    case TypeEditorDialogIds.deleteType:
      return (
        <DeleteTypeConfirm
          opened
          type={type}
          isPending={lifecycle.remove.isPending}
          onConfirm={() => {
            lifecycle.remove.mutate();
          }}
          onClose={close}
        />
      );

    default:
      return null;
  }
}
