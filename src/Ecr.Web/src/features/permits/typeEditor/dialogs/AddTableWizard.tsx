import { useMemo, useState, type JSX } from 'react';
import type { PermitType, PermitTypeTable } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { KeyValue } from '@/shared/ui/KeyValue';
import { Wizard, type WizardStep } from '@/shared/ui/Wizard';
import {
  TableBasicsStep,
  TableColumnsStep,
  initialTableData,
  type TableWizardData,
} from './AddTableSteps';
import { pickName, type UnitOption } from './names';
import {
  buildNewTable,
  keyColumnNames,
  newTableCode,
  tableRegistryCode,
  validateColumns,
} from './ops';

/**
 * Майстер «Add a table» у три кроки (мокап `add-table`; Permits, PM-08c): «Table» — назви й
 * спосіб почати, «Columns» — стовпці, «Review» (додає `Wizard`) — підсумок.
 *
 * Самодостатній: нічого не читає з мережі; тип і одиниці приходять пропами, результат — готова
 * таблиця запиту (`onApply`), яку хост кладе в тип (`applyOps.withTable`).
 *
 * ⚠ Початок з файлу — лише CSV (у репозиторії немає читача `.xlsx`, а залежність додавати не можна):
 * Excel зберігають як CSV, підпис пункту це каже.
 * ⛔ Вихід із введеним — через підтвердження «Discard», а не мовчки (`onExitUnsaved` — обов'язковий
 * проп `Wizard`).
 */
export interface AddTableWizardProps {
  readonly opened: boolean;
  readonly type: PermitType;
  readonly units: readonly UnitOption[];
  readonly onApply: (table: PermitTypeTable) => void;
  readonly onClose: () => void;
}

export function AddTableWizard({
  opened,
  type,
  units,
  onApply,
  onClose,
}: AddTableWizardProps): JSX.Element {
  const [discarding, setDiscarding] = useState(false);
  const typeName = pickName(type.nameL10n);
  const tableCodes = useMemo(() => type.tables.map((table) => table.code), [type.tables]);

  const steps: readonly WizardStep<TableWizardData>[] = [
    {
      id: 'table',
      label: t('permits.editor.dialogs.addTable.stepTable'),
      hint: t('permits.editor.dialogs.addTable.stepTableHint'),
      render: ({ data, update }) => <TableBasicsStep data={data} update={update} type={type} />,
      validate: (data) => {
        const name = data.nameEn.trim();
        if (name.length === 0) {
          return { message: t('permits.editor.dialogs.addTable.nameRequired'), focus: '#pm-nt-name' };
        }

        const lower = name.toLowerCase();
        if (type.tables.some((table) => pickName(table.nameL10n).toLowerCase() === lower)) {
          return { message: t('permits.editor.dialogs.addTable.nameTaken'), focus: '#pm-nt-name' };
        }

        if (data.from === 'csv' && data.columns.length === 0) {
          return { message: t('permits.editor.dialogs.addTable.fileRequired'), focus: '#pm-nt-file' };
        }

        return null;
      },
    },
    {
      id: 'columns',
      label: t('permits.editor.dialogs.addTable.stepColumns'),
      hint: t('permits.editor.dialogs.addTable.stepColumnsHint'),
      render: ({ data, update }) => <TableColumnsStep data={data} update={update} units={units} />,
      validate: (data) => {
        const problem = validateColumns(data.columns);
        if (problem === null) return null;

        return problem.kind === 'unnamed'
          ? {
              message: t('permits.editor.dialogs.addTable.columnUnnamed'),
              focus: `#pm-nt-c${String(problem.index)}-name`,
            }
          : { message: t('permits.editor.dialogs.addTable.noKey'), focus: '#pm-nt-c0-key' };
      },
    },
  ];

  return (
    <>
      <Wizard<TableWizardData>
        opened={opened}
        title={t('permits.editor.dialogs.addTable.title', { name: typeName })}
        wide
        initialData={initialTableData()}
        steps={steps}
        labels={{
          back: t('permits.editor.dialogs.wizard.back'),
          next: t('permits.editor.dialogs.wizard.next'),
          review: t('permits.editor.dialogs.wizard.review'),
        }}
        reviewText={t('permits.editor.dialogs.addTable.reviewText')}
        applyLabel={t('permits.editor.dialogs.addTable.apply')}
        summary={(data) => {
          const names = data.columns.map((column) => column.name.trim());
          const keys = keyColumnNames(data.columns);
          const code = newTableCode(type.code, data.nameEn, tableCodes);

          return (
            <KeyValue
              wide
              items={[
                { label: t('permits.editor.dialogs.addTable.sumTable'), value: data.nameEn.trim() },
                {
                  label: t('permits.editor.dialogs.addTable.sumColumns'),
                  value: `${String(names.length)}: ${names.slice(0, 5).join(', ')}${names.length > 5 ? '…' : ''}`,
                },
                {
                  label: t('permits.editor.dialogs.addTable.sumUnique'),
                  value: t('permits.editor.dialogs.addTable.uniqueRow', { keys: keys.join(' + ') }),
                },
                {
                  label: t('permits.editor.dialogs.addTable.sumStored'),
                  value: tableRegistryCode(type.code, code),
                  mono: true,
                },
              ]}
            />
          );
        }}
        onApply={(data, api) => {
          onApply(
            buildNewTable({
              typeCode: type.code,
              nameEn: data.nameEn,
              nameRu: data.nameRu,
              columns: data.columns,
              existingTableCodes: tableCodes,
            }),
          );
          api.close();
        }}
        onClose={onClose}
        onExitUnsaved={() => {
          setDiscarding(true);

          return false;
        }}
      />

      <ConfirmModal
        opened={discarding}
        title={t('permits.editor.dialogs.addTable.discardTitle')}
        text={t('permits.editor.dialogs.addTable.discardText')}
        verb={t('permits.editor.dialogs.addTable.discardVerb')}
        onConfirm={() => {
          setDiscarding(false);
          onClose();
        }}
        onClose={() => {
          setDiscarding(false);
        }}
      />
    </>
  );
}
