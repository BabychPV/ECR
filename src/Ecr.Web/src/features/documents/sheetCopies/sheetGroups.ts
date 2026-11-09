import type { DocumentTableDto } from '@/api/types';
import { copyFieldsOf, groupNameOf, groupNameValuesOf, sheetStateKey, sheetTabKey, sheetTabLabel } from './sheetCopyTypes';

/** Вкладка документа: аркуш шаблону або його копія — з таблицями. */
export interface SheetGroup {
  sheetDefId: number;
  /** Код аркуша шаблону; у оригіналу й копій однаковий. */
  code: string;
  /** Ключ у `sheetStates`: оригінал — `КОД`, копія — `КОД#sheetCopyId`. */
  stateKey: string;
  /** Ключ вкладки (`?sheet=`): оригінал — код, копія — `код~id`. */
  key: string;
  /** Підпис вкладки («Назва», «Назва 2»). */
  name: string;
  /** Назва групи без номера мовами каталогу — для діалогу перейменування. */
  groupNameValues: Record<string, string>;
  ordinal: number;
  copyOrdinal: number;
  sheetCopyId: number | null;
  isCopyable: boolean;
  tables: DocumentTableDto[];
}

/**
 * Стан вкладки: оригінал — `sheetStates[КОД]`, копія — `sheetStates[КОД#sheetCopyId]` (власний стан, не оригіналу).
 * Немає запису — `Draft` (так само його читає сервер).
 */
export function stateOfGroup(group: Pick<SheetGroup, 'stateKey'>, states: Readonly<Record<string, string>>): string {
  return states[group.stateKey] ?? 'Draft';
}

/**
 * ✎ UAT-recalc-ux / F13 p218 #788: позначка «перераховано після подання» вкладки — за КЛЮЧЕМ СТАНУ (`КОД` оригіналу,
 * `КОД#sheetCopyId` копії), як і сам стан. За голим кодом подана копія позначки не бачила, а оригінал віддавав би свою.
 */
export function recalculatedAfterSubmissionOf(
  group: Pick<SheetGroup, 'stateKey'>,
  sheets: readonly { code: string; recalculatedAfterSubmission?: boolean | null }[],
): boolean {
  return sheets.some((s) => s.code === group.stateKey && s.recalculatedAfterSubmission === true);
}

/**
 * TC02: скільки живих повідомлень чату буде видалено разом із копією (показується ДО підтвердження).
 * Сервер віддає число в кожній таблиці копії (`null` — оригінал); береться перше задане. Немає — `null`.
 */
export function chatMessageCountOf(group: Pick<SheetGroup, 'tables'>): number | null {
  for (const table of group.tables) {
    if (table.chatMessageCount !== undefined && table.chatMessageCount !== null) return table.chatMessageCount;
  }

  return null;
}

/** Елемент смуги аркушів: `code` — ключ вкладки, стан береться за ключем стану вкладки (`stateOfGroup`). */
export function sheetItemsOf(
  sheets: readonly SheetGroup[],
  states: Readonly<Record<string, string>>,
): { code: string; name: string; state: string }[] {
  return sheets.map((s) => ({ code: s.key, name: s.name, state: stateOfGroup(s, states) }));
}

/**
 * Групує таблиці за аркушами.
 *
 * ⚠ Сервер віддає плоский перелік екземплярів: так його можна віддати одним
 * запитом і не вигадувати вкладену структуру, яка все одно розбирається на
 * клієнті. Копії аркуша (`sheetCopyId`) — окремі вкладки з тим самим кодом аркуша.
 */
export function groupBySheet(tables: DocumentTableDto[]): SheetGroup[] {
  const sheets = new Map<string, SheetGroup>();
  // Є копії — оригінал теж нумерується («Назва 1»).
  const withCopies = new Set(
    tables.filter((table) => copyFieldsOf(table).sheetCopyId !== null).map((table) => table.sheetCode),
  );

  for (const table of tables) {
    const copy = copyFieldsOf(table);
    const key = sheetTabKey(table.sheetCode, copy.sheetCopyId);
    // ✎ b4b (P1 приховані аркуші): назва — лише з даних API; немає — «—», а не код.
    const group = sheets.get(key) ?? {
      sheetDefId: table.sheetDefId,
      code: table.sheetCode,
      stateKey: sheetStateKey(table.sheetCode, copy.sheetCopyId),
      key,
      name: sheetTabLabel(
        groupNameOf(table, withCopies.has(table.sheetCode)),
        copy.copyOrdinal,
        copy.sheetCopyId !== null,
        withCopies.has(table.sheetCode),
      ),
      groupNameValues: groupNameValuesOf(table, withCopies.has(table.sheetCode)),
      ordinal: table.sheetOrdinal,
      copyOrdinal: copy.copyOrdinal,
      sheetCopyId: copy.sheetCopyId,
      isCopyable: copy.isCopyable,
      tables: [],
    };

    group.tables.push(table);
    sheets.set(key, group);
  }

  return [...sheets.values()].sort((a, b) => a.ordinal - b.ordinal || a.copyOrdinal - b.copyOrdinal);
}
