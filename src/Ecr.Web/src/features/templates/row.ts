import type { components } from '@/api/schema';
import type { TableDto } from '@/api/types';
import type { LocalizedValue } from '@/shared/ui/LocalizedInput';

/**
 * Чернетка рядка фіксованої таблиці в редакторі (`W5.2`, третій вертикальний
 * зріз авторства структури шаблону — за зразком `sheet.ts` (`ФВ-2.1`, `W5.0`)
 * і `column.ts`).
 */

/** Псевдоніми згенерованих типів (`D-137`) — див. коментар у `column.ts`. */
type Schemas = components['schemas'];

/**
 * Рядок у СТРУКТУРІ версії (`GET …/structure`), яким описано існуючий рядок
 * для списку в `TemplateVersionPage`.
 *
 * ⛔ Індексний доступ до вже згенерованого `TableDto['rows']`, а не окремий
 * псевдонім через `Schemas['TemplateRowDto']`: `src/api/types.ts` не в SCOPE
 * цього зрізу, а сам тип уже існує в схемі (структуру версії з рядками
 * читає `GET …/structure` відтоді, як з'явився `GetTemplateStructureHandler`).
 */
export type TemplateRowDto = TableDto['rows'][number];

/** Роль рядка — дзеркалить `Ecr.Domain.Enums.RowKind`. */
export type RowKind = Schemas['RowKind'];

export const RowKindOptions: readonly RowKind[] = ['Group', 'Item', 'Balance', 'Note', 'Header'];

/** Рядок у відповіді `PUT …/tables/{tableId}/rows/{code}`. */
export type RowDefDto = Schemas['RowDefDto'];

/** Тіло запиту `PUT …/tables/{tableId}/rows/{code}`. */
export type SaveRowDefRequest = Schemas['SaveRowDefRequest'];

/**
 * Чернетка рядка в редакторі.
 *
 * ⚠ `TemplateRowDto.label` — це РОЗВ'ЯЗАНИЙ підпис ОДНІЄЮ мовою
 * (`GetTemplateStructureHandler.Row`: `row.LabelL10n.Get("en")`), не повний
 * `LabelL10n` усіма мовами каталогу — на відміну від `SheetDto.nameL10n`.
 * Тому чернетку рядка, який у ЦЬОМУ СЕАНСІ ще не зберігали через цей
 * редактор, `rowDraftOf` будує З ОДНІЄЮ мовою — і позначає `hasFullLabel:
 * false`, щоб форма попередила: збереження без правки інших мов зітре їх
 * (`PUT` — заміна цілком, `D2-147`; часткового патча в маршруту нема). Той
 * самий компроміс, що й `hasFullData` у `ColumnDraft`.
 */
export interface RowDraft {
  readonly rowKey: string;
  readonly labelL10n: LocalizedValue;
  readonly ordinal: number | null;
  readonly rowKind: RowKind;
  readonly parentRowKey: string;
  readonly isReadOnly: boolean;
  readonly isNew: boolean;

  /** `false` — `labelL10n` тут лише мовою інтерфейсу, не всіма (див. коментар типу). */
  readonly hasFullLabel: boolean;
}

/** Порожня чернетка нового рядка. */
export function emptyRowDraft(nextOrdinal: number): RowDraft {
  return {
    rowKey: '',
    labelL10n: {},
    ordinal: nextOrdinal,
    rowKind: 'Item',
    parentRowKey: '',
    isReadOnly: false,
    isNew: true,
    hasFullLabel: true,
  };
}

/**
 * Чернетка з наявного рядка — для правки.
 *
 * @param row Опис рядка зі структури версії.
 * @param full Повна відповідь цього PUT з ЦЬОГО сеансу, якщо рядок уже
 * зберігали через цей редактор відтоді, як сторінку відкрили.
 */
export function rowDraftOf(row: TemplateRowDto, full?: RowDefDto): RowDraft {
  if (full !== undefined) {
    return {
      rowKey: full.rowKey,
      labelL10n: { ...(full.labelL10n.values ?? {}) },
      ordinal: full.ordinal,
      rowKind: full.rowKind,
      parentRowKey: full.parentRowKey ?? '',
      isReadOnly: full.isReadOnly,
      isNew: false,
      hasFullLabel: true,
    };
  }

  return {
    rowKey: row.rowKey,
    labelL10n: row.label === null || row.label.length === 0 ? {} : { en: row.label },
    ordinal: row.ordinal,
    rowKind: row.rowKind as RowKind,
    parentRowKey: row.parentRowKey ?? '',
    isReadOnly: row.isReadOnly,
    isNew: false,
    hasFullLabel: false,
  };
}

/** Що саме заважає зберегти чернетку. */
export type RowBlocker = 'RowKey' | 'Label';

/** Чому чернетку ще не можна зберегти; `null` — можна. */
export function whyCannotSaveRow(draft: RowDraft): RowBlocker | null {
  if (draft.rowKey.trim().length === 0) return 'RowKey';
  if (!/^[A-Za-z0-9_.-]{1,100}$/.test(draft.rowKey)) return 'RowKey';

  if (Object.values(draft.labelL10n).every((text) => text.trim().length === 0)) return 'Label';

  return null;
}

/** Тіло запиту `PUT …/tables/{tableId}/rows/{code}`. */
export function rowBody(draft: RowDraft): SaveRowDefRequest {
  return {
    labelL10n: draft.labelL10n,
    ordinal: draft.ordinal,
    rowKind: draft.rowKind,
    parentRowKey: draft.parentRowKey.trim().length === 0 ? null : draft.parentRowKey.trim(),
    isReadOnly: draft.isReadOnly,
  };
}
