import type { ExpressionFunctionDto, ExpressionSymbolDto } from '@/api/types';
import type { EditorSymbols } from './completion';
import {
  segmentAround,
  slotAfter,
  type ColumnSymbol,
  type RowSymbol,
  type SheetSymbol,
  type TableSymbol,
} from './references';

/**
 * Довідка під курсором миші — та сама, що й у переліку доповнення.
 *
 * ⛔ Чиста функція, як і `completionAt`: вона вирішує, ЩО стоїть під курсором,
 * а не як це намалювати. Друге робить `describe.ts`, і саме тому підказка в
 * переліку та наведення не можуть розійтися.
 */

/** Символ, на який навели. */
export type HoverSymbol =
  | { readonly kind: 'function'; readonly fn: ExpressionFunctionDto }
  | {
      readonly kind: 'constant' | 'formula' | 'argument' | 'header';
      readonly symbol: ExpressionSymbolDto;
    }
  | { readonly kind: 'column'; readonly column: ColumnSymbol; readonly table: TableSymbol }
  | { readonly kind: 'row'; readonly row: RowSymbol; readonly table: TableSymbol }
  | { readonly kind: 'table'; readonly table: TableSymbol }
  | { readonly kind: 'sheet'; readonly sheet: SheetSymbol };

/** Що показати і який фрагмент тексту підсвітити. */
export interface HoverInfo {
  readonly from: number;
  readonly to: number;
  readonly symbol: HoverSymbol;
}

/**
 * Символ у позиції.
 *
 * @returns `null` — під курсором нічого відомого (число, оператор, ім'я,
 * якого немає в складі мови). Вигадувати довідку для невідомого не можна:
 * вона виглядала б як підтвердження, що ім'я правильне.
 */
export function hoverAt(text: string, offset: number, symbols: EditorSymbols): HoverInfo | null {
  if (symbols.dialect !== 'Methodology') {
    const cell = cellAt(text, offset, symbols);
    if (cell !== null) return cell;
  }

  const metadata = symbols.metadata;
  if (metadata === undefined) return null;

  const word = wordAt(text, offset);
  if (word === null) return null;

  const { from, to } = word;
  const before = text.slice(0, from);

  const find = (list: readonly ExpressionSymbolDto[], name: string): ExpressionSymbolDto | undefined =>
    list.find((s) => s.name === name) ?? list.find((s) => s.name.toUpperCase() === name.toUpperCase());

  const symbolHover = (
    kind: 'constant' | 'formula' | 'argument' | 'header',
    list: readonly ExpressionSymbolDto[],
    name: string,
    start: number,
  ): HoverInfo | null => {
    const symbol = find(list, name);
    return symbol === undefined ? null : { from: start, to, symbol: { kind, symbol } };
  };

  const name = text.slice(from, to);
  const methodology = symbols.dialect !== 'Template';

  if (methodology && before.endsWith('@')) {
    return symbolHover('argument', metadata.arguments, name, from - 1);
  }

  if (methodology && before.endsWith('!')) {
    return symbolHover('formula', metadata.formulas, name, from - 1);
  }

  const dot = name.indexOf('.');
  const head = dot < 0 ? name : name.slice(0, dot);
  const tail = dot < 0 ? '' : name.slice(dot + 1);

  if (methodology && head.toUpperCase() === 'CST' && tail !== '') {
    return symbolHover('constant', metadata.constants, tail, from);
  }

  if (head.toUpperCase() === 'HDR' && tail !== '') {
    return symbolHover('header', metadata.headers, tail, from);
  }

  if (dot >= 0) return null;

  // ⛔ Спершу ТОЧНИЙ збіг, потім без регістру — з тієї самої причини, що й у
  // підказці сигнатури (`monaco.ts`): у діалекті методологій регістр значущий.
  const fn =
    metadata.functions.find((f) => f.name === name) ??
    metadata.functions.find((f) => f.name.toUpperCase() === name.toUpperCase());

  return fn === undefined ? null : { from, to, symbol: { kind: 'function', fn } };
}

/** Слово під курсором: ім'я разом із крапками (`CST.X`, `@A.B`). */
function wordAt(text: string, offset: number): { from: number; to: number } | null {
  const isWord = (ch: string | undefined): boolean => ch !== undefined && /[\w.]/.test(ch);

  let from = Math.min(offset, text.length);
  let to = from;

  while (from > 0 && isWord(text[from - 1])) from--;
  while (to < text.length && isWord(text[to])) to++;

  // Крапки по краях — не частина імені (`1.` або `.5`).
  while (from < to && text[from] === '.') from++;
  while (to > from && text[to - 1] === '.') to--;

  if (from >= to || !/^[A-Za-z_]/.test(text.slice(from, to))) return null;

  return { from, to };
}

function cellAt(text: string, offset: number, symbols: EditorSymbols): HoverInfo | null {
  const structure = symbols.structure;
  if (structure === undefined) return null;

  const segment = segmentAround(text, offset);
  if (segment === null || segment.text === '') return null;

  const slot = slotAfter(structure, segment.segments, symbols.tableDefId);
  const same = (a: string): boolean => a.toUpperCase() === segment.text.toUpperCase();
  const range = { from: segment.from, to: segment.to };

  switch (slot.kind) {
    case 'first': {
      const table = slot.table;
      const column = table?.columns.find((c) => same(c.code));
      if (table !== undefined && column !== undefined) {
        return { ...range, symbol: { kind: 'column', column, table } };
      }

      const row = table?.rows.find((r) => same(r.key));
      if (table !== undefined && row !== undefined) {
        return { ...range, symbol: { kind: 'row', row, table } };
      }

      const other = structure.tables.find((t) => same(t.code));
      if (other !== undefined) return { ...range, symbol: { kind: 'table', table: other } };

      const sheet = structure.sheets.find((s) => same(s.code));
      return sheet === undefined ? null : { ...range, symbol: { kind: 'sheet', sheet } };
    }

    case 'table': {
      const table = slot.sheet.tables.find((t) => same(t.code));
      return table === undefined ? null : { ...range, symbol: { kind: 'table', table } };
    }

    case 'row': {
      const row = slot.table.rows.find((r) => same(r.key));
      return row === undefined ? null : { ...range, symbol: { kind: 'row', row, table: slot.table } };
    }

    case 'column': {
      const column = slot.table.columns.find((c) => same(c.code));
      return column === undefined
        ? null
        : { ...range, symbol: { kind: 'column', column, table: slot.table } };
    }

    default:
      return null;
  }
}
