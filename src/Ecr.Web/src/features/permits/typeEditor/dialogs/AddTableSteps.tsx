import { useState, type ChangeEvent, type JSX } from 'react';
import { Button, Group, Input, Select, Stack, Text, TextInput } from '@mantine/core';
import type { PermitType } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ColumnFields } from './ColumnFields';
import { draftColumnsFromCsv, parseCsvPreview } from './csvHeader';
import { findMonthUnit, pickName, type UnitOption } from './names';
import {
  MonthGroupCode,
  buildMonthColumns,
  codeFromName,
  draftFromColumn,
  emptyDraftColumn,
  type DraftColumn,
} from './ops';

/** Звідки беруться стовпці нової таблиці. */
export type TableSource = 'blank' | 'csv' | 'copy';

/** Дані майстра «Add a table»; живуть у `Wizard`, не в адресі. */
export interface TableWizardData {
  readonly nameEn: string;
  readonly nameRu: string;
  readonly from: TableSource;

  /** Код таблиці, яку копіюють (`from === 'copy'`). */
  readonly copyCode: string;

  /** Ім'я прочитаного CSV; `null` — файл ще не обрано. */
  readonly fileName: string | null;
  readonly columns: readonly DraftColumn[];
}

/** Початкові дані: один порожній ключовий стовпець «Name» (як у мокапі). */
export function initialTableData(): TableWizardData {
  return {
    nameEn: '',
    nameRu: '',
    from: 'blank',
    copyCode: '',
    fileName: null,
    columns: [defaultColumn()],
  };
}

function defaultColumn(): DraftColumn {
  return emptyDraftColumn({
    name: t('permits.editor.dialogs.addTable.defaultColumn'),
    key: true,
    required: true,
  });
}

/** Текст файлу; `Blob.text()` бракує старішим рушіям (і частині jsdom). */
function readFileText(file: File): Promise<string> {
  if (typeof file.text === 'function') return file.text();

  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      resolve(typeof reader.result === 'string' ? reader.result : '');
    };
    reader.onerror = () => {
      reject(reader.error ?? new Error('read'));
    };
    reader.readAsText(file);
  });
}

interface StepProps {
  readonly data: TableWizardData;
  readonly update: (patch: Partial<TableWizardData>) => void;
}

/** Колонки нової таблиці як копія стовпців наявної. */
function copiedColumns(type: PermitType, tableCode: string): DraftColumn[] {
  const source = type.tables.find((table) => table.code === tableCode);

  return source === undefined ? [defaultColumn()] : source.columns.map(draftFromColumn);
}

/** Крок 1 «Table»: назви й спосіб почати. */
export function TableBasicsStep({
  data,
  update,
  type,
}: StepProps & { readonly type: PermitType }): JSX.Element {
  const [readError, setReadError] = useState<string | null>(null);
  const hasTables = type.tables.length > 0;

  function chooseSource(next: TableSource): void {
    setReadError(null);

    if (next === 'blank') {
      update({ from: next, fileName: null, columns: [defaultColumn()] });
    } else if (next === 'copy') {
      const copyCode = data.copyCode !== '' ? data.copyCode : (type.tables[0]?.code ?? '');
      update({ from: next, fileName: null, copyCode, columns: copiedColumns(type, copyCode) });
    } else {
      update({ from: next, fileName: null, columns: [] });
    }
  }

  async function readFile(file: File | null): Promise<void> {
    if (file === null) return;

    const preview = parseCsvPreview(await readFileText(file));
    if (preview.headers.length === 0) {
      setReadError(t('permits.editor.dialogs.addTable.csvEmpty', { file: file.name }));
      update({ fileName: null, columns: [] });

      return;
    }

    setReadError(null);
    update({ fileName: file.name, columns: draftColumnsFromCsv(preview) });
  }

  return (
    <Stack gap="sm">
      <TextInput
        id="pm-nt-name"
        label={t('permits.editor.dialogs.addTable.nameEn')}
        placeholder={t('permits.editor.dialogs.addTable.namePlaceholder')}
        required
        value={data.nameEn}
        onChange={(event) => {
          update({ nameEn: event.currentTarget.value });
        }}
      />

      <TextInput
        id="pm-nt-ru"
        label={t('permits.editor.dialogs.addTable.nameRu')}
        value={data.nameRu}
        onChange={(event) => {
          update({ nameRu: event.currentTarget.value });
        }}
      />

      <Select
        id="pm-nt-from"
        label={t('permits.editor.dialogs.addTable.from')}
        value={data.from}
        allowDeselect={false}
        data={[
          { value: 'blank', label: t('permits.editor.dialogs.addTable.fromBlank') },
          { value: 'csv', label: t('permits.editor.dialogs.addTable.fromCsv') },
          {
            value: 'copy',
            label: t('permits.editor.dialogs.addTable.fromCopy'),
            disabled: !hasTables,
          },
        ]}
        onChange={(next) => {
          if (next === 'blank' || next === 'csv' || next === 'copy') chooseSource(next);
        }}
      />

      {data.from === 'copy' ? (
        <Select
          id="pm-nt-copy"
          label={t('permits.editor.dialogs.addTable.copyTable')}
          value={data.copyCode}
          allowDeselect={false}
          data={type.tables.map((table) => ({ value: table.code, label: pickName(table.nameL10n) }))}
          onChange={(next) => {
            if (next !== null) update({ copyCode: next, columns: copiedColumns(type, next) });
          }}
        />
      ) : null}

      {data.from === 'csv' ? (
        <Input.Wrapper
          id="pm-nt-file"
          label={t('permits.editor.dialogs.addTable.file')}
          description={t('permits.editor.dialogs.addTable.fileHint')}
          error={readError ?? undefined}
        >
          <Input
            id="pm-nt-file"
            type="file"
            accept=".csv,.txt,text/csv,text/plain"
            onChange={(event: ChangeEvent<HTMLInputElement>) => {
              void readFile(event.currentTarget.files?.item(0) ?? null);
            }}
          />
          {data.fileName === null ? null : (
            <Text size="xs" c="dimmed" mt="xs" role="status">
              {t('permits.editor.dialogs.addTable.fileRead', {
                file: data.fileName,
                count: data.columns.length,
              })}
            </Text>
          )}
        </Input.Wrapper>
      ) : null}
    </Stack>
  );
}

/** Крок 2 «Columns»: рядки стовпців, «Add column», «Add months January to December». */
export function TableColumnsStep({
  data,
  update,
  units,
}: StepProps & { readonly units: readonly UnitOption[] }): JSX.Element {
  const columns = data.columns;

  function patchColumn(id: string, patch: Partial<DraftColumn>): void {
    update({ columns: columns.map((column) => (column.id === id ? { ...column, ...patch } : column)) });
  }

  function addMonths(): void {
    const taken = columns.map((column) => column.base?.code ?? codeFromName(column.name, 'snake', 'Column'));
    const months = buildMonthColumns({
      unitId: findMonthUnit(units)?.id ?? null,
      group: MonthGroupCode,
      existingCodes: taken,
      startOrder: columns.length + 1,
    });
    update({ columns: [...columns, ...months.map(draftFromColumn)] });
  }

  return (
    <Stack gap="sm">
      {data.from === 'csv' && data.fileName !== null ? (
        <Banner
          tone="info"
          title={t('permits.editor.dialogs.addTable.csvTitle', { file: data.fileName })}
          text={t('permits.editor.dialogs.addTable.csvText', { count: columns.length })}
        />
      ) : null}

      {columns.map((column, index) => (
        <ColumnFields
          key={column.id}
          idPrefix={`pm-nt-c${String(index)}`}
          nameLabel={t('permits.editor.dialogs.addTable.columnN', { n: index + 1 })}
          value={column}
          units={units}
          showKey
          removeDisabled={columns.length < 2}
          onChange={(patch) => {
            patchColumn(column.id, patch);
          }}
          onRemove={() => {
            update({ columns: columns.filter((entry) => entry.id !== column.id) });
          }}
        />
      ))}

      <Group gap="xs">
        <Button
          variant="default"
          onClick={() => {
            update({ columns: [...columns, emptyDraftColumn()] });
          }}
        >
          {t('permits.editor.dialogs.addTable.addColumn')}
        </Button>
        <Button variant="default" onClick={addMonths}>
          {t('permits.editor.dialogs.addTable.addMonths')}
        </Button>
      </Group>
    </Stack>
  );
}
