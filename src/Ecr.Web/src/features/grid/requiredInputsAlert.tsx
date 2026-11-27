import { type JSX } from 'react';
import { Alert, List } from '@mantine/core';
import type { RequiredInputCell } from '@/api/client';
import { t } from '@/shared/i18n';

/**
 * Блок «незаповнені обов'язкові колонки методології» (`ECR-CALC-0437`).
 *
 * ✎ 25.11, живі проходи (таблиця 3.00 і документ 100 LAND_UI): над сіткою ріс розгорнутий коричневий банер —
 * заголовок «4 required column(s) missing», а під ним 131–137 рядків «Колонка «Land_TypeFuel» обов'язкова…»
 * (той самий рядок 50 разів, уже заповнені комірки не зникали), коди колонок замість назв, українською в
 * англійському інтерфейсі, без номерів рядків, і сітка з'їжджала вниз саме під час введення. Тепер:
 *
 * - **один рядок на КОЛОНКУ** (`groupRequiredInputs`): методології перелічено раз, рядки документа — номером або
 *   назвою; лічильник = кількість рядків переліку;
 * - **з поточних даних** (`liveRequiredInputs`): комірка, яку вже заповнили (у зрізі чи незбереженою правкою), і
 *   рядок, якого в зрізі більше немає, з переліку випадають одразу, не чекаючи наступної відповіді сервера;
 * - **назва колонки** — заголовок зрізу (`ColumnDto.header`, мовою інтерфейсу); текст — ключі каталогу;
 * - **Warn — одна кнопка в рядку інструментів сітки**, перелік — спливаючим вікном на вимогу: сітка не
 *   рухається, нічого не розгортається саме. Чому не «лише після виходу з рядка»: позначки на комірках і так
 *   приходять лише після збереження, а сховати підказку, поки фокус у рядку, означало б не попередити саме тоді,
 *   коли людина ще може дописати; згорнута кнопка — найтихіший варіант, що все ще попереджає до подання;
 * - **Block — розгорнутий банер**: збереження ВІДХИЛЕНО, людина мусить бачити, чого бракує.
 */

/** Зведений рядок переліку: одна колонка. */
export interface RequiredInputGroup {
  readonly columnCode: string;
  readonly header: string;
  readonly methodologies: readonly string[];
  /** Підписи рядків документа (назва рядка або його номер у зрізі). */
  readonly rows: readonly string[];
}

/** Колонка зрізу — лише те, що потрібно для назви. */
export interface RequiredInputColumn {
  readonly code: string;
  readonly header: string;
}

/** Рядок зрізу — лише те, що потрібно для підпису. */
export interface RequiredInputRow {
  readonly rowKey: string;
  readonly label: string | null;
}

/** Модель рядка сітки (значення за кодом колонки, з незбереженими правками). */
export type ShownRow = { readonly __rowKey: string } & Readonly<Record<string, unknown>>;

/** Вимоги без повторів (рядок + колонка), методології злито. */
export function dedupeRequiredInputs(cells: readonly RequiredInputCell[]): RequiredInputCell[] {
  const byCell = new Map<string, RequiredInputCell>();
  for (const cell of cells) {
    const key = `${cell.rowKey}\u0000${cell.columnCode}`;
    const known = byCell.get(key);
    if (known === undefined) {
      byCell.set(key, cell);
      continue;
    }

    const merged = [...(known.methodologies ?? [])];
    for (const code of cell.methodologies ?? []) if (!merged.includes(code)) merged.push(code);
    byCell.set(key, { ...known, methodologies: merged });
  }

  return [...byCell.values()];
}

function isFilled(value: unknown): boolean {
  return value !== null && value !== undefined && !(typeof value === 'string' && value.trim() === '');
}

/**
 * Лише вимоги, що досі в силі: рядок є в зрізі, а комірка порожня в тому, що бачить людина.
 *
 * @param rows Моделі рядків сітки; `null` — даних ще немає (перелік без змін).
 */
export function liveRequiredInputs(
  cells: readonly RequiredInputCell[],
  rows: readonly ShownRow[] | null,
): RequiredInputCell[] {
  if (rows === null) return [...cells];

  const byKey = new Map(rows.map((row) => [row.__rowKey, row]));

  return cells.filter((cell) => {
    const row = byKey.get(cell.rowKey);
    return row !== undefined && !isFilled(row[cell.columnCode]);
  });
}

/**
 * Зводить вимоги по колонці (порядок — порядок колонок зрізу, невідомі — у кінці за першою появою).
 */
export function groupRequiredInputs(
  cells: readonly RequiredInputCell[],
  columns: readonly RequiredInputColumn[],
  rows: readonly RequiredInputRow[] = [],
): readonly RequiredInputGroup[] {
  const rowIndex = new Map(rows.map((row, index) => [row.rowKey, index]));
  const rowLabel = (rowKey: string): string => {
    const index = rowIndex.get(rowKey);
    if (index === undefined) return rowKey;
    return rows[index]?.label ?? String(index + 1);
  };

  const byCode = new Map<string, { methodologies: string[]; rowKeys: string[]; first: number }>();
  cells.forEach((cell, index) => {
    const entry = byCode.get(cell.columnCode) ?? { methodologies: [], rowKeys: [], first: index };
    for (const code of cell.methodologies ?? []) {
      if (!entry.methodologies.includes(code)) entry.methodologies.push(code);
    }
    if (!entry.rowKeys.includes(cell.rowKey)) entry.rowKeys.push(cell.rowKey);
    byCode.set(cell.columnCode, entry);
  });

  const ordinal = new Map(columns.map((column, index) => [column.code, index]));
  const headerOf = new Map(columns.map((column) => [column.code, column.header]));

  return [...byCode.entries()]
    .sort(
      ([a, x], [b, y]) =>
        (ordinal.get(a) ?? columns.length + x.first) - (ordinal.get(b) ?? columns.length + y.first),
    )
    .map(([columnCode, entry]) => ({
      columnCode,
      header: headerOf.get(columnCode) ?? columnCode,
      methodologies: [...entry.methodologies].sort((a, b) => a.localeCompare(b)),
      rows: [...entry.rowKeys]
        .sort((a, b) => (rowIndex.get(a) ?? Number.MAX_SAFE_INTEGER) - (rowIndex.get(b) ?? Number.MAX_SAFE_INTEGER))
        .map(rowLabel),
    }));
}

/** Текст рядка переліку мовою інтерфейсу. */
export function requiredInputLine(group: RequiredInputGroup): string {
  const rows = group.rows.join(', ');

  return group.methodologies.length === 0
    ? t('grid.requiredInput.lineNoMethodology', { column: group.header, rows })
    : t('grid.requiredInput.line', { column: group.header, rows, methodologies: group.methodologies.join(', ') });
}

interface RequiredInputsProps {
  readonly cells: readonly RequiredInputCell[];
  readonly columns: readonly RequiredInputColumn[];
  readonly rows: readonly RequiredInputRow[];
}

function GroupList({ groups }: { readonly groups: readonly RequiredInputGroup[] }): JSX.Element {
  return (
    <List size="sm" data-required-inputs-list="">
      {groups.map((group) => (
        <List.Item key={group.columnCode}>{requiredInputLine(group)}</List.Item>
      ))}
    </List>
  );
}

/** Block: запис відхилено — розгорнутий банер над сіткою. */
export function RequiredInputsBlockedAlert({ cells, columns, rows }: RequiredInputsProps): JSX.Element | null {
  const groups = groupRequiredInputs(dedupeRequiredInputs(cells), columns, rows);
  if (groups.length === 0) return null;

  return (
    <Alert
      color="statusError"
      title={t('grid.requiredInputBlockedTitle', { count: groups.length })}
      data-required-inputs="blocked"
    >
      <GroupList groups={groups} />
    </Alert>
  );
}

// ✎ DOCUX L2: Warn-кнопку «N required column(s) not filled yet» замінив лічильник комірок `NeedsInputCounter` (‹ ›, F8).
