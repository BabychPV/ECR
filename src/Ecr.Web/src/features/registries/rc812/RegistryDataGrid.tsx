import { useRef, useState, type ClipboardEvent, type JSX, type KeyboardEvent } from 'react';
import { ActionIcon, Group, Table, Text, TextInput, VisuallyHidden } from '@mantine/core';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import type { RegistryRow } from '@/features/registries/rows/api';
import { CellEditor, type CommitMove } from './CellEditor';
import {
  cellDisplay,
  cellValue,
  checkText,
  validateCell,
  type CellProblem,
  type DuplicateMark,
  type RegistryField,
  type RowDraft,
} from './rowModel';

/** Рядок сітки: збережений запис і/або його чернетка. */
export interface GridRow {
  readonly rowKey: string;
  readonly row: RegistryRow | undefined;
  readonly draft: RowDraft | undefined;
}

export interface RegistryDataGridProps {
  readonly caption: string;
  readonly fields: readonly RegistryField[];
  readonly rows: readonly GridRow[];
  /** Скільки рядків у довіднику всього (`aria-rowcount`). */
  readonly totalCount: number;
  readonly readOnly: boolean;
  /** Чи вводить людина код нового запису (`CodeMode = Manual`). */
  readonly manualCode: boolean;
  readonly problems: ReadonlyMap<string, readonly CellProblem[]>;
  readonly duplicates: ReadonlyMap<string, DuplicateMark>;
  readonly lookupCodeOf: (field: RegistryField) => string | null;
  readonly asOf: string | null;
  readonly onEdit: (rowKey: string, field: string, value: string | null, display?: string) => void;
  readonly onEditCode: (rowKey: string, code: string) => void;
  readonly onToggleDelete: (rowKey: string) => void;
  readonly onOpen: (rowKey: string) => void;
  readonly onAddRow: () => void;
  readonly onPaste: (rowIndex: number, columnIndex: number, text: string) => void;
}

interface Position {
  readonly r: number;
  readonly c: number;
}

/**
 * Табличний редактор даних довідника (`ФВ-8.12`, §8.4, §8.8, §8.9).
 *
 * ⛔ Підказки кнопок — `title`, не Mantine `Tooltip`: `Tooltip` тягне частину `@floating-ui/react`,
 * що живе у ВХІДНОМУ чанку, і додавав +5 КБ gzip кожному маршруту (бюджет `D-132`).
 *
 * ⚠ Роль `grid` з рухомим фокусом: у сітці одна зупинка `Tab`, по комірках ходять стрілками,
 * `Enter`/`F2` відкриває типізований редактор. Стан комірки передається не лише кольором:
 * `aria-invalid`, текст причини в `title` і прихований текст для читача екрана.
 */
export function RegistryDataGrid(props: RegistryDataGridProps): JSX.Element {
  const { fields, rows, readOnly } = props;
  const [active, setActive] = useState<Position>({ r: 0, c: 0 });
  const [editing, setEditing] = useState<Position | null>(null);
  const tableRef = useRef<HTMLTableElement>(null);

  const focusCell = (next: Position): void => {
    const r = Math.max(0, Math.min(rows.length - 1, next.r));
    const c = Math.max(0, Math.min(fields.length - 1, next.c));
    setActive({ r, c });
    requestAnimationFrame(() => {
      tableRef.current?.querySelector<HTMLElement>(`[data-cell="${String(r)}:${String(c)}"]`)?.focus();
    });
  };

  const commit = (pos: Position, value: string | null, display: string | undefined, move: CommitMove): void => {
    const target = rows[pos.r];
    const field = fields[pos.c];
    setEditing(null);
    if (target === undefined || field === undefined) return;
    props.onEdit(target.rowKey, field.code, value, display);
    if (move === 'down') focusCell({ r: pos.r + 1, c: pos.c });
    else if (move === 'right') focusCell({ r: pos.r, c: pos.c + 1 });
    else if (move === 'left') focusCell({ r: pos.r, c: pos.c - 1 });
    else focusCell(pos);
  };

  const onCellKey = (event: KeyboardEvent<HTMLElement>, pos: Position): void => {
    if (editing !== null) return;
    const target = rows[pos.r];
    const ctrl = event.ctrlKey || event.metaKey;

    const moves: Record<string, Position> = {
      ArrowUp: { r: pos.r - 1, c: pos.c },
      ArrowDown: { r: pos.r + 1, c: pos.c },
      ArrowLeft: { r: pos.r, c: pos.c - 1 },
      ArrowRight: { r: pos.r, c: pos.c + 1 },
      Home: { r: ctrl ? 0 : pos.r, c: 0 },
      End: { r: ctrl ? rows.length - 1 : pos.r, c: fields.length - 1 },
      PageUp: { r: pos.r - 10, c: pos.c },
      PageDown: { r: pos.r + 10, c: pos.c },
    };
    const move = moves[event.key];
    if (move !== undefined) {
      event.preventDefault();
      focusCell(move);
      return;
    }

    if (ctrl && event.key === '.' && target !== undefined) {
      event.preventDefault();
      props.onOpen(target.rowKey);
      return;
    }
    if (readOnly || target === undefined) return;

    if (ctrl && event.key === 'Enter') {
      event.preventDefault();
      props.onAddRow();
      focusCell({ r: rows.length, c: 0 });
    } else if (ctrl && event.shiftKey && event.key === 'Delete') {
      event.preventDefault();
      props.onToggleDelete(target.rowKey);
    } else if ((event.key === 'Enter' || event.key === 'F2' || (event.altKey && event.key === 'ArrowDown')) && !target.draft?.deleted) {
      event.preventDefault();
      setEditing(pos);
    } else if ((event.key === 'Delete' || event.key === 'Backspace') && !target.draft?.deleted) {
      event.preventDefault();
      const field = fields[pos.c];
      if (field !== undefined) props.onEdit(target.rowKey, field.code, null);
    }
  };

  const onPaste = (event: ClipboardEvent<HTMLTableElement>): void => {
    if (readOnly || editing !== null) return;
    const text = event.clipboardData.getData('text/plain');
    if (text === '') return;
    event.preventDefault();
    props.onPaste(active.r, active.c, text);
  };

  return (
    <Table.ScrollContainer minWidth={480}>
      <Table
        ref={tableRef}
        role="grid"
        aria-label={props.caption}
        aria-rowcount={props.totalCount + 1}
        aria-colcount={fields.length + 3}
        striped
        withTableBorder
        highlightOnHover
        onPaste={onPaste}
      >
        <Table.Thead>
          <Table.Tr role="row" aria-rowindex={1}>
            <Table.Th role="columnheader">{t('registries.code')}</Table.Th>
            <Table.Th role="columnheader">{t('registries.name')}</Table.Th>
            {fields.map((field) => (
              <Table.Th key={field.code} role="columnheader">
                {localized(field.nameL10n) || field.code}
                {field.isRequired && <VisuallyHidden> ({t('registries.required')})</VisuallyHidden>}
              </Table.Th>
            ))}
            <Table.Th role="columnheader">
              <VisuallyHidden>{t('common.actions')}</VisuallyHidden>
            </Table.Th>
          </Table.Tr>
        </Table.Thead>

        <Table.Tbody>
          {rows.map((gridRow, r) => {
            const { row, draft, rowKey } = gridRow;
            const deleted = draft?.deleted === true;
            const rowProblems = props.problems.get(rowKey) ?? [];
            const rowLevel = rowProblems.filter((p) => p.field === null);
            const duplicate = props.duplicates.get(rowKey);
            const rowLabel = row?.code ?? t('registries.data.newRow', { row: r + 1 });

            return (
              <Table.Tr
                key={rowKey}
                role="row"
                aria-rowindex={r + 2}
                data-row-key={rowKey}
                data-deleted={deleted ? 'true' : undefined}
                style={deleted ? { textDecoration: 'line-through', opacity: 0.6 } : undefined}
              >
                <Table.Td role="gridcell">
                  {row === undefined && props.manualCode && !readOnly ? (
                    <TextInput
                      size="xs"
                      aria-label={t('registries.data.newCode', { row: r + 1 })}
                      value={draft?.code ?? ''}
                      onChange={(event) => props.onEditCode(rowKey, event.currentTarget.value)}
                    />
                  ) : (
                    <Text size="sm" ff="monospace">
                      {row?.code ?? t('registries.data.autoCode')}
                    </Text>
                  )}
                  {(rowLevel.length > 0 || duplicate !== undefined) && (
                    <Text size="xs" c="statusError" role="note">
                      ⚠{' '}
                      {duplicate !== undefined
                        ? t('registries.data.dupInBatch', { row: duplicate.otherRow, key: duplicate.keyCode })
                        : rowLevel.map(problemText).join(' ')}
                    </Text>
                  )}
                </Table.Td>
                <Table.Td role="gridcell">
                  <Text size="sm">{row?.display ?? ''}</Text>
                </Table.Td>

                {fields.map((field, c) => {
                  const pos = { r, c };
                  const value = cellValue(row, draft, field.code);
                  const edited = draft !== undefined && field.code in draft.values;
                  const local = edited ? validateCell(field, value) : null;
                  const server = rowProblems.filter((p) => p.field === field.code);
                  const message = local !== null ? checkText(local) : server.map(problemText).join(' ');
                  const invalid = message !== '';
                  const isActive = active.r === r && active.c === c;
                  const isEditing = editing?.r === r && editing.c === c;
                  const label = `${localized(field.nameL10n) || field.code}, ${rowLabel}`;

                  return (
                    <Table.Td
                      key={field.code}
                      role="gridcell"
                      tabIndex={isActive && !isEditing ? 0 : -1}
                      data-cell={`${String(r)}:${String(c)}`}
                      data-edited={edited ? 'true' : undefined}
                      aria-invalid={invalid ? true : undefined}
                      aria-readonly={readOnly ? true : undefined}
                      title={invalid ? message : undefined}
                      onFocus={() => setActive(pos)}
                      onDoubleClick={() => {
                        if (!readOnly && !deleted) setEditing(pos);
                      }}
                      onKeyDown={(event) => onCellKey(event, pos)}
                      style={{
                        fontWeight: edited ? 600 : undefined,
                        outline: invalid ? '2px solid var(--mantine-color-statusError-filled)' : undefined,
                        outlineOffset: -2,
                      }}
                    >
                      {isEditing ? (
                        <CellEditor
                          field={field}
                          label={label}
                          value={value}
                          display={cellDisplay(row, draft, field.code)}
                          lookupCode={props.lookupCodeOf(field)}
                          asOf={props.asOf}
                          onCommit={(next, display, move) => commit(pos, next, display, move)}
                          onCancel={() => {
                            setEditing(null);
                            focusCell(pos);
                          }}
                        />
                      ) : (
                        <Group gap="xs" wrap="nowrap">
                          <Text size="sm">{boolText(field, cellDisplay(row, draft, field.code))}</Text>
                          {edited && (
                            <Text size="xs" c="dimmed" aria-hidden>
                              ●
                            </Text>
                          )}
                          {edited && <VisuallyHidden>{t('registries.data.edited')}</VisuallyHidden>}
                          {invalid && (
                            <Text size="xs" c="statusError">
                              ⚠ <VisuallyHidden>{message}</VisuallyHidden>
                            </Text>
                          )}
                        </Group>
                      )}
                    </Table.Td>
                  );
                })}

                <Table.Td role="gridcell">
                  <Group gap="xs" wrap="nowrap">
                    <ActionIcon
                        size="sm"
                        variant="subtle"
                        title={t('registries.data.openEntry')}
                        aria-label={`${t('registries.data.openEntry')}: ${rowLabel}`}
                        onClick={() => props.onOpen(rowKey)}
                        disabled={row === undefined}
                      >
                        ⋯
                      </ActionIcon>
                    {!readOnly && (
                        <ActionIcon
                          size="sm"
                          variant="subtle"
                          title={deleted ? t('registries.data.restoreRow') : t('registries.data.deleteRow')}
                          color={deleted ? 'gray' : 'statusError'}
                          aria-label={`${deleted ? t('registries.data.restoreRow') : t('registries.data.deleteRow')}: ${rowLabel}`}
                          aria-pressed={deleted}
                          onClick={() => props.onToggleDelete(rowKey)}
                        >
                          {deleted ? '↺' : '✕'}
                        </ActionIcon>
                    )}
                  </Group>
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/** Текст помилки звіту пакета: ключ приходить із сервера (`messageKey` рядка). */
function problemText(problem: CellProblem): string {
  return t(problem.messageKey, { ...problem.params });
}

/** `true`/`false` у комірці `Bool` — словами каталогу, а не машинним літералом. */
function boolText(field: RegistryField, text: string): string {
  if (field.dataType !== 'Bool') return text;
  if (text === 'true') return t('registries.yes');
  if (text === 'false') return t('registries.data.no');
  return text;
}
