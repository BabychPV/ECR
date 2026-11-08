import type { DocumentTableDto } from '@/api/types';
import type { CalculationIssue } from '@/features/calculationIssues/api';
import { copyFieldsOf } from '@/features/documents/sheetCopies/sheetCopyTypes';
import { localized } from '@/shared/i18n/localized';

/** Один рядок без правила в групі таблиці. */
export interface UnmatchedRowRef {
  readonly rowKey: string;
  readonly rowNumber: number;
}

/** Рядки без правила однієї таблиці документа (D14 + AN-69, UAT 08.11 b1). */
export interface UnmatchedTableGroup {
  /** Стабільний ключ групи: код таблиці + копія аркуша. */
  readonly key: string;
  /** Назва аркуша (копії — її власна назва); порожньо — таблиці серед таблиць документа немає. */
  readonly sheetName: string;
  /** Назва таблиці; без таблиці в документі — її код. */
  readonly tableName: string;
  /** Екземпляр таблиці для переходу; `null` — таблиці в документі за цей період немає, перейти нікуди. */
  readonly tableInstanceId: number | null;
  /** Методологія прив'язки (перша відома серед рядків групи); `null` — невідома. */
  readonly methodologyCode: string | null;
  /** Рядки за зростанням номера, без повторів ключа. */
  readonly rows: readonly UnmatchedRowRef[];
}

/**
 * Групує рядки без правила за таблицею (і аркушем) у порядку аркушів і таблиць документа.
 *
 * ⛔ Повтори «1 (S01), 2 (S02), 1 (S01)…» старого банера — це ОДНАКОВІ ключі рядків РІЗНИХ таблиць, зведені в один
 * рядок без назви таблиці. Тут ключ групи — таблиця (код + копія аркуша), а рядок у групі — унікальний `rowKey`.
 *
 * ⚠ Проблема таблиці, якої в переліку таблиць документа немає (таблиця прихована чи прибрана з шаблону), лишається
 * у списку з кодом таблиці замість назви й без переходу — сховати її означало б показати меншу кількість, ніж є.
 */
export function groupUnmatchedRows(
  issues: readonly CalculationIssue[],
  tables: readonly DocumentTableDto[],
): readonly UnmatchedTableGroup[] {
  const tableOf = new Map<string, DocumentTableDto>();
  for (const table of tables) tableOf.set(groupKey(table.tableCode, copyFieldsOf(table).sheetCopyId), table);

  const byKey = new Map<string, { table: DocumentTableDto | undefined; code: string; methodology: string | null; rows: Map<string, number> }>();
  for (const issue of issues) {
    const key = groupKey(issue.tableCode, issue.sheetCopyId ?? null);
    let group = byKey.get(key);
    if (group === undefined) {
      group = { table: tableOf.get(key), code: issue.tableCode, methodology: null, rows: new Map() };
      byKey.set(key, group);
    }
    group.methodology ??= issue.methodologyCode ?? null;
    if (!group.rows.has(issue.rowKey)) group.rows.set(issue.rowKey, issue.rowNumber);
  }

  const order = (table: DocumentTableDto | undefined): readonly [number, number, number] =>
    table === undefined
      ? [Number.MAX_SAFE_INTEGER, Number.MAX_SAFE_INTEGER, Number.MAX_SAFE_INTEGER]
      : [table.sheetOrdinal, table.copyOrdinal, table.tableOrdinal];

  return [...byKey.entries()]
    .sort(([keyA, a], [keyB, b]) => {
      const [sa, ca, ta] = order(a.table);
      const [sb, cb, tb] = order(b.table);

      return sa - sb || ca - cb || ta - tb || keyA.localeCompare(keyB);
    })
    .map(([key, group]) => ({
      key,
      sheetName: group.table === undefined ? '' : sheetNameOf(group.table),
      tableName: group.table === undefined ? group.code : localized(group.table.tableNameL10n) || group.code,
      tableInstanceId: group.table?.tableInstanceId ?? null,
      methodologyCode: group.methodology,
      rows: [...group.rows.entries()]
        .map(([rowKey, rowNumber]) => ({ rowKey, rowNumber }))
        .sort((a, b) => a.rowNumber - b.rowNumber),
    }));
}

/** Загальна кількість рядків у групах. */
export function unmatchedRowCount(groups: readonly UnmatchedTableGroup[]): number {
  return groups.reduce((sum, group) => sum + group.rows.length, 0);
}

function groupKey(tableCode: string, sheetCopyId: number | null): string {
  return sheetCopyId === null ? tableCode : `${tableCode}#${String(sheetCopyId)}`;
}

function sheetNameOf(table: DocumentTableDto): string {
  const copy = copyFieldsOf(table);
  const own = copy.sheetCopyId === null ? '' : localized(copy.displayNameL10n ?? null);

  return own.length > 0 ? own : localized(table.sheetNameL10n) || table.sheetCode;
}
