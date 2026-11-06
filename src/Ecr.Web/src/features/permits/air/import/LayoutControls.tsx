import { useState, type JSX } from 'react';
import { Button, NumberInput, Select, Text, TextInput } from '@mantine/core';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import type { AirAnalysis } from '../airTypes';
import { NewColumnKinds, suggestedName, type NewColumnKind, type NewColumnLevel, type NewColumnRequest } from './addColumn';

export interface HeaderRows {
  readonly headerFrom: number;
  readonly headerTo: number;
  readonly dataFrom: number;
  /**
   * Worksheet to read (`spec.sheet`); absent — the sheet is not chosen (a one-sheet workbook). With `rowsAuto` the rows are
   * not sent: another sheet has its own header, which the server finds by itself.
   */
  readonly sheet?: string;
  readonly rowsAuto?: boolean;
}

/** Rows go in order: header start ≤ header end < data start, all ≥ 1. */
export function headerRowsValid(rows: HeaderRows): boolean {
  return rows.headerFrom >= 1 && rows.headerFrom <= rows.headerTo && rows.headerTo < rows.dataFrom;
}

function whole(value: string | number): number {
  const n = typeof value === 'number' ? value : Number.parseInt(value, 10);

  return Number.isFinite(n) ? Math.trunc(n) : 0;
}

/**
 * «Where the table is in the file»: the header block and the first data row, found automatically by the server. The user
 * changes the numbers when the file has extra rows at the top and re-reads the file with them.
 */
export function HeaderRowsForm({
  detected,
  sheets,
  sheet: readSheet,
  onApply,
  busy,
  disabled,
}: {
  readonly detected: HeaderRows;
  /** Visible sheets of the workbook; the choice is shown only when there is more than one. */
  readonly sheets: readonly string[];
  /** The sheet the server read. */
  readonly sheet: string;
  readonly onApply: (rows: HeaderRows) => void;
  readonly busy: boolean;
  readonly disabled: boolean;
}): JSX.Element {
  const [rows, setRows] = useState<HeaderRows>(detected);
  const [sheet, setSheet] = useState(readSheet);
  const [touched, setTouched] = useState(false);
  const valid = headerRowsValid(rows);
  const choose = sheets.length > 1;
  const sameRows = rows.headerFrom === detected.headerFrom && rows.headerTo === detected.headerTo && rows.dataFrom === detected.dataFrom;
  // Another sheet with the rows left as found on this one: the server finds the header of that sheet itself.
  const rowsAuto = choose && sheet !== readSheet && sameRows;

  return (
    <form
      className="pi-panel"
      aria-label={t('permits.air.import.rows.title')}
      onSubmit={(event) => {
        event.preventDefault();
        setTouched(true);
        if ((valid || rowsAuto) && !busy) onApply(choose ? { ...rows, sheet, rowsAuto } : rows);
      }}
    >
      <Text fw={600}>{t('permits.air.import.rows.title')}</Text>
      <Text size="sm" c="dimmed">
        {t('permits.air.import.rows.hint')}
      </Text>
      <div className="pi-grid">
        {choose && (
          <Select
            label={t('permits.air.import.rows.sheet')}
            allowDeselect={false}
            data={[...sheets]}
            value={sheet}
            onChange={(value) => setSheet(value ?? readSheet)}
          />
        )}
        <NumberInput
          label={t('permits.air.import.rows.headerFrom')}
          min={1}
          allowDecimal={false}
          value={rows.headerFrom}
          onChange={(value) => setRows({ ...rows, headerFrom: whole(value) })}
        />
        <NumberInput
          label={t('permits.air.import.rows.headerTo')}
          min={1}
          allowDecimal={false}
          value={rows.headerTo}
          onChange={(value) => setRows({ ...rows, headerTo: whole(value) })}
        />
        <NumberInput
          label={t('permits.air.import.rows.dataFrom')}
          min={2}
          allowDecimal={false}
          value={rows.dataFrom}
          onChange={(value) => setRows({ ...rows, dataFrom: whole(value) })}
        />
      </div>
      <div className="pi-actions">
        <Button type="submit" variant="default" loading={busy} disabled={disabled}>
          {t('permits.air.import.rows.apply')}
        </Button>
      </div>
      {touched && !valid && !rowsAuto && (
        <Text size="sm" c="statusError" role="alert">
          {t('permits.air.import.rows.invalid')}
        </Text>
      )}
    </form>
  );
}

type Unmapped = NonNullable<AirAnalysis['analysis']['header']>['unmapped'][number];

function kindLabel(kind: NewColumnKind): string {
  switch (kind) {
    case 'text':
      return t('permits.air.import.add.kind.text');
    case 'number':
      return t('permits.air.import.add.kind.number');
    case 'whole':
      return t('permits.air.import.add.kind.whole');
    case 'date':
      return t('permits.air.import.add.kind.date');
  }
}

function AddColumnForm({
  column,
  onAdd,
  onCancel,
  busy,
}: {
  readonly column: Unmapped;
  readonly onAdd: (request: NewColumnRequest) => void;
  readonly onCancel: () => void;
  readonly busy: boolean;
}): JSX.Element {
  const [name, setName] = useState(suggestedName(column.headerText));
  const [kind, setKind] = useState<NewColumnKind>('text');
  const [level, setLevel] = useState<NewColumnLevel>('Parent');
  const [touched, setTouched] = useState(false);
  const missing = name.trim() === '';

  return (
    <form
      className="pi-panel"
      aria-label={t('permits.air.import.add.title', { column: column.columnLetter })}
      onSubmit={(event) => {
        event.preventDefault();
        setTouched(true);
        if (!missing && !busy) onAdd({ letter: column.columnLetter, name: name.trim(), kind, level });
      }}
    >
      <Text fw={600}>{t('permits.air.import.add.title', { column: column.columnLetter })}</Text>
      <div className="pi-grid">
        <TextInput
          label={t('permits.air.import.add.name')}
          value={name}
          error={touched && missing ? t('permits.air.import.add.nameRequired') : null}
          onChange={(event) => setName(event.currentTarget.value)}
        />
        <Select
          label={t('permits.air.import.add.kind')}
          allowDeselect={false}
          data={NewColumnKinds.map((value) => ({ value, label: kindLabel(value) }))}
          value={kind}
          onChange={(value) => setKind(NewColumnKinds.find((option) => option === value) ?? 'text')}
        />
        <Select
          label={t('permits.air.import.add.level')}
          allowDeselect={false}
          data={[
            { value: 'Parent', label: t('permits.air.import.add.level.parent') },
            { value: 'Child', label: t('permits.air.import.add.level.child') },
          ]}
          value={level}
          onChange={(value) => setLevel(value === 'Child' ? 'Child' : 'Parent')}
        />
      </div>
      <div className="pi-actions">
        <Button variant="default" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button type="submit" loading={busy}>
          {t('permits.air.import.add.submit')}
        </Button>
      </div>
    </form>
  );
}

/**
 * File columns no field reads: listed with a plain hint; with the right to change the permit type each one can be added as a
 * new column of the permit table («Add as new column»).
 */
export function UnmappedColumns({
  columns,
  mayAdd,
  onAdd,
  busy,
}: {
  readonly columns: readonly Unmapped[];
  readonly mayAdd: boolean;
  readonly onAdd: (request: NewColumnRequest) => void;
  readonly busy: boolean;
}): JSX.Element | null {
  const [open, setOpen] = useState<string | null>(null);
  if (columns.length === 0) return null;

  return (
    <details className="pi-panel" open={open !== null ? true : undefined}>
      <summary>{t('permits.air.import.map.unmapped', { count: formatNumber(columns.length) })}</summary>
      <Text size="sm" c="dimmed">
        {t('permits.air.import.map.unmappedHint')}
      </Text>
      <ul className="pi-unmapped">
        {columns.map((column) => (
          <li key={column.column}>
            <span className="pa-code">{column.columnLetter}</span>
            <span>
              {column.headerText}
              {column.hasData ? ` · ${t('permits.air.import.map.hasData')}` : ''}
            </span>
            {mayAdd && open !== column.columnLetter ? (
              <Button
                variant="default"
                size="xs"
                aria-label={t('permits.air.import.add.buttonFor', { column: column.columnLetter })}
                onClick={() => setOpen(column.columnLetter)}
              >
                {t('permits.air.import.add.button')}
              </Button>
            ) : (
              <span />
            )}
            {open === column.columnLetter && (
              <AddColumnForm
                column={column}
                busy={busy}
                onCancel={() => setOpen(null)}
                onAdd={(request) => {
                  onAdd(request);
                }}
              />
            )}
          </li>
        ))}
      </ul>
    </details>
  );
}
