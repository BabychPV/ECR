import type { DocumentTableDto } from '@/api/types';
import { copyFieldsOf, groupNameOf, sheetTabKey, sheetTabLabel } from './sheetCopyTypes';

/** Вкладка документа: аркуш шаблону або його копія — з таблицями. */
export interface SheetGroup {
  sheetDefId: number;
  /** Код аркуша шаблону: ключ у `sheetStates`; у оригіналу й копій однаковий. */
  code: string;
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

/** Елемент смуги аркушів: `code` — ключ вкладки, стан береться за кодом аркуша. */
export function sheetItemsOf(
  sheets: readonly SheetGroup[],
  states: Readonly<Record<string, string>>,
): { code: string; name: string; state: string }[] {
  return sheets.map((s) => ({ code: s.key, name: s.name, state: states[s.code] ?? 'Draft' }));
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
      key,
      name: sheetTabLabel(groupNameOf(table), copy.copyOrdinal, copy.sheetCopyId !== null, withCopies.has(table.sheetCode)),
      groupNameValues: { ...((copy.displayNameL10n ?? table.sheetNameL10n).values ?? {}) },
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
