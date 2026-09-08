import type { TableDto } from '@/api/types';
import type { LocalizedText } from '@/shared/i18n/localized';
import type { LocalizedValue } from '@/shared/ui/LocalizedInput';

/**
 * Таблиця у структурі версії — з назвою й порядком (`W5.1`).
 *
 * ⛔ Розширює згенерований `TableDto`, а не замінює його. Сервер уже віддає
 * `nameL10n` і `ordinal` (W5.1 додав їх у `TableDto` саме для того, щоб
 * редактор міг попередньо заповнити форму — без цього поля другий `PUT`
 * тим самим кодом стирав би назву мовчки), але `schema.d.ts` генерується
 * окремим кроком (`npm run api:types` проти живого OpenAPI знімка) і в цьому
 * зрізі не перегенерований. Це той самий виняток, що й `CellConflictDto` в
 * `api/types.ts`: тимчасове ручне оголошення там, де згенерований тип ще не
 * встиг за сервером.
 */
export type TableStructureDto = TableDto & {
  readonly nameL10n: LocalizedText;
  readonly ordinal: number;
};

/**
 * Чернетка таблиці в редакторі (`W5.1`).
 *
 * ⚠ Окремий тип від {@link TableStructureDto} — той самий довід, що й
 * `SheetDraft` проти `SheetDto`: у DTO є `id`, `columns` і `rows`, усі три
 * рахує сервер, і форма ними не керує.
 */
export interface TableDraft {
  /** Код таблиці; після створення не змінюється — це його адреса в API. */
  readonly code: string;
  readonly nameL10n: LocalizedValue;
  readonly ordinal: number | null;
  readonly layoutKind: TableDto['layoutKind'];
  readonly rowMode: TableDto['rowMode'];
  readonly maxDynamicRows: number | null;

  /** Чи це нова чернетка: код нової ще можна набрати. */
  readonly isNew: boolean;
}

/**
 * Розкладки — рівно ті, що є в переліку домену.
 *
 * ⛔ Перелік виписаний, а не зібраний із рядка: підпис
 * `t(\`tableDef.layout${kind}\`)` невидимий для сторожа «кожен рядок, якого
 * просить клієнт, є в каталозі» (`D2-172`).
 */
export const TableLayoutKinds: readonly TableDto['layoutKind'][] = [
  'MonthsInColumns',
  'MonthsInRows',
  'Static',
  'PerPeriodInstance',
];

/** Способи формування рядків — та сама причина виписаного переліку. */
export const TableRowModes: readonly TableDto['rowMode'][] = ['Fixed', 'Dynamic', 'Mixed'];

/** Порожня чернетка нової таблиці. */
export function emptyDraft(nextOrdinal: number): TableDraft {
  return {
    code: '',
    nameL10n: {},
    ordinal: nextOrdinal,
    layoutKind: 'PerPeriodInstance',
    rowMode: 'Fixed',
    maxDynamicRows: null,
    isNew: true,
  };
}

/** Чернетка з наявної таблиці — для правки. */
export function draftOf(table: TableStructureDto): TableDraft {
  return {
    code: table.code,
    nameL10n: { ...(table.nameL10n.values ?? {}) },
    ordinal: table.ordinal,
    layoutKind: table.layoutKind,
    rowMode: table.rowMode,
    maxDynamicRows: table.maxDynamicRows,
    isNew: false,
  };
}

/** Що саме заважає зберегти чернетку. */
export type TableBlocker = 'Code' | 'Name';

/**
 * Чому чернетку ще не можна зберегти; `null` — можна.
 *
 * ⛔ Не копія серверних правил (наприклад, стелі рядків для нединамічної
 * таблиці) — те саме питання, поставлене раніше, тим самим набором перевірок,
 * що й `SheetDraft.whyCannotSave`: сервер відхиляє недопустимий код і сам.
 */
export function whyCannotSave(draft: TableDraft): TableBlocker | null {
  if (draft.code.trim().length === 0) return 'Code';
  if (!/^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(draft.code)) return 'Code';

  if (Object.values(draft.nameL10n).every((text) => text.trim().length === 0)) return 'Name';

  return null;
}
