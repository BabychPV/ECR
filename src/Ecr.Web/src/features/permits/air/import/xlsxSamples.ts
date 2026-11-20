import { useEffect, useState } from 'react';

/*
 * Sample values of the file's columns for the «Mapping» step (UX 20.11): the user recognises a column by its data, not only by
 * its header text. Read in the browser from the workbook the user chose — an .xlsx is a zip of XML parts — so nothing is sent
 * anywhere and the server contract is unchanged. Best effort: a part that does not read (another zip layout, a file changed on
 * disk, a browser without `DecompressionStream`) gives no samples, and the step works without them.
 *
 * Only what is needed is read: the workbook part list, the shared strings and the rows right below the header of one sheet
 * (scanning stops once every asked column has its samples).
 */

/** Column number (1-based) → its first distinct non-empty values below the header. */
export type ColumnSamples = ReadonlyMap<number, readonly string[]>;

interface ZipEntry {
  readonly method: number;
  readonly size: number;
  readonly offset: number;
}

const EndOfDirectory = 0x06054b50;
const DirectoryEntry = 0x02014b50;
const LocalHeader = 0x04034b50;
/** Longest a value is shown: a sample is for recognising the column, not for reading it. */
const SampleLength = 40;

function u16(bytes: Uint8Array, at: number): number {
  return (bytes[at] ?? 0) | ((bytes[at + 1] ?? 0) << 8);
}

function u32(bytes: Uint8Array, at: number): number {
  return (u16(bytes, at) | (u16(bytes, at + 2) << 16)) >>> 0;
}

/** The central directory of a zip: part name → how and where it is stored. */
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
    if (u32(bytes, at) !== DirectoryEntry) throw new Error('xlsx: broken directory');
    const nameLength = u16(bytes, at + 28);
    const name = names.decode(bytes.subarray(at + 46, at + 46 + nameLength));
    out.set(name, { method: u16(bytes, at + 10), size: u32(bytes, at + 20), offset: u32(bytes, at + 42) });
    at += 46 + nameLength + u16(bytes, at + 30) + u16(bytes, at + 32);
  }

  return out;
}

async function inflate(data: Uint8Array): Promise<Uint8Array> {
  if (typeof DecompressionStream !== 'function') throw new Error('xlsx: no DecompressionStream');
  const inflater = new DecompressionStream('deflate-raw');
  const writer = inflater.writable.getWriter();
  const written = writer.write(data).then(() => writer.close());
  // A broken part fails the reading side too (that error is the one thrown); the writing side must not stay unhandled.
  written.catch(() => undefined);
  const reader = inflater.readable.getReader();
  const chunks: Uint8Array[] = [];
  let length = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    length += value.length;
  }
  await written;
  const out = new Uint8Array(length);
  let at = 0;
  for (const chunk of chunks) {
    out.set(chunk, at);
    at += chunk.length;
  }

  return out;
}

async function partText(bytes: Uint8Array, entry: ZipEntry): Promise<string> {
  if (u32(bytes, entry.offset) !== LocalHeader) throw new Error('xlsx: broken part');
  const start = entry.offset + 30 + u16(bytes, entry.offset + 26) + u16(bytes, entry.offset + 28);
  const data = bytes.subarray(start, start + entry.size);
  if (entry.method === 0) return new TextDecoder().decode(data);
  if (entry.method !== 8) throw new Error('xlsx: unsupported compression');

  return new TextDecoder().decode(await inflate(data));
}

/** XML text → plain text (the five named entities and numeric references). */
export function xmlText(text: string): string {
  return text.replace(/&(#x[0-9a-f]+|#\d+|amp|lt|gt|quot|apos);/gi, (match, entity: string) => {
    const lower = entity.toLowerCase();
    if (lower.startsWith('#x')) return String.fromCodePoint(Number.parseInt(lower.slice(2), 16));
    if (lower.startsWith('#')) return String.fromCodePoint(Number.parseInt(lower.slice(1), 10));

    return { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" }[lower] ?? match;
  });
}

function attribute(attributes: string, name: string): string | null {
  const found = new RegExp(`(?:^|\\s)${name}="([^"]*)"`).exec(attributes);

  return found === null ? null : xmlText(found[1] ?? '');
}

/** The visible text of a string item: its `<t>` runs, without the phonetic guide (`<rPh>`). */
function runsText(xml: string): string {
  const visible = xml.replace(/<(?:\w+:)?rPh\b[\s\S]*?<\/(?:\w+:)?rPh>/g, '');
  const parts: string[] = [];
  for (const run of visible.matchAll(/<(?:\w+:)?t(?:\s[^>]*)?>([\s\S]*?)<\/(?:\w+:)?t>/g)) parts.push(xmlText(run[1] ?? ''));

  return parts.join('');
}

export function sharedStrings(xml: string): string[] {
  return [...xml.matchAll(/<(?:\w+:)?si\b[^>]*>([\s\S]*?)<\/(?:\w+:)?si>/g)].map((item) => runsText(item[1] ?? ''));
}

/** Sheet name → its part in the zip (`xl/worksheets/sheet1.xml`), from the workbook and its relationships. */
export function sheetParts(workbook: string, relationships: string): ReadonlyMap<string, string> {
  const targets = new Map<string, string>();
  for (const relation of relationships.matchAll(/<(?:\w+:)?Relationship\b([^>]*?)\/?>/g)) {
    const id = attribute(relation[1] ?? '', 'Id');
    const target = attribute(relation[1] ?? '', 'Target');
    if (id === null || target === null) continue;
    targets.set(id, target.startsWith('/') ? target.slice(1) : `xl/${target}`);
  }
  const out = new Map<string, string>();
  for (const sheet of workbook.matchAll(/<(?:\w+:)?sheet\b([^>]*?)\/?>/g)) {
    const name = attribute(sheet[1] ?? '', 'name');
    const id = attribute(sheet[1] ?? '', '(?:\\w+:)?id');
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

function cellValue(attributes: string, body: string, shared: readonly string[]): string {
  const type = attribute(attributes, 't');
  if (type === 'inlineStr') return runsText(body);
  const raw = /<(?:\w+:)?v>([\s\S]*?)<\/(?:\w+:)?v>/.exec(body)?.[1];
  if (raw === undefined) return '';
  const value = xmlText(raw);
  if (type === 's') return shared[Number.parseInt(value, 10)] ?? '';
  if (type === 'b') return value === '1' ? 'TRUE' : 'FALSE';

  return value;
}

function shown(value: string): string {
  const text = value.replace(/\s+/g, ' ').trim();

  return text.length > SampleLength ? `${text.slice(0, SampleLength)}…` : text;
}

/**
 * The first `count` distinct non-empty values of each asked column in the rows `fromRow … fromRow + scanRows - 1` of a sheet
 * part. Rows and cells without a reference take the next position, as Excel does.
 */
export function sheetSamples(
  sheet: string,
  shared: readonly string[],
  fromRow: number,
  columns: readonly number[],
  count = 3,
  scanRows = 300,
): ColumnSamples {
  const wanted = new Set(columns);
  const out = new Map<number, string[]>();
  let open = wanted.size;
  let rowNumber = 0;
  for (const row of sheet.matchAll(/<(?:\w+:)?row\b([^>]*?)(?:\/>|>([\s\S]*?)<\/(?:\w+:)?row>)/g)) {
    const reference = attribute(row[1] ?? '', 'r');
    rowNumber = reference === null ? rowNumber + 1 : Number.parseInt(reference, 10);
    if (rowNumber < fromRow) continue;
    if (rowNumber >= fromRow + scanRows || open === 0) break;
    let cellColumn = 0;
    for (const cell of (row[2] ?? '').matchAll(/<(?:\w+:)?c\b([^>]*?)(?:\/>|>([\s\S]*?)<\/(?:\w+:)?c>)/g)) {
      const attributes = cell[1] ?? '';
      const at = /^([A-Za-z]+)\d+$/.exec(attribute(attributes, 'r') ?? '');
      cellColumn = at === null ? cellColumn + 1 : columnNumber(at[1] ?? '');
      if (!wanted.has(cellColumn)) continue;
      const value = shown(cellValue(attributes, cell[2] ?? '', shared));
      const list = out.get(cellColumn) ?? [];
      if (value === '' || list.length >= count || list.includes(value)) continue;
      list.push(value);
      out.set(cellColumn, list);
      if (list.length === count) open -= 1;
    }
  }

  return out;
}

interface Workbook {
  readonly bytes: Uint8Array;
  readonly entries: ReadonlyMap<string, ZipEntry>;
  readonly sheets: ReadonlyMap<string, string>;
  readonly shared: readonly string[];
}

async function optionalPart(bytes: Uint8Array, entries: ReadonlyMap<string, ZipEntry>, name: string): Promise<string> {
  const entry = entries.get(name);

  return entry === undefined ? '' : partText(bytes, entry);
}

function readBytes(file: Blob): Promise<Uint8Array> {
  if (typeof file.arrayBuffer === 'function') return file.arrayBuffer().then((buffer) => new Uint8Array(buffer));

  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => (reader.result instanceof ArrayBuffer ? resolve(new Uint8Array(reader.result)) : reject(new Error('xlsx: read')));
    reader.onerror = () => reject(reader.error ?? new Error('xlsx: read'));
    reader.readAsArrayBuffer(file);
  });
}

export async function openWorkbook(file: Blob): Promise<Workbook> {
  const bytes = await readBytes(file);
  const entries = zipEntries(bytes);
  const workbook = await optionalPart(bytes, entries, 'xl/workbook.xml');
  const relationships = await optionalPart(bytes, entries, 'xl/_rels/workbook.xml.rels');

  return {
    bytes,
    entries,
    sheets: sheetParts(workbook, relationships),
    shared: sharedStrings(await optionalPart(bytes, entries, 'xl/sharedStrings.xml')),
  };
}

/** Samples of the columns of one sheet of a workbook file (`sheet` — its name; unknown — the first sheet). */
export async function workbookSamples(file: Blob, sheet: string, fromRow: number, columns: readonly number[]): Promise<ColumnSamples> {
  const book = await openWorkbook(file);
  const part = book.sheets.get(sheet) ?? [...book.sheets.values()][0];
  const entry = part === undefined ? undefined : book.entries.get(part);
  if (entry === undefined) return new Map();

  return sheetSamples(await partText(book.bytes, entry), book.shared, fromRow, columns);
}

/**
 * Samples for the step: `null` while they are read or when they cannot be (the step then shows no «Examples» column). Read again
 * for another file, sheet, first data row or set of columns.
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
    let current = true;
    workbookSamples(file, sheet, fromRow, wanted.split(',').map(Number))
      .then((samples) => {
        if (current) setState({ file, key: read, samples: samples.size === 0 ? null : samples });
      })
      .catch(() => {
        if (current) setState({ file, key: read, samples: null });
      });

    return () => {
      current = false;
    };
  }, [file, sheet, fromRow, wanted]);

  return state.file === file && state.key === key ? state.samples : null;
}
