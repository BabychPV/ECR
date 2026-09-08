import type { SheetDto } from '@/api/types';
import type { LocalizedValue } from '@/shared/ui/LocalizedInput';

/**
 * Чернетка аркуша в редакторі (`ФВ-2.1`).
 *
 * ⚠ Окремий тип від `SheetDto`. У DTO є `id` і `tables` — обидва рахує
 * сервер, і форма ними не керує: `tables` заводить наступний зріз
 * (`TableDef`), а `id` з'являється лише після збереження.
 */
export interface SheetDraft {
  /** Код аркуша; після створення не змінюється — це його адреса в API. */
  readonly code: string;
  readonly nameL10n: LocalizedValue;
  readonly ordinal: number | null;
  readonly sheetGroup: string;
  readonly isMandatory: boolean;
  readonly isVisible: boolean;

  /** Чи це нова чернетка: код нової ще можна набрати. */
  readonly isNew: boolean;
}

/** Порожня чернетка нового аркуша. */
export function emptyDraft(nextOrdinal: number): SheetDraft {
  return {
    code: '',
    nameL10n: {},
    ordinal: nextOrdinal,
    sheetGroup: '',
    isMandatory: false,
    isVisible: true,
    isNew: true,
  };
}

/** Чернетка з наявного аркуша — для правки. */
export function draftOf(sheet: SheetDto): SheetDraft {
  return {
    code: sheet.code,
    nameL10n: { ...(sheet.nameL10n.values ?? {}) },
    ordinal: sheet.ordinal,
    sheetGroup: sheet.sheetGroup ?? '',
    isMandatory: sheet.isMandatory,
    isVisible: sheet.isVisible,
    isNew: false,
  };
}

/** Що саме заважає зберегти чернетку. */
export type SheetBlocker = 'Code' | 'Name';

/**
 * Чому чернетку ще не можна зберегти; `null` — можна.
 *
 * ⛔ Не копія серверних правил, а те саме питання, поставлене раніше: сервер
 * відхиляє недопустимий код `ECR-CFG-0422` і сам, форма лише не везе в
 * мережу те, що напевно повернеться відмовою.
 */
export function whyCannotSave(draft: SheetDraft): SheetBlocker | null {
  if (draft.code.trim().length === 0) return 'Code';
  if (!/^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(draft.code)) return 'Code';

  if (Object.values(draft.nameL10n).every((text) => text.trim().length === 0)) return 'Name';

  return null;
}
