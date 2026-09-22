import type { components } from '@/api/schema';
import type { TemplateColumnDto } from '@/api/types';
import type { LocalizedValue } from '@/shared/ui/LocalizedInput';
import { normalizeDecimal } from '@/shared/format';
import { type StyleDraft, whyCannotSaveStyle } from './style';

/**
 * Чернетка колонки таблиці в редакторі (`W5.2`, другий вертикальний зріз
 * авторства структури шаблону — за зразком `sheet.ts` (`ФВ-2.1`, `W5.0`)).
 */

/**
 * Псевдоніми згенерованих типів (`D-137`, `src/api/types.ts`).
 *
 * ⚠ Не через `src/api/types.ts`: цей файл поза SCOPE зрізу W5.2 (див. звіт
 * агента), тому псевдоніми названо тут — так само законно, як і там, бо
 * `type X = Schemas['X']` не оголошує форми, а лише коротко називає вже
 * оголошену в `schema.d.ts` (лінт-правило дозволяє саме це).
 */
type Schemas = components['schemas'];

/** Тип даних колонки — дзеркалить `Ecr.Domain.Enums.CellDataType`. */
export type CellDataType = Schemas['CellDataType'];

/**
 * Типи, які людина обирає у формі. `Formula`/`Calculated` рахує рушій
 * (`ColumnDef.IsComputed`) — колонку такого типу заводить не ця форма.
 */
export const EditableDataTypes: readonly CellDataType[] = [
  'String',
  'Int',
  'Decimal',
  'Bool',
  'Date',
  'Lookup',
  'Unit',
];

/** Колонка у відповіді `PUT …/tables/{tableId}/columns/{code}`. */
export type ColumnDefDto = Schemas['ColumnDefDto'];

/** Тіло запиту `PUT …/tables/{tableId}/columns/{code}`. */
export type SaveColumnDefRequest = Schemas['SaveColumnDefRequest'];

/**
 * Чернетка колонки в редакторі.
 *
 * ⚠ `precision`/`scale`/`defaultValue`/`lookupRegistryDefId`/`unitId` —
 * розширені поля. `TemplateColumnDto` (структура версії,
 * `Dto/TemplateStructureDto.cs`) навмисно бідніший — він для екрана
 * презентаційної правки й цих полів не носить (`Q-012`). Тому чернетку
 * існуючої колонки, яку в ЦЬОМУ СЕАНСІ ще не зберігали через цей редактор,
 * будує `columnDraftOf` БЕЗ них — і позначає це `hasFullData: false`, щоб
 * форма попередила людину, а не тихо скинула лукап чи точність на `PUT`
 * (`PUT` — заміна цілком, `D2-147`; часткового патча в цього маршруту нема).
 */
export interface ColumnDraft {
  readonly code: string;
  readonly headerL10n: LocalizedValue;
  readonly ordinal: number | null;
  readonly dataType: CellDataType;
  readonly isRequired: boolean;
  readonly isReadOnly: boolean;
  readonly isHidden: boolean;
  readonly precision: number | null;
  readonly scale: number | null;
  readonly defaultValue: string;
  readonly displayFormat: string;
  readonly lookupRegistryDefId: number | null;
  readonly unitId: number | null;
  readonly isNew: boolean;

  /**
   * Стиль показу (директива registry-lookup / cell-style, Частина B, PR
   * B1) — ідентифікатор уже ЗБЕРЕЖЕНОГО `StyleDef`; `null` — колонка без
   * стилю. Незалежний від {@link style} нижче: це те, що сервер уже знає,
   * `style` — те, що редагує ця сесія форми ПРЯМО ЗАРАЗ.
   */
  readonly styleId: number | null;

  /**
   * Чернетка стилю, що редагується РАЗОМ із колонкою в цій формі; `null` —
   * перемикач «власний стиль» вимкнено (колонка лишається без стилю, або зі
   * старим {@link styleId}, якщо він був). Не персиститься сама по собі:
   * `saveColumn` (`columnApi.ts`) спершу зберігає ЇЇ (`PUT …/styles/{code}`),
   * і лише потім — колонку з отриманим `styleId`.
   */
  readonly style: StyleDraft | null;

  /** `false` — розширені поля вище невідомі клієнту (див. коментар типу). */
  readonly hasFullData: boolean;
}

/** Порожня чернетка нової колонки. */
export function emptyColumnDraft(nextOrdinal: number): ColumnDraft {
  return {
    code: '',
    headerL10n: {},
    ordinal: nextOrdinal,
    dataType: 'Decimal',
    isRequired: false,
    isReadOnly: false,
    isHidden: false,
    precision: null,
    scale: null,
    defaultValue: '',
    displayFormat: '',
    lookupRegistryDefId: null,
    unitId: null,
    styleId: null,
    style: null,
    isNew: true,
    hasFullData: true,
  };
}

/**
 * Чернетка з наявної колонки — для правки.
 *
 * @param column Опис колонки зі структури версії (`GET …/structure`).
 * @param full Повна відповідь цього PUT з ЦЬОГО сеансу, якщо колонку вже
 * зберігали через цей редактор відтоді, як сторінку відкрили. Коли є —
 * розширені поля читаються звідси, а не губляться.
 */
export function columnDraftOf(column: TemplateColumnDto, full?: ColumnDefDto): ColumnDraft {
  if (full !== undefined) {
    return {
      code: full.code,
      headerL10n: { ...(full.headerL10n.values ?? {}) },
      ordinal: full.ordinal,
      dataType: full.dataType,
      isRequired: full.isRequired,
      isReadOnly: full.isReadOnly,
      isHidden: full.isHidden,
      precision: full.precision,
      scale: full.scale,
      defaultValue: full.defaultValue ?? '',
      displayFormat: full.displayFormat ?? '',
      lookupRegistryDefId: full.lookupRegistryDefId,
      unitId: full.unitId,
      styleId: full.styleId,
      style: null,
      isNew: false,
      hasFullData: true,
    };
  }

  return {
    code: column.code,
    headerL10n: { ...(column.headerL10n.values ?? {}) },
    ordinal: column.ordinal,
    dataType: column.dataType as CellDataType,
    isRequired: column.isRequired,
    isReadOnly: column.isReadOnly,
    isHidden: column.isHidden,
    precision: null,
    scale: null,
    defaultValue: '',
    displayFormat: column.displayFormat ?? '',
    lookupRegistryDefId: null,
    unitId: null,
    styleId: null,
    style: null,
    isNew: false,
    hasFullData: false,
  };
}

/**
 * Що саме заважає зберегти чернетку.
 *
 * ⛔ Q-336 (`table.ts`) розділила той самий блокувальник на `CodeEmpty` і
 * `CodeInvalid` — тут та сама причина: `'Code'` одним значенням покривало і
 * порожнє поле, і код із недопустимими символами, тож повідомлення «дай коду
 * код» не мінялося, коли причина насправді була в символах, а не в порожньому
 * полі.
 */
export type ColumnBlocker =
  | 'CodeEmpty'
  | 'CodeInvalid'
  | 'Header'
  | 'Scale'
  | 'StyleCode'
  | 'StyleFontSize';

/** Чому чернетку ще не можна зберегти; `null` — можна. */
export function whyCannotSaveColumn(draft: ColumnDraft): ColumnBlocker | null {
  if (draft.code.trim().length === 0) return 'CodeEmpty';
  if (!/^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(draft.code)) return 'CodeInvalid';

  if (Object.values(draft.headerL10n).every((text) => text.trim().length === 0)) return 'Header';

  // ⚠ Дзеркалить `ColumnDef.SetNumericFormat`: сервер відхилив би те саме,
  // форма лише не везе в мережу те, що напевно повернеться відмовою.
  if (draft.precision !== null && draft.scale !== null && draft.scale > draft.precision) return 'Scale';

  // ⛔ Директива registry-lookup / cell-style, PR B1: перемикач «власний
  // стиль» увімкнено (`draft.style !== null`), і код стилю ще недійсний —
  // збереження колонки заблоковане РАЗОМ зі стилем: `saveColumn` шле стиль
  // ПЕРШИМ (`columnApi.ts`), і недійсний код там дав би сиру відмову сервера
  // замість цієї, зрозумілої одразу на формі.
  if (draft.style !== null && whyCannotSaveStyle(draft.style) !== null) return 'StyleCode';

  /*
   * ⛔ Розмір шрифту — `decimal` контракту, і поле тепер текстове
   * (`StyleEditor.tsx`): «не число» стало можливим станом, якого `NumberInput`
   * просто не давав ввести. Без цієї перевірки `12pt` поїхало б у
   * `PUT …/styles/{code}` і повернулося сирою відмовою сервера — а `saveColumn`
   * шле стиль ПЕРШИМ (`columnApi.ts`), тож колонка не збереглася б теж.
   *
   * ⚠ `fontSize === null` сюди не потрапляє навмисно: `null` контракт читає як
   * «розмір теми за замовчуванням», тобто це заповнене значення, а не порожнеча.
   *
   * ⚠ Перевірка тут, а не у `whyCannotSaveStyle`: той файл зараз править інша
   * сесія (див. звіт), і дві правки в одному файлі коштували б конфлікту.
   */
  if (
    draft.style !== null &&
    draft.style.fontSize !== null &&
    normalizeDecimal(draft.style.fontSize) === null
  ) {
    return 'StyleFontSize';
  }

  return null;
}

/** Тіло запиту `PUT …/tables/{tableId}/columns/{code}`. */
export function columnBody(draft: ColumnDraft): SaveColumnDefRequest {
  return {
    headerL10n: draft.headerL10n,
    ordinal: draft.ordinal,
    dataType: draft.dataType,
    isRequired: draft.isRequired,
    isReadOnly: draft.isReadOnly,
    isHidden: draft.isHidden,
    precision: draft.precision,
    scale: draft.scale,
    defaultValue: draft.defaultValue.trim().length === 0 ? null : draft.defaultValue.trim(),
    displayFormat: draft.displayFormat.trim().length === 0 ? null : draft.displayFormat.trim(),

    // ⛔ Директива registry-lookup / cell-style, PR B1: раніше сюди завжди
    // йшов `null` — жоден код у застосунку не міг записати `ColumnDef.StyleId`
    // взагалі, бо не було чим його ЗАВЕСТИ. `saveColumn` (`columnApi.ts`)
    // підставляє СПРАВЖНІй, щойно збережений `styleId` замість цього поля,
    // коли форма несе чернетку стилю (`draft.style !== null`) — тут лишається
    // те, що вже персистентне (наявний `styleId`, або `null`, якщо стилю
    // ніколи не було).
    styleId: draft.styleId,
    lookupRegistryDefId: draft.lookupRegistryDefId,
    lookupFilter: null,
    unitId: draft.unitId,
  };
}
