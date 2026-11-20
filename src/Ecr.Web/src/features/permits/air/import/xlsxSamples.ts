import { useEffect, useState } from 'react';

/*
 * Sample values of the file's columns for the «Mapping» step (UX 20.11): the user recognises a column by its data, not only by
 * its header text. Read in the browser from the workbook the user chose — an .xlsx is a zip of XML parts — so nothing is sent
 * anywhere and the server contract is unchanged. Best effort: a part that does not read (another zip layout, a file changed on
 * disk, a browser without `DecompressionStream`, a file over the limits) gives no samples, and the step works without them.
 *
 * ⚠ Review 20.11 (M3): the work is bounded — the file size, the declared size of each part, the text inflated (streamed, stopped
 * once past the rows needed), the shared strings inflated (only up to the highest index the sample cells use), a time budget and
 * an abort from the caller; the XML is scanned with `indexOf` (linear — an unclosed tag stops the scan, it is never re-searched).
 * A workbook is read once per `File` (cache), a sample set once per sheet / first row / columns.
 */

/** Column number (1-based) → its first distinct non-empty values below the header. */
export type ColumnSamples = ReadonlyMap<number, readonly string[]>;

/** Bounds of the reading (bytes of the file and of the declared parts, characters of the inflated text). */
export const SampleLimits = {
  fileBytes: 8 * 1024 * 1024,
  sheetBytes: 16 * 1024 * 1024,
  sharedBytes: 8 * 1024 * 1024,
  smallPartBytes: 1024 * 1024,
  sheetChars: 4 * 1024 * 1024,
  sharedChars: 8 * 1024 * 1024,
  sharedItems: 100_000,
  budgetMs: 2000,
  scanRows: 300,
  count: 3,
} as const;

/** Why no samples are read: over a limit or out of time (not an error of the file). */
export class SamplesSkipped extends Error {}

export interface SampleOptions {
  readonly signal?: AbortSignal;
  /** Time budget of one reading; default `SampleLimits.budgetMs`. */
  readonly budgetMs?: number;
}

interface ZipEntry {
  readonly method: number;
  readonly size: number;
  readonly uncompressed: number;
  readonly offset: number;
}

const EndOfDirectory = 0x06054b50;
const DirectoryEntry = 0x02014b50;
const LocalHeader = 0x04034b50;
/** Longest a value is shown: a sample is for recognising the column, not for reading it. */
const SampleLength = 40;
/** Compressed bytes fed to the inflater at a time: what one write may expand to stays bounded. */
const InflateSlice = 16 * 1024;

function u16(bytes: Uint8Array, at: number): number {
  return (bytes[at] ?? 0) | ((bytes[at + 1] ?? 0) << 8);
}

function u32(bytes: Uint8Array, at: number): number {
  return (u16(bytes, at) | (u16(bytes, at + 2) << 16)) >>> 0;
}

/** The central directory of a zip: part name → how and where it is stored, and its declared size. */
export function zipEntries(bytes: Uint8Array): ReadonlyMap<string, ZipEntry> {
  let end = -1;
  for (let at = bytes.length - 22; at >= Math.max(0, bytes.length - 65_557); at -= 1) {
    if (u32(bytes, at) === EndOfDirectory) {
      end = at;
      break;
    }
  }
  if (end < 0) throw new Error('xlsx: not a zip');
  const count = u16(bytes, end + 10);
  const names = new TextDecoder();
  const out = new Map<string, ZipEntry>();
  let at = u32(bytes, end + 16);
  for (let index = 0; index < count; index += 1) {
    if (at + 46 > bytes.length || u32(bytes, at) !== DirectoryEntry) throw new Error('xlsx: broken directory');
    const nameLength = u16(bytes, at + 28);
    const name = names.decode(bytes.subarray(at + 46, at + 46 + nameLength));
    out.set(name, { method: u16(bytes, at + 10), size: u32(bytes, at + 20), uncompressed: u32(bytes, at + 24), offset: u32(bytes, at + 42) });
    at += 46 + nameLength + u16(bytes, at + 30) + u16(bytes, at + 32);
  }

  return out;
}

/* ───────────────────────── linear XML scanning ───────────────────────── */

function isNameChar(code: number): boolean {
  return (
    (code >= 48 && code <= 58) || // 0-9 and ':'
    (code >= 65 && code <= 90) ||
    (code >= 97 && code <= 122) ||
    code === 95 ||
    code === 45 ||
    code === 46
  );
}

function tagNameEnd(text: string, start: number): number {
  let end = start;
  while (end < text.length && isNameChar(text.charCodeAt(end))) end += 1;

  return end;
}

/** The tag name `text[start, end)` is `local`, with or without a namespace prefix (compared in place: no string per tag). */
function localNameIs(text: string, start: number, end: number, local: string): boolean {
  const localStart = end - local.length;
  if (localStart < start || !text.startsWith(local, localStart)) return false;

  return localStart === start || text.charCodeAt(localStart - 1) === 58;
}

interface XmlElement {
  readonly start: number;
  readonly attributes: string;
  /** Text between the tags; `null` — a self-closing element. */
  readonly inner: string | null;
  readonly end: number;
}

/**
 * The next element `<local …>` (any namespace prefix) at or after `from`; `null` when there is none or it is not closed — the scan
 * then stops (an unclosed tag is never searched again, so the scan stays linear).
 */
export function nextElement(text: string, local: string, from: number): XmlElement | null {
  let pos = from;
  for (;;) {
    const lt = text.indexOf('<', pos);
    if (lt < 0) return null;
    const nameEnd = tagNameEnd(text, lt + 1);
    if (!localNameIs(text, lt + 1, nameEnd, local)) {
      pos = nameEnd > lt + 1 ? nameEnd : lt + 1;
      continue;
    }
    const qname = text.slice(lt + 1, nameEnd);
    const gt = text.indexOf('>', nameEnd);
    if (gt < 0) return null;
    if (text.charCodeAt(gt - 1) === 47) return { start: lt, attributes: text.slice(nameEnd, gt - 1), inner: null, end: gt + 1 };
    const close = `</${qname}>`;
    const closeAt = text.indexOf(close, gt + 1);
    if (closeAt < 0) return null;

    return { start: lt, attributes: text.slice(nameEnd, gt), inner: text.slice(gt + 1, closeAt), end: closeAt + close.length };
  }
}

function* elements(text: string, local: string): Generator<XmlElement> {
  let pos = 0;
  for (;;) {
    const element = nextElement(text, local, pos);
    if (element === null) return;
    yield element;
    pos = element.end;
  }
}

/** XML text → plain text (the five named entities and numeric references; a reference out of range stays as written). */
export function xmlText(text: string): string {
  if (!text.includes('&')) return text;

  return text.replace(/&(#x[0-9a-f]{1,8}|#\d{1,10}|amp|lt|gt|quot|apos);/gi, (match, entity: string) => {
    const lower = entity.toLowerCase();
    if (lower.startsWith('#')) {
      const code = lower.startsWith('#x') ? Number.parseInt(lower.slice(2), 16) : Number.parseInt(lower.slice(1), 10);

      return Number.isInteger(code) && code >= 0 && code <= 0x10ffff ? String.fromCodePoint(code) : match;
    }

    return { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" }[lower] ?? match;
  });
}

function attribute(attributes: string, name: string): string | null {
  const found = new RegExp(`(?:^|\\s)${name}="([^"]*)"`).exec(attributes);

  return found === null ? null : xmlText(found[1] ?? '');
}

/** The visible text of a string item: its `<t>` runs, without the phonetic guide (`<rPh>`). */
function runsText(xml: string): string {
  let visible = '';
  let pos = 0;
  for (const guide of elements(xml, 'rPh')) {
    visible += xml.slice(pos, guide.start);
    pos = guide.end;
  }
  visible += xml.slice(pos);
  let out = '';
  for (const run of elements(visible, 't')) out += xmlText(run.inner ?? '');

  return out;
}

/** The string items of `sharedStrings.xml`, at most `limit` of them (the rest is not looked at). */
export function sharedStrings(xml: string, limit = Number.POSITIVE_INFINITY): string[] {
  const out: string[] = [];
  for (const item of elements(xml, 'si')) {
    if (out.length >= limit) break;
    out.push(runsText(item.inner ?? ''));
  }

  return out;
}

/** Sheet name → its part in the zip (`xl/worksheets/sheet1.xml`), from the workbook and its relationships. */
export function sheetParts(workbook: string, relationships: string): ReadonlyMap<string, string> {
  const targets = new Map<string, string>();
  for (const relation of elements(relationships, 'Relationship')) {
    const id = attribute(relation.attributes, 'Id');
    const target = attribute(relation.attributes, 'Target');
    if (id === null || target === null) continue;
    targets.set(id, target.startsWith('/') ? target.slice(1) : `xl/${target}`);
  }
  const out = new Map<string, string>();
  for (const sheet of elements(workbook, 'sheet')) {
    const name = attribute(sheet.attributes, 'name');
    const id = attribute(sheet.attributes, '(?:\\w+:)?id');
    const part = id === null ? undefined : targets.get(id);
    if (name !== null && part !== undefined) out.set(name, part);
  }

  return out;
}

/** `AB` → 28. */
export function columnNumber(letters: string): number {
  let out = 0;
  for (const letter of letters.toUpperCase()) out = out * 26 + (letter.charCodeAt(0) - 64);

  return out;
}

/** A cell value before the shared strings are known: an index into them, or the text itself. */
export type RawValue = { readonly shared: number } | { readonly text: string };

function rawValue(attributes: string, body: string): RawValue | null {
  const type = attribute(attributes, 't');
  if (type === 'inlineStr') return { text: runsText(body) };
  const value = nextElement(body, 'v', 0)?.inner;
  if (value === undefined || value === null) return null;
  const text = xmlText(value);
  if (type === 's') {
    const index = Number.parseInt(text, 10);

    return Number.isInteger(index) && index >= 0 ? { shared: index } : null;
  }
  if (type === 'b') return { text: text === '1' ? 'TRUE' : 'FALSE' };

  return { text };
}

function shown(value: string): string {
  const text = value.replace(/\s+/g, ' ').trim();

  return text.length > SampleLength ? `${text.slice(0, SampleLength)}…` : text;
}

/**
 * The first `count` distinct cells of each asked column in the rows `fromRow … fromRow + scanRows - 1` of a sheet part, before
 * shared strings are resolved (so only the shared strings up to the highest index used need to be read). Rows and cells without
 * a reference take the next position, as Excel does. Scanning stops once every column has its cells.
 */
export function sheetCells(
  sheet: string,
  fromRow: number,
  columns: readonly number[],
  count: number = SampleLimits.count,
  scanRows: number = SampleLimits.scanRows,
): ReadonlyMap<number, readonly RawValue[]> {
  const wanted = new Set(columns);
  const out = new Map<number, RawValue[]>();
  const seen = new Map<number, Set<string>>();
  let open = wanted.size;
  let rowNumber = 0;
  for (const row of elements(sheet, 'row')) {
    const reference = attribute(row.attributes, 'r');
    rowNumber = reference === null ? rowNumber + 1 : Number.parseInt(reference, 10);
    if (rowNumber < fromRow) continue;
    if (rowNumber >= fromRow + scanRows || open === 0) break;
    let cellColumn = 0;
    for (const cell of elements(row.inner ?? '', 'c')) {
      const at = /^([A-Za-z]{1,3})\d+$/.exec(attribute(cell.attributes, 'r') ?? '');
      cellColumn = at === null ? cellColumn + 1 : columnNumber(at[1] ?? '');
      if (!wanted.has(cellColumn)) continue;
      const value = rawValue(cell.attributes, cell.inner ?? '');
      if (value === null || ('text' in value && shown(value.text) === '')) continue;
      const key = 'shared' in value ? `s${String(value.shared)}` : `t${shown(value.text)}`;
      const keys = seen.get(cellColumn) ?? new Set<string>();
      const list = out.get(cellColumn) ?? [];
      if (list.length >= count || keys.has(key)) continue;
      keys.add(key);
      list.push(value);
      seen.set(cellColumn, keys);
      out.set(cellColumn, list);
      if (list.length === count) open -= 1;
    }
  }

  return out;
}

/** The highest shared-string index the cells use (`-1` — none). */
export function highestShared(cells: ReadonlyMap<number, readonly RawValue[]>): number {
  let out = -1;
  for (const list of cells.values()) for (const value of list) if ('shared' in value) out = Math.max(out, value.shared);

  return out;
}

/** Cells → shown values (shared strings resolved; an empty or unknown one left out). */
export function resolveSamples(cells: ReadonlyMap<number, readonly RawValue[]>, shared: readonly string[]): ColumnSamples {
  const out = new Map<number, string[]>();
  for (const [column, list] of cells) {
    const values: string[] = [];
    for (const value of list) {
      const text = shown('shared' in value ? (shared[value.shared] ?? '') : value.text);
      if (text !== '' && !values.includes(text)) values.push(text);
    }
    if (values.length > 0) out.set(column, values);
  }

  return out;
}

/** The samples of a sheet part with its shared strings (both as text). */
export function sheetSamples(
  sheet: string,
  shared: readonly string[],
  fromRow: number,
  columns: readonly number[],
  count: number = SampleLimits.count,
  scanRows: number = SampleLimits.scanRows,
): ColumnSamples {
  return resolveSamples(sheetCells(sheet, fromRow, columns, count, scanRows), shared);
}

/* ───────────────────────── bounded reading of parts ───────────────────────── */

interface Budget {
  readonly signal: AbortSignal | undefined;
  readonly deadline: number;
}

function checkBudget(budget: Budget): void {
  if (budget.signal?.aborted === true) throw new DOMException('Samples aborted', 'AbortError');
  if (Date.now() > budget.deadline) throw new SamplesSkipped('xlsx: out of time');
}

/**
 * Text of a part, inflated as a stream: stops (and cancels the inflater) once `enough` says so or `maxChars` are read; the budget
 * and the abort are checked between chunks.
 */
async function partText(
  bytes: Uint8Array,
  entry: ZipEntry,
  budget: Budget,
  maxChars: number,
  enough: (text: string) => boolean = () => false,
): Promise<string> {
  checkBudget(budget);
  if (u32(bytes, entry.offset) !== LocalHeader) throw new Error('xlsx: broken part');
  const start = entry.offset + 30 + u16(bytes, entry.offset + 26) + u16(bytes, entry.offset + 28);
  const data = bytes.subarray(start, start + entry.size);
  if (entry.method === 0) return new TextDecoder().decode(data.subarray(0, maxChars * 4)).slice(0, maxChars);
  if (entry.method !== 8) throw new Error('xlsx: unsupported compression');
  if (typeof DecompressionStream !== 'function') throw new SamplesSkipped('xlsx: no DecompressionStream');

  const inflater = new DecompressionStream('deflate-raw');
  const writer = inflater.writable.getWriter();
  // The compressed bytes go in by slices, each only after the previous one was taken: what is held at once stays small.
  void (async () => {
    for (let at = 0; at < data.length; at += InflateSlice) {
      await writer.ready;
      await writer.write(data.subarray(at, at + InflateSlice));
    }
    await writer.close();
  })().catch(() => undefined);
  const reader = inflater.readable.getReader();
  const cancel = (): void => void reader.cancel().catch(() => undefined);
  budget.signal?.addEventListener('abort', cancel);
  const decoder = new TextDecoder();
  let text = '';
  try {
    for (;;) {
      checkBudget(budget);
      const { done, value } = await reader.read();
      if (done) {
        text += decoder.decode();
        break;
      }
      text += decoder.decode(value, { stream: true });
      if (text.length >= maxChars || enough(text)) {
        cancel();
        break;
      }
    }
  } finally {
    budget.signal?.removeEventListener('abort', cancel);
  }
  checkBudget(budget);

  return text.length > maxChars ? text.slice(0, maxChars) : text;
}

/** A row tag reader over a text that grows: says when a row at or past `stopRow` has started (`r` of the last complete tags). */
export function rowWatcher(stopRow: number): (text: string) => boolean {
  let pos = 0;

  return (text) => {
    for (;;) {
      const lt = text.indexOf('<', pos);
      if (lt < 0) return false;
      const nameEnd = tagNameEnd(text, lt + 1);
      if (nameEnd >= text.length) return false; // the tag goes on in the next chunk
      if (!localNameIs(text, lt + 1, nameEnd, 'row')) {
        pos = nameEnd > lt + 1 ? nameEnd : lt + 1;
        continue;
      }
      const gt = text.indexOf('>', nameEnd);
      if (gt < 0) return false;
      pos = gt + 1;
      const reference = attribute(text.slice(nameEnd, gt), 'r');
      if (reference !== null && Number.parseInt(reference, 10) >= stopRow) return true;
    }
  };
}

/** A counter of closed string items (`</si>`) over a text that grows: says when `needed` of them are in. */
export function itemWatcher(needed: number): (text: string) => boolean {
  let pos = 0;
  let closed = 0;

  return (text) => {
    for (;;) {
      const at = text.indexOf('si>', pos);
      if (at < 0) return false;
      pos = at + 3;
      const lt = text.lastIndexOf('<', at);
      if (lt >= 0 && text.charCodeAt(lt + 1) === 47) closed += 1;
      if (closed >= needed) return true;
    }
  };
}

interface Book {
  readonly bytes: Uint8Array;
  readonly entries: ReadonlyMap<string, ZipEntry>;
  sheets: ReadonlyMap<string, string> | null;
  readonly results: Map<string, ColumnSamples>;
}

const books = new WeakMap<Blob, Promise<Book>>();

function readBytes(file: Blob): Promise<Uint8Array> {
  if (typeof file.arrayBuffer === 'function') return file.arrayBuffer().then((buffer) => new Uint8Array(buffer));

  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => (reader.result instanceof ArrayBuffer ? resolve(new Uint8Array(reader.result)) : reject(new Error('xlsx: read')));
    reader.onerror = () => reject(reader.error ?? new Error('xlsx: read'));
    reader.readAsArrayBuffer(file);
  });
}

/** The workbook of a file, read once per file (a failed read is not kept). Over `SampleLimits.fileBytes` — skipped unread. */
function openBook(file: Blob): Promise<Book> {
  const cached = books.get(file);
  if (cached !== undefined) return cached;
  if (file.size > SampleLimits.fileBytes) return Promise.reject(new SamplesSkipped('xlsx: file too large'));
  const opening = readBytes(file).then((bytes) => ({ bytes, entries: zipEntries(bytes), sheets: null, results: new Map<string, ColumnSamples>() }));
  books.set(file, opening);
  opening.catch(() => books.delete(file));

  return opening;
}

async function smallPart(book: Book, name: string, budget: Budget): Promise<string> {
  const entry = book.entries.get(name);
  if (entry === undefined) return '';
  if (entry.uncompressed > SampleLimits.smallPartBytes) throw new SamplesSkipped(`xlsx: ${name} too large`);

  return partText(book.bytes, entry, budget, SampleLimits.smallPartBytes);
}

/**
 * Samples of the columns of one sheet of a workbook file (`sheet` — its name; unknown — the first sheet). Rejects with
 * `SamplesSkipped` over the limits or the time budget, with an `AbortError` when `signal` aborts; the caller shows no samples.
 */
export async function workbookSamples(
  file: Blob,
  sheet: string,
  fromRow: number,
  columns: readonly number[],
  options: SampleOptions = {},
): Promise<ColumnSamples> {
  const budget: Budget = { signal: options.signal, deadline: Date.now() + (options.budgetMs ?? SampleLimits.budgetMs) };
  checkBudget(budget);
  const book = await openBook(file);
  const key = `${sheet}\u0001${String(fromRow)}\u0001${columns.join(',')}`;
  const known = book.results.get(key);
  if (known !== undefined) return known;

  if (book.sheets === null) {
    book.sheets = sheetParts(await smallPart(book, 'xl/workbook.xml', budget), await smallPart(book, 'xl/_rels/workbook.xml.rels', budget));
  }
  const part = book.sheets.get(sheet) ?? [...book.sheets.values()][0];
  const entry = part === undefined ? undefined : book.entries.get(part);
  if (entry === undefined) return new Map();
  if (entry.uncompressed > SampleLimits.sheetBytes) throw new SamplesSkipped('xlsx: sheet too large');

  const stopRow = fromRow + SampleLimits.scanRows;
  const cells = sheetCells(await partText(book.bytes, entry, budget, SampleLimits.sheetChars, rowWatcher(stopRow)), fromRow, columns);
  const highest = highestShared(cells);
  const sharedEntry = book.entries.get('xl/sharedStrings.xml');
  let shared: readonly string[] = [];
  if (highest >= 0 && sharedEntry !== undefined && sharedEntry.uncompressed <= SampleLimits.sharedBytes && highest < SampleLimits.sharedItems) {
    const xml = await partText(book.bytes, sharedEntry, budget, SampleLimits.sharedChars, itemWatcher(highest + 1));
    shared = sharedStrings(xml, highest + 1);
  }
  const samples = resolveSamples(cells, shared);
  book.results.set(key, samples);

  return samples;
}

/**
 * Samples for the step: `null` while they are read or when they cannot be (the step then shows no «Examples» column). Read again
 * for another file, sheet, first data row or set of columns; a reading no longer wanted is aborted.
 */
export function useColumnSamples(file: File | null | undefined, sheet: string, fromRow: number, columns: readonly number[]): ColumnSamples | null {
  const wanted = columns.join(',');
  const key = `${sheet}\u0001${String(fromRow)}\u0001${wanted}`;
  const [state, setState] = useState<{ readonly file: File | null | undefined; readonly key: string; readonly samples: ColumnSamples | null }>({
    file,
    key,
    samples: null,
  });

  useEffect(() => {
    if (file === null || file === undefined || wanted === '') return undefined;
    const read = `${sheet}\u0001${String(fromRow)}\u0001${wanted}`;
    const abort = new AbortController();
    workbookSamples(file, sheet, fromRow, wanted.split(',').map(Number), { signal: abort.signal })
      .then((samples) => {
        if (!abort.signal.aborted) setState({ file, key: read, samples: samples.size === 0 ? null : samples });
      })
      .catch(() => {
        if (!abort.signal.aborted) setState({ file, key: read, samples: null });
      });

    return () => abort.abort();
  }, [file, sheet, fromRow, wanted]);

  return state.file === file && state.key === key ? state.samples : null;
}
