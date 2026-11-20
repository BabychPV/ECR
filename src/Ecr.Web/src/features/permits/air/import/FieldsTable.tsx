import { useEffect, useId, useRef, useState, type JSX } from 'react';
import { Button, Select, Text, Title } from '@mantine/core';
import { serverMessage } from '@/features/registries/rc816/serverMessage';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import { columnOwners, type FieldRow, type FieldState, type FileColumn } from './fieldRows';
import { fieldLabel, type ColumnNames } from './fieldLabels';
import { fileColumnOptions } from './importModel';
import type { ColumnSamples } from './xlsxSamples';

export interface FieldsTableProps {
  readonly rows: readonly FieldRow[];
  /** Every column of the file that has a header (matched or not): what a field may be pointed at. */
  readonly columns: readonly FileColumn[];
  /** Values under the columns (from the chosen workbook); `null` — none, the «Examples» column is not shown. */
  readonly samples: ColumnSamples | null;
  readonly columnNames: ColumnNames;
  /** A field may be pointed at a column (the specification is known and the user may import). */
  readonly mayPick: boolean;
  /** The file is being re-read: the pickers wait. */
  readonly busy: boolean;
  /** The field at `index` of the specification reads the column `letter`; `null` — back to matching by header text. */
  readonly onPick: (index: number, letter: string | null) => void;
}

function stateText(state: FieldState): string {
  switch (state) {
    case 'found':
      return t('permits.air.import.fields.found');
    case 'missing':
      return t('permits.air.import.fields.missing');
    case 'notInFile':
      return t('permits.air.import.fields.notInFile');
  }
}

/** The column picker of one field: the file's columns as «B — Header», with their values and the field that reads one now. */
function ColumnPicker({
  row,
  label,
  columns,
  samples,
  owners,
  busy,
  focus,
  onPick,
}: {
  readonly row: FieldRow & { readonly index: number };
  readonly label: string;
  readonly columns: readonly FileColumn[];
  readonly samples: ColumnSamples | null;
  readonly owners: ReadonlyMap<string, string>;
  readonly busy: boolean;
  readonly focus: boolean;
  readonly onPick: (index: number, letter: string | null) => void;
}): JSX.Element {
  const input = useRef<HTMLInputElement>(null);
  // «Change» puts the focus into the picker it opens (keyboard users go on right there).
  useEffect(() => {
    if (focus) input.current?.focus();
  }, [focus]);
  const numbers = new Map(columns.map((column) => [column.columnLetter, column.column] as const));
  const details = (letter: string): string => {
    const values = samples?.get(numbers.get(letter) ?? 0) ?? [];
    const owner = owners.get(letter);
    const parts = [
      ...(values.length > 0 ? [values.join(' · ')] : []),
      ...(owner !== undefined && owner !== label ? [t('permits.air.import.fields.usedBy', { field: owner })] : []),
    ];

    return parts.join(' — ');
  };

  return (
    <Select
      ref={input}
      aria-label={t('permits.air.import.fields.pickFor', { field: label })}
      data={fileColumnOptions(columns)}
      value={row.column?.columnLetter ?? null}
      placeholder={t('permits.air.import.fields.pick')}
      searchable
      allowDeselect={false}
      clearable={row.matchesByHeader && row.column !== null}
      disabled={busy}
      error={row.state === 'missing'}
      nothingFoundMessage={t('permits.air.import.fields.noColumns')}
      renderOption={({ option }) => {
        const more = details(option.value);

        return (
          <div>
            <div>{option.label}</div>
            {more !== '' && (
              <Text size="xs" c="dimmed">
                {more}
              </Text>
            )}
          </div>
        );
      }}
      onChange={(value) => onPick(row.index, value)}
    />
  );
}

function Row({
  row,
  props,
  owners,
}: {
  readonly row: FieldRow;
  readonly props: FieldsTableProps;
  readonly owners: ReadonlyMap<string, string>;
}): JSX.Element {
  const [changing, setChanging] = useState(false);
  const label = fieldLabel(row.name, props.columnNames);
  const index = row.index;
  const pickable = index !== null && props.mayPick;
  const picking = pickable && (row.state !== 'found' || changing);
  const values = row.column === null ? [] : (props.samples?.get(row.column.column) ?? []);

  return (
    <tr data-state={row.state}>
      <th scope="row">{label}</th>
      <td>
        <span className="pi-need" data-required={row.required}>
          {row.required ? t('permits.air.import.fields.required') : t('permits.air.import.fields.optional')}
        </span>
      </td>
      <td className="pi-pick">
        {picking ? (
          <ColumnPicker
            row={{ ...row, index }}
            label={label}
            columns={props.columns}
            samples={props.samples}
            owners={owners}
            busy={props.busy}
            focus={changing}
            onPick={(at, letter) => {
              setChanging(false);
              props.onPick(at, letter);
            }}
          />
        ) : row.column !== null ? (
          <span className="pi-column">
            <span className="pa-code">{row.column.columnLetter}</span>
            <span>{row.column.headerText}</span>
          </span>
        ) : (
          '—'
        )}
        {pickable && row.state === 'found' && !changing && (
          <Button
            variant="subtle"
            size="compact-xs"
            aria-label={t('permits.air.import.fields.changeFor', { field: label })}
            onClick={() => setChanging(true)}
          >
            {t('permits.air.import.fields.change')}
          </Button>
        )}
        {changing && (
          <Button
            variant="subtle"
            size="compact-xs"
            aria-label={t('permits.air.import.fields.keepFor', { field: label })}
            onClick={() => setChanging(false)}
          >
            {t('common.cancel')}
          </Button>
        )}
      </td>
      {props.samples !== null && <td className="pi-samples-cell">{values.join(' · ')}</td>}
      <td>
        <span className="pi-state" data-state={row.state}>
          {stateText(row.state)}
        </span>
        {row.issue !== null && (
          <Text size="xs" c="dimmed">
            {serverMessage(row.issue.messageKey, row.issue.args ?? {})}
          </Text>
        )}
      </td>
    </tr>
  );
}

function Head({ samples }: { readonly samples: boolean }): JSX.Element {
  return (
    <thead>
      <tr>
        <th scope="col">{t('permits.air.import.fields.field')}</th>
        <th scope="col">{t('permits.air.import.fields.needed')}</th>
        <th scope="col">{t('permits.air.import.fields.column')}</th>
        {samples && <th scope="col">{t('permits.air.import.fields.examples')}</th>}
        <th scope="col">{t('permits.air.import.fields.status')}</th>
      </tr>
    </thead>
  );
}

/**
 * UX 20.11: the step's main table — one row per field: what the field is, whether it is required, the file column it is read
 * from (with values from the file) and a plain state. A required field that was not found has its column picker right in the
 * row (the one action the user must take); a found one is changed with «Change». Optional fields the file does not have are
 * folded below: nothing to do about them unless the file has them under another header.
 */
export function FieldsTable(props: FieldsTableProps): JSX.Element {
  const titleId = useId();
  const owners = columnOwners(props.rows, (name) => fieldLabel(name, props.columnNames));
  const main = props.rows.filter((row) => row.state !== 'notInFile');
  const absent = props.rows.filter((row) => row.state === 'notInFile');
  const samples = props.samples !== null;

  return (
    <section className="pi-panel" aria-labelledby={titleId}>
      <Title order={3} size="h5" id={titleId}>
        {t('permits.air.import.fields.title')}
      </Title>
      <Text size="sm" c="dimmed">
        {t('permits.air.import.fields.hint')}
      </Text>
      <div className="pa-scroll" role="region" aria-label={t('permits.air.import.map.mappedAria')} tabIndex={0}>
        <table className="pi-table" data-testid="air-fields">
          <Head samples={samples} />
          <tbody>
            {main.map((row) => (
              <Row key={`${row.name}-${String(row.index)}`} row={row} props={props} owners={owners} />
            ))}
          </tbody>
        </table>
      </div>
      {absent.length > 0 && (
        <details data-testid="air-fields-absent">
          <summary>{t('permits.air.import.fields.absent', { count: formatNumber(absent.length) })}</summary>
          <Text size="sm" c="dimmed">
            {t('permits.air.import.fields.absentHint')}
          </Text>
          <div className="pa-scroll" role="region" aria-label={t('permits.air.import.fields.absentAria')} tabIndex={0}>
            <table className="pi-table">
              <Head samples={samples} />
              <tbody>
                {absent.map((row) => (
                  <Row key={`${row.name}-${String(row.index)}`} row={row} props={props} owners={owners} />
                ))}
              </tbody>
            </table>
          </div>
        </details>
      )}
    </section>
  );
}
