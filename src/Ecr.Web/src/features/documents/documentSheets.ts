import type { DocumentSummary } from '@/api/types';
import { localized } from '@/shared/i18n/localized';

/** Аркуш документа в переліку: код, людська назва, стан. */
export interface SheetLabel {
  readonly code: string;
  readonly label: string;
  readonly state: string;
}

type SheetSource = Pick<DocumentSummary, 'sheetStates' | 'sheets'>;

/**
 * Аркуші документа в ПОРЯДКУ аркушів і з назвою мовою інтерфейсу.
 *
 * ⚠ `sheets` (з назвою й порядком) — першим; без нього — пари словника
 * `sheetStates` з кодом замість назви. Назви немає жодною мовою — підпис падає
 * на код (`S99819007` краще, ніж порожнє місце).
 */
export function sheetLabels(document: SheetSource): SheetLabel[] {
  if (document.sheets !== undefined && document.sheets !== null && document.sheets.length > 0) {
    return document.sheets.map((sheet) => {
      const name = localized(sheet.nameL10n);

      return { code: sheet.code, label: name.length > 0 ? name : sheet.code, state: sheet.state };
    });
  }

  return Object.entries(document.sheetStates).map(([code, state]) => ({ code, label: code, state }));
}

/** Чи є в документа стани за обраний період (без періоду словник порожній, `D-93`). */
export function hasSheetStates(document: Pick<DocumentSummary, 'sheetStates'>): boolean {
  return Object.keys(document.sheetStates).length > 0;
}

/**
 * Порядок «найгіршого» стану — дзеркало `DocumentListSummaryStore`
 * (`DocumentListSummaryResponse`): `Rejected > Draft > Submitted > Approved`.
 */
const Worst = ['Rejected', 'Draft', 'Submitted', 'Approved'] as const;

/**
 * Стан ДОКУМЕНТА в переліку — найгірший зі станів його аркушів.
 *
 * ⛔ Скалярного стану документа на сервері немає (`D-93`); саме цим правилом
 * сервер рахує смугу лічильників над переліком і фільтр `state`. Колонка мусить
 * рахувати так само, інакше клік «2 Draft» показав би рядки з іншим станом.
 *
 * ⚠ Стан, якого правило не знає (новий стан сервера), повертається як є — і
 * `StatusBadge` позначить його «увагою», а не сховає за відомим словом.
 * Без станів (`null`) — період не обрано або станів за нього немає.
 */
export function documentState(document: Pick<DocumentSummary, 'sheetStates'>): string | null {
  const states = Object.values(document.sheetStates);
  if (states.length === 0) return null;

  const unknown = states.find((state) => !(Worst as readonly string[]).includes(state));
  if (unknown !== undefined) return unknown;

  return Worst.find((state) => states.includes(state)) ?? null;
}
