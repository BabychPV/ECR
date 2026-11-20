import { useEffect, useId, useRef, useState, type JSX, type RefObject } from 'react';
import { Button, Select, Text, Title } from '@mantine/core';
import { serverMessage } from '@/features/registries/rc816/serverMessage';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import { columnOwner, columnOwners, type FieldRow, type FieldState, type FileColumn } from './fieldRows';
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
  /** The file is being re-read: the pickers wait (a pick then would be dropped). */
  readonly busy: boolean;
  /**
   * The field at `index` of the specification reads the column `letter`; `null` — back to matching by header text. `ownerIndex` —
   * the field that read the column before (the user confirmed taking it), `null` — none or not in the specification.
   */
  readonly onPick: (index: number, letter: string | null, ownerIndex: number | null) => void;
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

/** Review 20.11 (level kept visible): what one value of the field belongs to, in plain words; read-only (edited under «Advanced»). */
function levelNote(level: FieldRow['level']): string | null {
  if (level === 'Parent') return t('permits.air.import.fields.perSource');
  if (level === 'Child') return t('permits.air.import.fields.perRow');

  return null;
}

/** The column picker of one field: the file's columns as «B — Header», with their values and the field that reads one now. */
function ColumnPicker({
  row,
  label,
  columns,
  samples,
  owners,
  busy,
  inputRef,
  onChoose,
}: {
  readonly row: FieldRow;
  readonly label: string;
  readonly columns: readonly FileColumn[];
  readonly samples: ColumnSamples | null;
  readonly owners: ReadonlyMap<string, string>;
  readonly busy: boolean;
  readonly inputRef: RefObject<HTMLInputElement | null>;
  readonly onChoose: (letter: string | null) => void;
}): JSX.Element {
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
      ref={inputRef}
      aria-label={t('permits.air.import.fields.pickFor', { field: label })}
      data={fileColumnOptions(columns)}
      value={row.column?.columnLetter ?? null}
      placeholder={t('permits.air.import.fields.pick')}
      searchable
      allowDeselect={false}
      // Review 20.11 (L2): «back to header texts» only for a field that names its column explicitly and has header texts to
      // fall back on — clearing never leaves a field without any way to its column.
      clearable={row.explicit && row.matchesByHeader}
      // Mantine hides its clear button from the keyboard and screen readers; here it is a real, named button.
      clearButtonProps={{ 'aria-label': t('permits.air.import.fields.clearFor', { field: label }), 'aria-hidden': false, tabIndex: 0 }}
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
      onChange={onChoose}
    />
  );
}

interface Move {
  readonly letter: string;
  readonly owner: FieldRow;
}

function Row({
  row,
  props,
  owners,
  focusNow,
  onPicked,
  onFocused,
}: {
  readonly row: FieldRow;
  readonly props: FieldsTableProps;
  readonly owners: ReadonlyMap<string, string>;
  /** The file was re-read after a pick in this row: the focus comes back here (L1). */
  readonly focusNow: boolean;
  readonly onPicked: (name: string) => void;
  readonly onFocused: () => void;
}): JSX.Element {
  const [changing, setChanging] = useState(false);
  const [move, setMove] = useState<Move | null>(null);
  const picker = useRef<HTMLInputElement>(null);
  const change = useRef<HTMLButtonElement>(null);
  const moveButton = useRef<HTMLButtonElement>(null);
  const label = fieldLabel(row.name, props.columnNames);
  const index = row.index;
  const pickable = index !== null && props.mayPick;
  const picking = pickable && (row.state !== 'found' || changing) && move === null;
  const values = row.column === null ? [] : (props.samples?.get(row.column.column) ?? []);
  const level = row.column === null ? null : levelNote(row.level);

  // «Change» puts the focus into the picker it opens; a pick that asks first puts it on «Move column».
  useEffect(() => {
    if (changing && move === null) picker.current?.focus();
  }, [changing, move]);
  useEffect(() => {
    if (move !== null) moveButton.current?.focus();
  }, [move]);
  // After the re-read the focus goes to this row: «Change» when the column was found, else the picker again.
  useEffect(() => {
    if (!focusNow) return;
    (change.current ?? picker.current)?.focus();
    onFocused();
  }, [focusNow, onFocused]);

  const apply = (letter: string | null, owner: FieldRow | null): void => {
    if (index === null) return;
    setChanging(false);
    setMove(null);
    onPicked(row.name);
    props.onPick(index, letter, owner?.index ?? null);
  };
  const choose = (letter: string | null): void => {
    const owner = letter === null ? null : columnOwner(props.rows, letter, row.name);
    // Review 20.11 (M2): a column another field reads is taken only after the user agrees, with the consequence said.
    if (owner !== null && letter !== null) setMove({ letter, owner });
    else apply(letter, null);
  };
  const ownerLabel = move === null ? '' : fieldLabel(move.owner.name, props.columnNames);

  return (
    <tr data-state={row.state}>
      <th scope="row">{label}</th>
      <td>
        <span className="pi-need" data-required={row.required}>
          {row.required ? t('permits.air.import.fields.required') : t('permits.air.import.fields.optional')}
        </span>
        {level !== null && <span className="pi-level">{level}</span>}
      </td>
      <td className="pi-pick">
        {picking ? (
          <ColumnPicker
            row={row}
            label={label}
            columns={props.columns}
            samples={props.samples}
            owners={owners}
            busy={props.busy}
            inputRef={picker}
            onChoose={choose}
          />
        ) : row.column !== null ? (
          <span className="pi-column">
            <span className="pa-code">{row.column.columnLetter}</span>
            <span>{row.column.headerText}</span>
          </span>
        ) : (
          '—'
        )}
        {pickable && row.state === 'found' && !changing && move === null && (
          <Button
            ref={change}
            variant="subtle"
            size="xs"
            disabled={props.busy}
            aria-label={t('permits.air.import.fields.changeFor', { field: label })}
            onClick={() => setChanging(true)}
          >
            {t('permits.air.import.fields.change')}
          </Button>
        )}
        {changing && move === null && (
          <Button
            variant="subtle"
            size="xs"
            aria-label={t('permits.air.import.fields.keepFor', { field: label })}
            onClick={() => setChanging(false)}
          >
            {t('common.cancel')}
          </Button>
        )}
        {move !== null && (
          <div className="pi-move" role="group" aria-label={t('permits.air.import.fields.moveTitle', { column: move.letter, owner: ownerLabel })} data-testid="air-column-move">
            <Text size="sm" fw={600}>
              {t('permits.air.import.fields.moveTitle', { column: move.letter, owner: ownerLabel })}
            </Text>
            <Text size="sm">{t('permits.air.import.fields.moveText', { column: move.letter, owner: ownerLabel, field: label })}</Text>
            {move.owner.required && (
              <Text size="sm" c="statusError">
                {t('permits.air.import.fields.moveRequired', { owner: ownerLabel })}
              </Text>
            )}
            <div className="pi-actions">
              <Button
                variant="default"
                size="xs"
                aria-label={t('permits.air.import.fields.moveCancel', { column: move.letter, owner: ownerLabel })}
                onClick={() => setMove(null)}
              >
                {t('common.cancel')}
              </Button>
              <Button ref={moveButton} size="xs" disabled={props.busy} onClick={() => apply(move.letter, move.owner)}>
                {t('permits.air.import.fields.move')}
              </Button>
            </div>
          </div>
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
 * UX 20.11: the step's main table — one row per field: what the field is, whether it is required (and what one value belongs
 * to), the file column it is read from (with values from the file) and a plain state. A required field that was not found has
 * its column picker right in the row (the one action the user must take); a found one is changed with «Change». Optional fields
 * the file does not have are folded below: nothing to do about them unless the file has them under another header.
 */
export function FieldsTable(props: FieldsTableProps): JSX.Element {
  const titleId = useId();
  const owners = columnOwners(props.rows, (name) => fieldLabel(name, props.columnNames));
  const main = props.rows.filter((row) => row.state !== 'notInFile');
  const absent = props.rows.filter((row) => row.state === 'notInFile');
  const samples = props.samples !== null;
  // L1: the row a pick came from gets the focus back once the re-read it started is over (seen busy, then not).
  const [focusAfter, setFocusAfter] = useState<{ readonly name: string; readonly seenBusy: boolean } | null>(null);
  if (focusAfter !== null && props.busy && !focusAfter.seenBusy) setFocusAfter({ ...focusAfter, seenBusy: true });
  const focusName = focusAfter !== null && focusAfter.seenBusy && !props.busy ? focusAfter.name : null;
  const [onFocused] = useState(() => () => setFocusAfter(null));
  const rowOf = (row: FieldRow): JSX.Element => (
    <Row
      key={`${row.name}-${String(row.index)}`}
      row={row}
      props={props}
      owners={owners}
      focusNow={focusName === row.name}
      onPicked={(name) => setFocusAfter({ name, seenBusy: false })}
      onFocused={onFocused}
    />
  );

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
          <tbody>{main.map(rowOf)}</tbody>
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
              <tbody>{absent.map(rowOf)}</tbody>
            </table>
          </div>
        </details>
      )}
    </section>
  );
}
