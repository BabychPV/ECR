import type { components } from '@/api/schema';
import type { LocalizedValue } from '@/shared/ui/LocalizedInput';
import { type CellDataType } from './column';

/**
 * Чернетка поля шапки документа версії шаблону (той самий draft→publish
 * контракт, що `ColumnDef`/`column.ts`, W5.2) — рівень усього документа, не
 * таблиці: `Template.View` читає, `Template.Edit` пише
 * `PUT …/header-fields/{code}`.
 *
 * ⚠ На відміну від колонки (`ColumnDraft`, `Q-012`): `GET …/header-fields`
 * віддає той самий повний `HeaderFieldDefDto`, який приймає і повертає `PUT`
 * — тобто немає різниці «бідна структура проти повного PUT», і чернетці не
 * потрібні ні `hasFullData`, ні кеш сеансу (`savedColumns` у
 * `TemplateVersionPage.tsx`). Немає й розширених полів колонки (стиль,
 * точність/масштаб, формат показу, одиниця, значення за замовчуванням) —
 * шапка документа їх не носить (`HeaderFieldDef.cs`).
 */

type Schemas = components['schemas'];

/** Поле шапки у відповіді `GET …/header-fields` і `PUT …/header-fields/{code}`. */
export type HeaderFieldDefDto = Schemas['HeaderFieldDefDto'];

/** Тіло запиту `PUT …/header-fields/{code}`. */
export type SaveHeaderFieldDefRequest = Schemas['SaveHeaderFieldDefRequest'];

export interface HeaderFieldDraft {
  readonly code: string;
  readonly labelL10n: LocalizedValue;
  readonly ordinal: number | null;
  readonly dataType: CellDataType;
  readonly isRequired: boolean;

  /**
   * Довідник для типу `Lookup`; для решти типів завжди `null` —
   * `headerFieldBody` нижче не надсилає його, навіть якщо форма встигла
   * зберегти стале значення після перемикання типу (`HeaderFieldDef.SetLookup`
   * на сервері й так відхилив би це `ECR-TMPL-0422`).
   */
  readonly lookupRegistryDefId: number | null;

  readonly isNew: boolean;
}

/** Порожня чернетка нового поля шапки. */
export function emptyHeaderFieldDraft(nextOrdinal: number | null): HeaderFieldDraft {
  return {
    code: '',
    labelL10n: {},
    ordinal: nextOrdinal,
    dataType: 'String',
    isRequired: false,
    lookupRegistryDefId: null,
    isNew: true,
  };
}

/** Чернетка з наявного поля шапки — для правки. */
export function headerFieldDraftOf(field: HeaderFieldDefDto): HeaderFieldDraft {
  return {
    code: field.code,
    labelL10n: { ...(field.labelL10n.values ?? {}) },
    ordinal: field.ordinal,
    dataType: field.dataType,
    isRequired: field.isRequired,
    lookupRegistryDefId: field.lookupRegistryDefId,
    isNew: false,
  };
}

/**
 * Що саме заважає зберегти чернетку.
 *
 * ⚠ `LookupRequired`, а не мовчазне надсилання `null`: `HeaderFieldDef`
 * дозволяє поле типу `Lookup` без довідника лише в домені (`SetLookup`
 * викликається окремо), але форма, що показує вибір довідника й нічого не
 * обирає, повертала б це як «зберегла», хоча жодного довідника поле так і не
 * отримало б.
 */
export type HeaderFieldBlocker = 'CodeEmpty' | 'CodeInvalid' | 'Label' | 'LookupRequired';

/** Чому чернетку ще не можна зберегти; `null` — можна. */
export function whyCannotSaveHeaderField(draft: HeaderFieldDraft): HeaderFieldBlocker | null {
  if (draft.code.trim().length === 0) return 'CodeEmpty';
  if (!/^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(draft.code)) return 'CodeInvalid';

  if (Object.values(draft.labelL10n).every((text) => text.trim().length === 0)) return 'Label';

  if (draft.dataType === 'Lookup' && draft.lookupRegistryDefId === null) return 'LookupRequired';

  return null;
}

/** Тіло запиту `PUT …/header-fields/{code}`. */
export function headerFieldBody(draft: HeaderFieldDraft): SaveHeaderFieldDefRequest {
  return {
    labelL10n: draft.labelL10n,
    ordinal: draft.ordinal,
    dataType: draft.dataType,
    isRequired: draft.isRequired,

    // ⛔ Лише для Lookup — інакше стале значення з форми (перемкнули тип, не
    // обнулили вибір довідника) поїхало б у PUT поля, якому довідник узагалі
    // не належить (мутаційний доказ: прибрати цю умову — тест ловить).
    lookupRegistryDefId: draft.dataType === 'Lookup' ? draft.lookupRegistryDefId : null,
  };
}
