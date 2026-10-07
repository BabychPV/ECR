// Мінімальний CSV-парсер (RFC 4180: лапки, "" усередині, \r\n). Роздільник
// визначається за першим рядком: `;` або `,` (Excel у uk-UA пише `;`).

export type CsvRow = Record<string, string>;

export function detectDelimiter(headerLine: string): string {
  const semi = (headerLine.match(/;/g) ?? []).length;
  const comma = (headerLine.match(/,/g) ?? []).length;
  return semi > comma ? ';' : ',';
}

function splitRecords(text: string, delimiter: string): string[][] {
  const records: string[][] = [];
  let field = '';
  let record: string[] = [];
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i]!;
    if (quoted) {
      if (ch === '"' && text[i + 1] === '"') {
        field += '"';
        i++;
      } else if (ch === '"') {
        quoted = false;
      } else {
        field += ch;
      }
    } else if (ch === '"') {
      quoted = true;
    } else if (ch === delimiter) {
      record.push(field);
      field = '';
    } else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && text[i + 1] === '\n') i++;
      record.push(field);
      records.push(record);
      record = [];
      field = '';
    } else {
      field += ch;
    }
  }
  if (field.length > 0 || record.length > 0) {
    record.push(field);
    records.push(record);
  }
  return records.filter((r) => r.some((f) => f.trim() !== ''));
}

export function parseCsv(input: string): CsvRow[] {
  const text = input.replace(/^\uFEFF/, '');
  const firstLine = text.split(/\r?\n/, 1)[0] ?? '';
  const [header, ...body] = splitRecords(text, detectDelimiter(firstLine));
  if (!header) return [];
  const names = header.map((h) => h.trim());
  return body.map((rec) => {
    const row: CsvRow = {};
    names.forEach((name, i) => {
      row[name] = (rec[i] ?? '').trim();
    });
    return row;
  });
}

/** Число з CSV: десяткова кома і пробіли-роздільники тисяч допускаються. */
export function parseNumber(raw: string): number | null {
  const s = raw.replace(/[\s\u00A0]/g, '').replace(',', '.');
  if (s === '') return null;
  const n = Number(s);
  return Number.isFinite(n) ? n : null;
}
