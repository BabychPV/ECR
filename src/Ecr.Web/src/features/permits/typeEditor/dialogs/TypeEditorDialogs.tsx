import { useState, type JSX } from 'react';
import type { PermitType } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { showApiError, showWarning } from '@/shared/ui/notify';
import { useUrlState } from '@/shared/ui/useUrlState';
import { addColumn, addRule, addTable, appendColumns, type TypeDraft } from '../draftModel';
import { AddColumnDialog } from './AddColumnDialog';
import { AddRuleDialog } from './AddRuleDialog';
import { AddSeriesConfirm } from './AddSeriesConfirm';
import { AddTableWizard } from './AddTableWizard';
import { DeleteTypeConfirm } from './DeleteTypeConfirm';
import { NewVersionConfirm } from './NewVersionConfirm';
import { PublishTypeDialog } from './PublishTypeDialog';
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
 * - `onChange(change)` — локальна правка (таблиця, стовпці, перевірка) як чиста функція над робочою
 *   копією (`draftModel`); сторінка застосовує її до своєї чернетки. Сервер тут не викликається.
 * - `settle()` — перед публікацією й новою версією: зберегти чернетку й повернути найсвіжіший тип
 *   сервера (його `rowVersion` іде в запит) або `null`, якщо зберегти не вдалося — тоді нічого не шлемо.
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
  readonly onChange: (change: (draft: TypeDraft) => TypeDraft) => void;
  readonly settle?: (() => Promise<PermitType | null>) | undefined;
  readonly onTypeReplaced?: ((next: PermitType) => void) | undefined;
  readonly onReload?: (() => void) | undefined;
  readonly onDeleted?: (() => void) | undefined;
  readonly onSelectTable?: ((code: string) => void) | undefined;
}

export function TypeEditorDialogs({
  type,
  selectedTableCode,
  permitCount,
  onChange,
  settle,
  onTypeReplaced,
  onReload,
  onDeleted,
  onSelectTable,
}: TypeEditorDialogsProps): JSX.Element | null {
  const [dialog, setDialog] = useUrlState('dialog');
  const [settling, setSettling] = useState(false);

  /** Спершу зберігає чернетку (`settle`), і лише з найсвіжішим типом шле запит; не збереглась — не шле. */
  const afterSettle = (send: (fresh: PermitType) => void): void => {
    setSettling(true);
    void (settle === undefined ? Promise.resolve(type) : settle())
      .then((fresh) => {
        if (fresh === null) showWarning(t('permits.editor.dialogs.unsaved'));
        else send(fresh);
      })
      .catch((error: unknown) => {
        showApiError(error);
      })
      .finally(() => {
        setSettling(false);
      });
  };
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
            onChange((draft) => addTable(draft, added));
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
            onChange((draft) => addColumn(draft, table.code, column));
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
            onChange((draft) => appendColumns(draft, table.code, columns));
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
            onChange((draft) => addRule(draft, rule, where));
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
          isPending={lifecycle.publish.isPending || settling}
          onSubmit={(reason) => {
            afterSettle((fresh) => {
              lifecycle.publish.mutate({ reason, rowVersion: fresh.rowVersion });
            });
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
          isPending={lifecycle.openVersion.isPending || settling}
          onConfirm={() => {
            afterSettle(() => {
              lifecycle.openVersion.mutate();
            });
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
