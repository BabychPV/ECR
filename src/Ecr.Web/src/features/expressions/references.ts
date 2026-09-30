import type { TemplateStructureDto } from '@/api/types';
import type { LocalizedText } from '@/shared/i18n/localized';

/**
 * Посилання на структуру шаблону в квадратних дужках (`02b` §3.1):
 * `[Sheet].[Table].[Row].[Col]`, `[Table].[Row].[Col]`, `[Row].[Col]`, `[Col]`.
 *
 * ⛔ Роль ланки визначається ПОЗИЦІЄЮ З КІНЦЯ, а не з початку — так само, як
 * її читає парсер (`Parser.cs`: остання ланка — колонка, передостання — рядок,
 * далі таблиця, аркуш). Тому під час набору, коли останньої ланки ще немає,
 * роль попередніх вгадується за даними: `[R1].[` — рядок поточної таблиці, отже
 * далі колонка; `[FT1].[` — таблиця, отже далі рядок.
 *
 * ⚠ Модуль чистий: жодного Monaco і жодного каталогу рядків. Перевіряється без
 * редактора, як і `completion.ts`.
 */

/** Колонка таблиці — те, що підставляється в останню ланку. */
export interface ColumnSymbol {
  readonly code: string;
  readonly name: string;
  readonly dataType: string;
  readonly unit: string | null;
}

/** Рядок таблиці з фіксованим набором рядків. */
export interface RowSymbol {
  readonly key: string;
  readonly label: string | null;
}

/** Таблиця шаблону. */
export interface TableSymbol {
  readonly id: number;
  readonly code: string;
  readonly name: string;
  readonly sheetCode: string;
  /** Динамічні рядки адресуються предикатом, а не ключем (`02b` §3.3 п. 3). */
  readonly dynamic: boolean;
  readonly columns: readonly ColumnSymbol[];
  readonly rows: readonly RowSymbol[];
}

/** Аркуш шаблону. */
export interface SheetSymbol {
  readonly code: string;
  readonly name: string;
  readonly tables: readonly TableSymbol[];
}

/** Структура версії шаблону в формі, придатній для підказок. */
export interface StructureIndex {
  readonly sheets: readonly SheetSymbol[];
  readonly tables: readonly TableSymbol[];
}

/**
 * Будує індекс підказок зі структури версії (`GET …/structure`).
 *
 * ⚠ Структуру не запитує: її вже тримає кеш сторінки
 * (`queryKeys.templates.version`). Похід на сервер за кожним `[` означав би,
 * що перелік з'являється через мережу — уже після того, як код дописали руками.
 *
 * @param dto Структура версії.
 * @param label Як показати локалізовану назву (мовою користувача).
 */
export function indexStructure(
  dto: TemplateStructureDto,
  label: (text: LocalizedText | null | undefined) => string,
): StructureIndex {
  const sheets: SheetSymbol[] = [...dto.sheets]
    .sort((a, b) => a.ordinal - b.ordinal)
    .map((sheet) => ({
      code: sheet.code,
      name: label(sheet.nameL10n),
      tables: [...sheet.tables]
        .sort((a, b) => a.ordinal - b.ordinal)
        .map((table) => ({
          id: table.id,
          code: table.code,
          name: label(table.nameL10n),
          sheetCode: sheet.code,
          dynamic: table.rowMode === 'Dynamic',
          columns: [...table.columns]
            .sort((a, b) => a.ordinal - b.ordinal)
            .map((column) => ({
              code: column.code,
              name: label(column.headerL10n),
              dataType: column.dataType,
              unit: column.unitSymbol,
            })),
          rows: [...table.rows]
            .sort((a, b) => a.ordinal - b.ordinal)
            .map((row) => ({ key: row.rowKey, label: row.label })),
        })),
    }));

  return { sheets, tables: sheets.flatMap((s) => s.tables) };
}

/** Відкрита квадратна дужка під курсором. */
export interface BracketContext {
  /** Попередні ланки ланцюжка — тексти без дужок, зліва направо. */
  readonly segments: readonly string[];
  /** Уже набране всередині поточної ланки. */
  readonly typed: string;
  /** Зміщення першого символу всередині поточної дужки. */
  readonly replaceFrom: number;
  /** Чи стоїть одразу за курсором `]` (її поставило автозакриття). */
  readonly closed: boolean;
}

/** Що може стояти всередині ланки під час набору: код, ключ, діапазон, `{Month}`, зсув періоду. */
const SegmentText = /^[\w\-{}:+]*$/;

/**
 * Знаходить ланку посилання, яку зараз набирають.
 *
 * @returns `null` — курсор не всередині `[...]` (або всередині предиката
 * `[WHERE …]`, де пишуть вираз, а не код).
 */
export function bracketAt(text: string, offset: number): BracketContext | null {
  const end = Math.max(0, Math.min(offset, text.length));
  const open = unmatchedOpen(text, end);
  if (open < 0) return null;

  const typed = text.slice(open + 1, end);
  if (!SegmentText.test(typed)) return null;

  return {
    segments: segmentsBefore(text, open),
    typed,
    replaceFrom: open + 1,
    closed: text[end] === ']',
  };
}

/**
 * Ланка, всередині якої стоїть позиція, — цілком, з обох боків курсора.
 *
 * ⚠ Потрібна наведенню: там курсор може стояти посередині коду.
 */
export function segmentAround(
  text: string,
  offset: number,
): { readonly segments: readonly string[]; readonly text: string; readonly from: number; readonly to: number } | null {
  const context = bracketAt(text, offset);
  if (context === null) return null;

  const close = text.indexOf(']', offset);
  if (close < 0) return null;

  const whole = text.slice(context.replaceFrom, close);
  if (!SegmentText.test(whole)) return null;

  return { segments: context.segments, text: whole, from: context.replaceFrom, to: close };
}

function unmatchedOpen(text: string, end: number): number {
  let depth = 0;

  for (let i = end - 1; i >= 0; i--) {
    const symbol = text[i];

    if (symbol === ']') depth++;
    else if (symbol === '[') {
      if (depth === 0) return i;
      depth--;
    }
  }

  return -1;
}

/** Попередні ланки `[a].[b].` перед відкритою дужкою в позиції `open`. */
function segmentsBefore(text: string, open: number): string[] {
  const segments: string[] = [];
  let j = open - 1;

  // ⚠ Більше чотирьох ланок у мові не буває (`02b` §1); п'ята — вже не посилання.
  while (segments.length < 5 && text[j] === '.' && text[j - 1] === ']') {
    const close = j - 1;
    const start = unmatchedOpen(text, close);
    if (start < 0) break;

    segments.unshift(text.slice(start + 1, close));
    j = start - 1;
  }

  return segments;
}

/** Що очікується в поточній ланці. */
export type Slot =
  | { readonly kind: 'first'; readonly table: TableSymbol | undefined }
  | { readonly kind: 'table'; readonly sheet: SheetSymbol }
  | { readonly kind: 'row'; readonly table: TableSymbol }
  | { readonly kind: 'column'; readonly table: TableSymbol }
  | { readonly kind: 'none' };

/** Перша ланка `[Period]`, `[Period:-1]` — зсув періоду, а не частина адреси (`02b` §3.2). */
const PeriodSegment = /^Period(\s*:\s*[+-]?\d+)?$/i;

/**
 * Роль наступної ланки після `segments`.
 *
 * @param index Структура версії.
 * @param segments Попередні ланки.
 * @param currentTableId Таблиця, у якій живе вираз; `undefined` — невідома.
 */
export function slotAfter(
  index: StructureIndex,
  segments: readonly string[],
  currentTableId: number | undefined,
): Slot {
  const current = index.tables.find((t) => t.id === currentTableId);
  const chain = segments.length > 0 && PeriodSegment.test(segments[0] ?? '') ? segments.slice(1) : segments;

  let slot: Slot = { kind: 'first', table: current };

  for (const segment of chain) {
    slot = step(index, slot, segment, current);
    if (slot.kind === 'none') break;
  }

  return slot;
}

function step(
  index: StructureIndex,
  slot: Slot,
  segment: string,
  current: TableSymbol | undefined,
): Slot {
  switch (slot.kind) {
    case 'first': {
      // ⛔ Рядок поточної таблиці — ПЕРШИМ: `[R1].[` у формулі колонки
      // найчастіша форма, і таблиця з кодом `R1` не має її перехоплювати.
      if (current !== undefined && isRowSelector(current, segment)) {
        return { kind: 'column', table: current };
      }

      const table = findTable(index, segment, current?.sheetCode);
      if (table !== undefined) return { kind: 'row', table };

      const sheet = index.sheets.find((s) => same(s.code, segment));
      if (sheet !== undefined) return { kind: 'table', sheet };

      return { kind: 'none' };
    }

    case 'table': {
      const table = slot.sheet.tables.find((t) => same(t.code, segment));
      return table === undefined ? { kind: 'none' } : { kind: 'row', table };
    }

    case 'row':
      // ⚠ Рядок приймається будь-який: діапазон `a:b`, предикат `WHERE …`,
      // ключ, якого ще немає в чернетці. Перевірить його публікація; підказці
      // досить знати, що далі — колонка цієї таблиці.
      return { kind: 'column', table: slot.table };

    default:
      return { kind: 'none' };
  }
}

function isRowSelector(table: TableSymbol, segment: string): boolean {
  if (table.rows.some((r) => same(r.key, segment))) return true;

  // Діапазон рядків і предикат — теж рядок, хоч і не ключ.
  return segment.includes(':') || /^WHERE\b/i.test(segment);
}

function findTable(
  index: StructureIndex,
  code: string,
  sheetCode: string | undefined,
): TableSymbol | undefined {
  // Та сама таблиця в тому самому аркуші — першою (`02b` §3.1, `[Main].[…]`).
  return (
    index.tables.find((t) => t.sheetCode === sheetCode && same(t.code, code)) ??
    index.tables.find((t) => same(t.code, code))
  );
}

function same(a: string, b: string): boolean {
  return a.toUpperCase() === b.toUpperCase();
}
