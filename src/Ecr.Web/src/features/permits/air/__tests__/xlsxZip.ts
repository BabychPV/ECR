import { DecompressionStream } from 'node:stream/web';
import { deflateRawSync } from 'node:zlib';
import { vi } from 'vitest';

/** The browsers' `DecompressionStream` (jsdom has none): Node's own, the same Compression Streams API. */
export function withDecompression(): void {
  vi.stubGlobal('DecompressionStream', DecompressionStream);
}

/**
 * A real (tiny) .xlsx for the tests of the «Mapping» step samples (UX 20.11): a zip with deflated parts (`store` — names kept
 * uncompressed, as some writers do). CRC is not filled: the reader of the step does not check it.
 */
export function zip(files: Readonly<Record<string, string>>, store: readonly string[] = [], declared: Readonly<Record<string, number>> = {}): Uint8Array {
  const encoder = new TextEncoder();
  const locals: Uint8Array[] = [];
  const centrals: Uint8Array[] = [];
  let offset = 0;
  for (const [name, text] of Object.entries(files)) {
    const nameBytes = encoder.encode(name);
    const raw = encoder.encode(text);
    const stored = store.includes(name);
    const data = stored ? raw : new Uint8Array(deflateRawSync(raw));
    const local = new Uint8Array(30 + nameBytes.length + data.length);
    const view = new DataView(local.buffer);
    view.setUint32(0, 0x04034b50, true);
    view.setUint16(8, stored ? 0 : 8, true);
    view.setUint32(18, data.length, true);
    view.setUint32(22, raw.length, true);
    view.setUint16(26, nameBytes.length, true);
    local.set(nameBytes, 30);
    local.set(data, 30 + nameBytes.length);
    const central = new Uint8Array(46 + nameBytes.length);
    const cv = new DataView(central.buffer);
    cv.setUint32(0, 0x02014b50, true);
    cv.setUint16(10, stored ? 0 : 8, true);
    cv.setUint32(20, data.length, true);
    cv.setUint32(24, declared[name] ?? raw.length, true);
    cv.setUint16(28, nameBytes.length, true);
    cv.setUint32(42, offset, true);
    central.set(nameBytes, 46);
    locals.push(local);
    centrals.push(central);
    offset += local.length;
  }
  const directorySize = centrals.reduce((sum, part) => sum + part.length, 0);
  const end = new Uint8Array(22);
  const ev = new DataView(end.buffer);
  ev.setUint32(0, 0x06054b50, true);
  ev.setUint16(8, centrals.length, true);
  ev.setUint16(10, centrals.length, true);
  ev.setUint32(12, directorySize, true);
  ev.setUint32(16, offset, true);
  const out = new Uint8Array(offset + directorySize + 22);
  let at = 0;
  for (const part of [...locals, ...centrals, end]) {
    out.set(part, at);
    at += part.length;
  }

  return out;
}

export const WorkbookXml = `<?xml version="1.0" encoding="UTF-8"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
<sheets><sheet name="Summary" sheetId="1" r:id="rId1"/><sheet name="Table 3.3" sheetId="2" r:id="rId2"/></sheets></workbook>`;

export const RelsXml = `<?xml version="1.0" encoding="UTF-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
<Relationship Target="/xl/worksheets/sheet2.xml" Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"/>
</Relationships>`;

export const SharedXml = `<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="4" uniqueCount="4">
<si><t>Source number</t></si><si><t>Цех 1</t></si><si><r><t>Цех</t></r><r><t xml:space="preserve"> 2</t></r><rPh><t>ignored</t></rPh></si><si><t>A &amp; B</t></si></sst>`;

/** «Table 3.3»: header at row 4, data from row 9 — B workshop (shared strings), G source number, AA t/year. */
export const SheetXml = `<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
<row r="4"><c r="B4" t="s"><v>0</v></c><c r="G4" t="inlineStr"><is><t>header</t></is></c></row>
<row r="8"/>
<row r="9"><c r="B9" t="s"><v>1</v></c><c r="G9"><v>1</v></c><c r="AA9"><v>0.25</v></c></row>
<row r="10"><c r="B10" t="s"><v>1</v></c><c r="G10"><v>2</v></c><c r="H10" t="b"><v>1</v></c><c r="AA10"><v>1.5</v></c></row>
<row r="11"><c r="B11" t="s"><v>2</v></c><c r="G11"/><c r="I11" t="inlineStr"><is><t>inline &lt;x&gt;</t></is></c></row>
<row r="12"><c r="B12" t="s"><v>3</v></c><c r="G12"><v>3</v></c></row>
<row r="13"><c r="B13" t="str"><v>far below</v></c><c r="G13"><v>4</v></c></row>
</sheetData></worksheet>`;

/** The workbook above as bytes (`declared` — part sizes to declare in the zip directory instead of the real ones). */
export function sampleWorkbookBytes(declared: Readonly<Record<string, number>> = {}): Uint8Array {
  return zip(
    {
      'xl/workbook.xml': WorkbookXml,
      'xl/_rels/workbook.xml.rels': RelsXml,
      'xl/sharedStrings.xml': SharedXml,
      'xl/worksheets/sheet1.xml': '<worksheet><sheetData><row r="9"><c r="B9" t="inlineStr"><is><t>summary</t></is></c></row></sheetData></worksheet>',
      'xl/worksheets/sheet2.xml': SheetXml,
    },
    ['xl/workbook.xml'],
    declared,
  );
}

/** The workbook above as the file the user chose. */
export function sampleWorkbook(name = 'limits.xlsx', declared: Readonly<Record<string, number>> = {}): File {
  return new File([sampleWorkbookBytes(declared)], name, { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' });
}