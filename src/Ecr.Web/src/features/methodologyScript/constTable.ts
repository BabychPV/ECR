import { ScriptKeywords } from './scriptLanguage';

/**
 * Розбір і друк літерала `const`-мапи (граматика `constValue`/`mapLit`, план MS
 * 3.3) у сітку для `ConstTableDialog` (13.5). Чистий модуль без Monaco.
 */

export type ConstCell = number | string | boolean | null;

/**
 * Сітка: рядки — ключі першого рівня. `columnKeys === null` — однорівнева мапа
 * (одна колонка значень, `cells[i][0]`).
 */
export interface ConstGrid {
  readonly rowKeys: readonly string[];
  readonly columnKeys: readonly string[] | null;
  readonly cells: readonly (readonly ConstCell[])[];
}

export type ConstParseErrorCode =
  | 'notMap'
  | 'syntax'
  | 'mixedLevels'
  | 'tooDeep'
  | 'duplicateKey';

export type ConstParseResult =
  | { readonly ok: true; readonly grid: ConstGrid }
  | {
      readonly ok: false;
      readonly code: ConstParseErrorCode;
      readonly offset: number;
    };

// ─────────────────────────────────────────────────────────────────────────────
// Лексер
// ─────────────────────────────────────────────────────────────────────────────

type TokenKind = 'ident' | 'number' | 'string' | 'punct';

interface Token {
  readonly kind: TokenKind;
  /** Для рядка — розекранований вміст; інакше — вихідний текст. */
  readonly text: string;
  readonly start: number;
  readonly end: number;
}

const IdentifierStart = /[A-Za-z_]/;
const IdentifierPart = /[A-Za-z0-9_]/;
const NumberRx = /\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/y;

function tokenize(text: string): Token[] | { readonly error: number } {
  const tokens: Token[] = [];
  let i = 0;
  while (i < text.length) {
    const c = text[i] ?? '';
    if (/\s/.test(c)) {
      i++;
    } else if (c === '/' && text[i + 1] === '/') {
      const nl = text.indexOf('\n', i);
      i = nl === -1 ? text.length : nl + 1;
    } else if (c === '/' && text[i + 1] === '*') {
      const close = text.indexOf('*/', i + 2);
      if (close === -1) return { error: i };
      i = close + 2;
    } else if (c === '"' || c === "'") {
      let j = i + 1;
      let value = '';
      let closed = false;
      while (j < text.length) {
        const d = text[j] ?? '';
        if (d === '\\') {
          const e = text[j + 1] ?? '';
          value += e === 'n' ? '\n' : e;
          j += 2;
        } else if (d === c) {
          closed = true;
          j++;
          break;
        } else {
          value += d;
          j++;
        }
      }
      if (!closed) return { error: i };
      tokens.push({ kind: 'string', text: value, start: i, end: j });
      i = j;
    } else if (IdentifierStart.test(c)) {
      let j = i + 1;
      while (j < text.length) {
        const d = text[j] ?? '';
        if (IdentifierPart.test(d)) {
          j++;
        } else if (d === '{') {
          const close = text.indexOf('}', j);
          if (close === -1 || !/^[A-Za-z_][A-Za-z0-9_]*$/.test(text.slice(j + 1, close))) break;
          j = close + 1;
        } else break;
      }
      tokens.push({ kind: 'ident', text: text.slice(i, j), start: i, end: j });
      i = j;
    } else if (/[0-9]/.test(c)) {
      NumberRx.lastIndex = i;
      const m = NumberRx.exec(text);
      const raw = m?.[0] ?? c;
      tokens.push({ kind: 'number', text: raw, start: i, end: i + raw.length });
      i += raw.length;
    } else {
      tokens.push({ kind: 'punct', text: c, start: i, end: i + 1 });
      i++;
    }
  }
  return tokens;
}

// ─────────────────────────────────────────────────────────────────────────────
// Розбір
// ─────────────────────────────────────────────────────────────────────────────

interface MapNode {
  readonly entries: { readonly key: string; readonly value: ValueNode; readonly at: number }[];
}
type ValueNode = ConstCell | MapNode;

class ParseFailure extends Error {
  constructor(
    readonly code: ConstParseErrorCode,
    readonly offset: number,
  ) {
    super(code);
  }
}

function isMap(value: ValueNode): value is MapNode {
  return typeof value === 'object' && value !== null;
}

class Cursor {
  position = 0;
  constructor(
    readonly tokens: readonly Token[],
    readonly length: number,
  ) {}

  peek(): Token | undefined {
    return this.tokens[this.position];
  }

  here(): number {
    return this.peek()?.start ?? this.length;
  }

  isPunct(char: string): boolean {
    const token = this.peek();
    return token?.kind === 'punct' && token.text === char;
  }

  expectPunct(char: string): void {
    if (!this.isPunct(char)) throw new ParseFailure('syntax', this.here());
    this.position++;
  }
}

function parseValue(cursor: Cursor): ValueNode {
  const token = cursor.peek();
  if (token === undefined) throw new ParseFailure('syntax', cursor.here());

  if (token.kind === 'punct' && token.text === '{') return parseMap(cursor);

  if (token.kind === 'punct' && token.text === '-') {
    cursor.position++;
    const next = cursor.peek();
    if (next?.kind !== 'number') throw new ParseFailure('syntax', cursor.here());
    cursor.position++;
    return -Number(next.text);
  }
  if (token.kind === 'number') {
    cursor.position++;
    return Number(token.text);
  }
  if (token.kind === 'string') {
    cursor.position++;
    return token.text;
  }
  if (token.kind === 'ident' && (token.text === 'true' || token.text === 'false')) {
    cursor.position++;
    return token.text === 'true';
  }
  throw new ParseFailure('syntax', token.start);
}

function parseMap(cursor: Cursor): MapNode {
  cursor.expectPunct('{');
  const entries: { key: string; value: ValueNode; at: number }[] = [];
  while (!cursor.isPunct('}')) {
    const keyToken = cursor.peek();
    if (keyToken === undefined || keyToken.kind === 'punct') {
      throw new ParseFailure('syntax', cursor.here());
    }
    cursor.position++;
    cursor.expectPunct(':');
    const value = parseValue(cursor);
    if (entries.some((e) => e.key === keyToken.text)) {
      throw new ParseFailure('duplicateKey', keyToken.start);
    }
    entries.push({ key: keyToken.text, value, at: keyToken.start });
    if (cursor.isPunct(',')) cursor.position++;
    else if (!cursor.isPunct('}')) throw new ParseFailure('syntax', cursor.here());
  }
  cursor.expectPunct('}');
  return { entries };
}

/** Розбирає літерал мапи (`{ … }`) у сітку. */
export function parseConstLiteral(text: string): ConstParseResult {
  const tokens = tokenize(text);
  if (!Array.isArray(tokens)) {
    return { ok: false, code: 'syntax', offset: (tokens as { error: number }).error };
  }
  const cursor = new Cursor(tokens, text.length);
  try {
    if (!cursor.isPunct('{')) return { ok: false, code: 'notMap', offset: cursor.here() };
    const root = parseMap(cursor);
    if (cursor.peek() !== undefined) throw new ParseFailure('syntax', cursor.here());
    return { ok: true, grid: toGrid(root) };
  } catch (error) {
    if (error instanceof ParseFailure) return { ok: false, code: error.code, offset: error.offset };
    throw error;
  }
}

function toGrid(root: MapNode): ConstGrid {
  const rowKeys = root.entries.map((e) => e.key);
  const nested = root.entries.filter((e) => isMap(e.value));

  if (nested.length === 0) {
    return {
      rowKeys,
      columnKeys: null,
      cells: root.entries.map((e) => [e.value as ConstCell]),
    };
  }
  if (nested.length !== root.entries.length) {
    const firstScalar = root.entries.find((e) => !isMap(e.value));
    throw new ParseFailure('mixedLevels', firstScalar?.at ?? 0);
  }

  const columnKeys: string[] = [];
  for (const entry of root.entries) {
    for (const inner of (entry.value as MapNode).entries) {
      if (isMap(inner.value)) throw new ParseFailure('tooDeep', inner.at);
      if (!columnKeys.includes(inner.key)) columnKeys.push(inner.key);
    }
  }

  const cells = root.entries.map((entry) => {
    const inner = new Map((entry.value as MapNode).entries.map((e) => [e.key, e.value as ConstCell]));
    return columnKeys.map((key) => inner.get(key) ?? null);
  });
  return { rowKeys, columnKeys, cells };
}

// ─────────────────────────────────────────────────────────────────────────────
// Друк
// ─────────────────────────────────────────────────────────────────────────────

const Reserved = new Set(ScriptKeywords);

/** Ключ без лапок — лише коли це ідентифікатор і не ключове слово. */
export function printKey(key: string): string {
  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(key) && !Reserved.has(key) ? key : printString(key);
}

function printString(value: string): string {
  const escaped = value
    .replace(/\\/g, '\\\\')
    .replace(/"/g, '\\"')
    .replace(/\r?\n/g, '\\n');
  return `"${escaped}"`;
}

/** Друк значення комірки; `null` не друкується (запису немає). */
export function printCell(cell: ConstCell): string {
  if (cell === null) return '';
  if (typeof cell === 'number') return String(cell);
  if (typeof cell === 'boolean') return cell ? 'true' : 'false';
  return printString(cell);
}

/**
 * Канонічний вигляд: рядок на ключ першого рівня, вкладена мапа в тому ж
 * рядку з вирівняними колонками, кінцеві коми. Порожні (`null`) комірки в
 * літерал не потрапляють.
 */
export function printConstGrid(grid: ConstGrid, indent = '    '): string {
  if (grid.rowKeys.length === 0) return '{}';

  const keyTexts = grid.rowKeys.map((k) => `${printKey(k)}:`);
  const lines: string[] = [];

  if (grid.columnKeys === null) {
    // Однорівнева мапа: рядок без значення (`null`) у літерал не потрапляє.
    const present = grid.rowKeys
      .map((_, r) => ({ key: keyTexts[r] ?? '', value: printCell(grid.cells[r]?.[0] ?? null) }))
      .filter((e) => e.value !== '');
    if (present.length === 0) return '{}';
    const width = Math.max(...present.map((e) => e.key.length));
    for (const e of present) lines.push(`${indent}${e.key.padEnd(width)} ${e.value},`);
    return `{\n${lines.join('\n')}\n}`;
  }

  const keyWidth = Math.max(...keyTexts.map((k) => k.length));
  const columnKeys = grid.columnKeys;
  const texts = grid.rowKeys.map((_, r) =>
    columnKeys.map((key, c) => {
      const value = printCell(grid.cells[r]?.[c] ?? null);
      return value === '' ? null : `${printKey(key)}: ${value}`;
    }),
  );
  const widths = columnKeys.map((_, c) =>
    Math.max(0, ...texts.map((row) => row[c]?.length ?? 0)),
  );

  texts.forEach((row, r) => {
    let lastPresent = -1;
    row.forEach((text, c) => {
      if (text !== null) lastPresent = c;
    });
    const parts: string[] = [];
    row.forEach((text, c) => {
      const width = (widths[c] ?? 0) + 1;
      if (text === null) {
        parts.push(''.padEnd(width));
      } else {
        parts.push((c < lastPresent ? `${text},` : text).padEnd(width));
      }
    });
    const body = parts.join(' ').trimEnd();
    const inner = body === '' ? '{}' : `{ ${body} }`;
    lines.push(`${indent}${(keyTexts[r] ?? '').padEnd(keyWidth)} ${inner},`);
  });

  return `{\n${lines.join('\n')}\n}`;
}

// ─────────────────────────────────────────────────────────────────────────────
// Вставка з Excel (TSV)
// ─────────────────────────────────────────────────────────────────────────────

/** Текст комірки → значення: число (кома → крапка), `true`/`false`, рядок; порожнє — `null`. */
export function parseCellText(raw: string): ConstCell {
  const text = raw.trim();
  if (text === '') return null;
  const lower = text.toLowerCase();
  if (lower === 'true') return true;
  if (lower === 'false') return false;

  const grouped = /^[+-]?\d{1,3}(?:[  ]\d{3})+(?:[.,]\d+)?$/.test(text)
    ? text.replace(/[  ]/g, '')
    : text;
  if (/^[+-]?(?:\d+(?:[.,]\d+)?|[.,]\d+)(?:[eE][+-]?\d+)?$/.test(grouped)) {
    const value = Number(grouped.replace(',', '.'));
    if (Number.isFinite(value)) return value;
  }
  return text;
}

/** Значення комірки → текст для поля сітки. */
export function cellToText(cell: ConstCell): string {
  return cell === null ? '' : String(cell);
}

/** Розбирає буфер обміну Excel у прямокутник комірок. */
export function parseTsv(text: string): ConstCell[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let field = '';
  let i = 0;
  let atFieldStart = true;

  const endRow = (): void => {
    row.push(field);
    rows.push(row);
    row = [];
    field = '';
    atFieldStart = true;
  };

  while (i < text.length) {
    const c = text[i] ?? '';
    if (atFieldStart && c === '"') {
      i++;
      while (i < text.length) {
        if (text[i] === '"') {
          if (text[i + 1] === '"') {
            field += '"';
            i += 2;
            continue;
          }
          i++;
          break;
        }
        field += text[i];
        i++;
      }
      atFieldStart = false;
    } else if (c === '\t') {
      row.push(field);
      field = '';
      atFieldStart = true;
      i++;
    } else if (c === '\r' || c === '\n') {
      endRow();
      i += c === '\r' && text[i + 1] === '\n' ? 2 : 1;
    } else {
      field += c;
      atFieldStart = false;
      i++;
    }
  }
  if (field !== '' || row.length > 0) endRow();

  // Excel завершує буфер порожнім рядком.
  while (rows.length > 0 && (rows[rows.length - 1] ?? []).every((f) => f.trim() === '')) rows.pop();

  const width = Math.max(0, ...rows.map((r) => r.length));
  return rows.map((r) => Array.from({ length: width }, (_, c) => parseCellText(r[c] ?? '')));
}

/** Сітка з довільним типом комірки (у діалозі — текст поля вводу). */
export interface GridOf<T> {
  readonly rowKeys: readonly string[];
  readonly columnKeys: readonly string[] | null;
  readonly cells: readonly (readonly T[])[];
}

/**
 * Вставляє прямокутник у сітку з клітинки `(row, col)`; нові рядки й колонки
 * дістають імена `Key{n}` / `Col{n}`, порожні клітинки — `empty`. Для
 * однорівневої сітки колонка лише одна.
 */
export function pasteBlock<T>(
  grid: GridOf<T>,
  block: readonly (readonly T[])[],
  row: number,
  col: number,
  empty: T,
): GridOf<T> {
  const rowKeys = [...grid.rowKeys];
  const columnKeys = grid.columnKeys === null ? null : [...grid.columnKeys];
  const cells = grid.cells.map((r) => [...r]);
  const maxCols = columnKeys === null ? 1 : Number.POSITIVE_INFINITY;

  block.forEach((blockRow, dr) => {
    const r = row + dr;
    while (rowKeys.length <= r) {
      rowKeys.push(freshKey(rowKeys, 'Key'));
      cells.push(Array.from({ length: columnKeys?.length ?? 1 }, () => empty));
    }
    blockRow.forEach((value, dc) => {
      const c = col + dc;
      if (c >= maxCols) return;
      if (columnKeys !== null) {
        while (columnKeys.length <= c) {
          columnKeys.push(freshKey(columnKeys, 'Col'));
          for (const existing of cells) existing.push(empty);
        }
      }
      const target = cells[r];
      if (target !== undefined) target[c] = value;
    });
  });

  return { rowKeys, columnKeys, cells };
}

/** `pasteBlock` для готової сітки значень. */
export function pasteIntoGrid(
  grid: ConstGrid,
  block: readonly (readonly ConstCell[])[],
  row: number,
  col: number,
): ConstGrid {
  return pasteBlock<ConstCell>(grid, block, row, col, null);
}

/**
 * Усю таблицю з Excel: перший стовпець — ключі рядків; перший рядок — ключі
 * колонок, якщо `columnHeaders`; без заголовків колонок і з двома стовпцями —
 * однорівнева мапа.
 */
export function gridFromTsv(text: string, columnHeaders: boolean): ConstGrid | null {
  const block = parseTsv(text);
  if (block.length === 0) return null;
  const width = block[0]?.length ?? 0;
  if (width < 2) return null;

  const keyOf = (cell: ConstCell | undefined): string => cellToText(cell ?? null);

  if (columnHeaders) {
    const header = block[0] ?? [];
    const body = block.slice(1);
    return {
      rowKeys: body.map((r) => keyOf(r[0])),
      columnKeys: header.slice(1).map((c) => keyOf(c)),
      cells: body.map((r) => r.slice(1)),
    };
  }

  if (width === 2) {
    return { rowKeys: block.map((r) => keyOf(r[0])), columnKeys: null, cells: block.map((r) => [r[1] ?? null]) };
  }
  return {
    rowKeys: block.map((r) => keyOf(r[0])),
    columnKeys: Array.from({ length: width - 1 }, (_, i) => `Col${String(i + 1)}`),
    cells: block.map((r) => r.slice(1)),
  };
}

function freshKey(existing: readonly string[], prefix: string): string {
  let n = existing.length + 1;
  while (existing.includes(`${prefix}${String(n)}`)) n++;
  return `${prefix}${String(n)}`;
}

// ─────────────────────────────────────────────────────────────────────────────
// Перевірка сітки
// ─────────────────────────────────────────────────────────────────────────────

export interface ConstGridIssue {
  readonly kind: 'emptyKey' | 'duplicateKey';
  readonly axis: 'row' | 'column';
  readonly index: number;
}

/** Порожні й повторні ключі — літерал із ними не побудувати. */
export function gridIssues(grid: ConstGrid): ConstGridIssue[] {
  const issues: ConstGridIssue[] = [];
  const check = (keys: readonly string[], axis: 'row' | 'column'): void => {
    const seen = new Set<string>();
    keys.forEach((key, index) => {
      if (key.trim() === '') issues.push({ kind: 'emptyKey', axis, index });
      else if (seen.has(key)) issues.push({ kind: 'duplicateKey', axis, index });
      seen.add(key);
    });
  };
  check(grid.rowKeys, 'row');
  if (grid.columnKeys !== null) check(grid.columnKeys, 'column');
  return issues;
}

// ─────────────────────────────────────────────────────────────────────────────
// Пошук літерала під курсором
// ─────────────────────────────────────────────────────────────────────────────

export interface ConstRange {
  /** Ім'я константи. */
  readonly name: string;
  /** Початок `const`. */
  readonly declarationStart: number;
  /** Початок літерала (`{`). */
  readonly start: number;
  /** Кінець літерала (після `}`), виключно. */
  readonly end: number;
}

/**
 * Літерал `const`-мапи під курсором: курсор у межах оголошення від `const` до
 * `;` (включно). Скалярні константи й курсор поза ними дають `null`.
 */
export function findConstAt(text: string, offset: number): ConstRange | null {
  const tokens = tokenize(text);
  if (!Array.isArray(tokens)) return null;

  for (let i = 0; i < tokens.length; i++) {
    const head = tokens[i];
    if (head?.kind !== 'ident' || head.text !== 'const') continue;
    const name = tokens[i + 1];
    if (name?.kind !== 'ident') continue;

    let j = i + 2;
    if (tokens[j]?.text === ':' && tokens[j]?.kind === 'punct' && tokens[j + 1]?.kind === 'ident') j += 2;
    const eq = tokens[j];
    const open = tokens[j + 1];
    if (eq?.kind !== 'punct' || eq.text !== '=' || open?.kind !== 'punct' || open.text !== '{') continue;

    let depth = 0;
    let k = j + 1;
    let close: Token | undefined;
    for (; k < tokens.length; k++) {
      const token = tokens[k];
      if (token?.kind !== 'punct') continue;
      if (token.text === '{') depth++;
      else if (token.text === '}') {
        depth--;
        if (depth === 0) {
          close = token;
          break;
        }
      }
    }
    if (close === undefined) return null;

    const semicolon = tokens[k + 1];
    const declarationEnd =
      semicolon?.kind === 'punct' && semicolon.text === ';' ? semicolon.end : close.end;

    if (offset >= head.start && offset <= declarationEnd) {
      return { name: name.text, declarationStart: head.start, start: open.start, end: close.end };
    }
    i = k;
  }
  return null;
}
