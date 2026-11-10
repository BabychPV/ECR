import type { DocumentTableDto } from '@/api/types';
import type { CalculationIssue, CalculationIssueTableHint } from '@/features/calculationIssues/api';
import { copyFieldsOf } from '@/features/documents/sheetCopies/sheetCopyTypes';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

/**
 * Селектор банера над сіткою: панель «Calculation results» веде сюди посиланням «not calculated: N rows» (✎ 10.11).
 * ⚠ Не `id` на `Alert`: Mantine сам дає тілу `<id>-body`, і це перекрило б `document-unmatched-rows-body` згортання.
 */
export const UnmatchedRowsBannerSelector = '[data-testid="document-unmatched-rows"]';

/** Вид проблеми рядка, що його відхилило правило категорії (✎ 10.11, doc 95). */
export const CategoryRuleRejectedKind = 'CategoryRuleRejected';

/** Колонка, яку треба заповнити, щоб рядок порахувався. */
export interface MissingColumnRef {
  readonly code: string;
  readonly name: string;
}

/** Один рядок без правила (або відхилений правилом категорії) в групі таблиці. */
export interface UnmatchedRowRef {
  readonly rowKey: string;
  readonly rowNumber: number;
  /** Значення рядка в колонках підказки (порядок `hint.columns`, `''` — порожньо); `null` — підказки немає. */
  readonly currentValues: readonly string[] | null;
  /** ✎ 10.11: правило методології, що закрило відхилений рядок; `null` — не відхилений або невідоме. */
  readonly ruleCode: string | null;
  /** ✎ 10.11: порожні колонки відхиленого рядка, які треба заповнити; порожньо — назвати нічого. */
  readonly missingColumns: readonly MissingColumnRef[];
}

/** Що приймають правила методологій таблиці (UAT 08.11 b3). */
export interface UnmatchedTableHint {
  /** Назви колонок, які згадують правила. */
  readonly columns: readonly string[];
  /** Допустимі варіанти — вже текстом («0903» або «Col A = x · Col B = y» для кількох колонок). */
  readonly options: readonly string[];
  /** Назви методологій (код — лише коли назви немає). */
  readonly methodologies: readonly string[];
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
  /** Що приймають правила; `null` — сервер підказки не дав (немає правил з колонками таблиці чи колонка прихована). */
  readonly hint: UnmatchedTableHint | null;
  /** Рядки за зростанням номера, без повторів ключа. */
  readonly rows: readonly UnmatchedRowRef[];
  /** ✎ 10.11: рядки ВІДХИЛЕНІ правилом категорії (правило підійшло, ключа категорії немає), а не «без правила». */
  readonly rejected: boolean;
}

/**
 * Групує рядки без правила за таблицею (і аркушем) у порядку аркушів і таблиць документа.
 *
 * ⛔ Повтори «1 (S01), 2 (S02), 1 (S01)…» старого банера — це ОДНАКОВІ ключі рядків РІЗНИХ таблиць, зведені в один
 * рядок без назви таблиці. Тут ключ групи — таблиця (код + копія аркуша), а рядок у групі — унікальний `rowKey`.
 *
 * ⚠ Проблема таблиці, якої в переліку таблиць документа немає (таблиця прихована чи прибрана з шаблону), лишається
 * у списку з кодом таблиці замість назви й без переходу — сховати її означало б показати меншу кількість, ніж є.
 *
 * ✎ UAT 08.11 b3: підказка таблиці (`hints`, з тієї ж відповіді сервера) — допустимі значення колонок правил; варіант
 * однієї колонки — саме значення, кількох — «Колонка = значення» через « · », колонка без обмеження пропускається.
 */
export function groupUnmatchedRows(
  issues: readonly CalculationIssue[],
  tables: readonly DocumentTableDto[],
  hints: readonly CalculationIssueTableHint[] = [],
): readonly UnmatchedTableGroup[] {
  const tableOf = new Map<string, DocumentTableDto>();
  for (const table of tables) tableOf.set(groupKey(table.tableCode, copyFieldsOf(table).sheetCopyId), table);
  const hintOf = new Map<string, CalculationIssueTableHint>();
  for (const hint of hints) hintOf.set(groupKey(hint.tableCode, hint.sheetCopyId ?? null), hint);

  const byKey = new Map<
    string,
    {
      table: DocumentTableDto | undefined;
      code: string;
      methodology: string | null;
      rejected: boolean;
      tableKey: string;
      rows: Map<string, UnmatchedRowRef>;
    }
  >();
  for (const issue of issues) {
    const tableKey = groupKey(issue.tableCode, issue.sheetCopyId ?? null);
    // ✎ 10.11: відхилені правилом категорії — окремою групою тієї ж таблиці: причина й дія інші, ніж у «без правила».
    const rejected = issue.kind === CategoryRuleRejectedKind;
    const key = rejected ? `${tableKey}!rejected` : tableKey;
    let group = byKey.get(key);
    if (group === undefined) {
      group = { table: tableOf.get(tableKey), code: issue.tableCode, methodology: null, rejected, tableKey, rows: new Map() };
      byKey.set(key, group);
    }
    group.methodology ??= issue.methodologyCode ?? null;
    if (!group.rows.has(issue.rowKey)) {
      group.rows.set(issue.rowKey, {
        rowKey: issue.rowKey,
        rowNumber: issue.rowNumber,
        currentValues: rejected ? null : (issue.currentValues ?? null),
        ruleCode: rejected ? (issue.ruleCode ?? null) : null,
        missingColumns: rejected
          ? (issue.columns ?? []).map((column) => ({ code: column.code, name: localized(column.nameL10n) || column.code }))
          : [],
      });
    }
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
    .map(([key, group]) => {
      const hint = group.rejected ? undefined : hintOf.get(group.tableKey);

      return {
        key,
        sheetName: group.table === undefined ? '' : sheetNameOf(group.table),
        tableName: group.table === undefined ? group.code : localized(group.table.tableNameL10n) || group.code,
        tableInstanceId: group.table?.tableInstanceId ?? null,
        methodologyCode: group.methodology,
        hint: hint === undefined ? null : hintTextOf(hint),
        rows: [...group.rows.values()]
          .map((row) => (hint === undefined ? { ...row, currentValues: null } : row))
          .sort((a, b) => a.rowNumber - b.rowNumber),
        rejected: group.rejected,
      };
    });
}

/** Загальна кількість рядків у групах. */
export function unmatchedRowCount(groups: readonly UnmatchedTableGroup[]): number {
  return groups.reduce((sum, group) => sum + group.rows.length, 0);
}

function hintTextOf(hint: CalculationIssueTableHint): UnmatchedTableHint {
  const columns = hint.columns.map((column) => localized(column.nameL10n) || column.code);
  const single = columns.length === 1;
  const options = hint.options
    .map((option) =>
      single
        ? (option[0] ?? '')
        : option
            // ✎ fg6 F13 P3: назви колонок — один раз у «Calculated only for «A, B»», варіант — значення через « / »;
            // колонка, яку правило не обмежує, — «any».
            .map((value) => (value === '' ? t('document.unmatchedRows.anyValue') : value))
            .join(' / '),
    )
    .filter((text) => text.length > 0);

  const methodologies = hint.methodologies.map((m) => localized(m.nameL10n ?? null) || m.code);

  return { columns, options: [...new Set(options)], methodologies };
}

function groupKey(tableCode: string, sheetCopyId: number | null): string {
  return sheetCopyId === null ? tableCode : `${tableCode}#${String(sheetCopyId)}`;
}

function sheetNameOf(table: DocumentTableDto): string {
  const copy = copyFieldsOf(table);
  const own = copy.sheetCopyId === null ? '' : localized(copy.displayNameL10n ?? null);

  return own.length > 0 ? own : localized(table.sheetNameL10n) || table.sheetCode;
}
