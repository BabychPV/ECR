import { emptyDraftColumn, type DraftColumn } from './ops';

/*
 * Заголовок CSV-файлу для кроку «Columns» майстра «Add a table» (Permits, PM-08c).
 *
 * ⛔ Лише CSV. У репозиторії немає залежності для читання `.xlsx` (`package.json`: ні `xlsx`,
 * ні `exceljs`, ні `papaparse`), а додавати її з цього завдання заборонено. Книга Excel зберігається
 * як CSV одним пунктом меню; майстер читає саме такий файл і каже про це в підписі.
 */

/** Розібраний початок файлу: заголовок і кілька рядків для вгадування типів. */
export interface CsvPreview {
  readonly headers: readonly string[];
  readonly sample: readonly (readonly string[])[];
}

const Delimiters = [',', ';', '\t'] as const;

/** Роздільник за першим рядком: найчастіший поза лапками (кома, крапка з комою, табуляція). */
function detectDelimiter(text: string): string {
  const firstLine = text.split(/\r?\n/, 1)[0] ?? '';
  const counts = new Map<string, number>(Delimiters.map((d) => [d, 0]));
  let quoted = false;

  for (const char of firstLine) {
    if (char === '"') quoted = !quoted;
    else if (!quoted && counts.has(char)) counts.set(char, (counts.get(char) ?? 0) + 1);
  }

  let best: string = ',';
  let bestCount = 0;
  for (const [delimiter, count] of counts) {
    if (count > bestCount) {
      best = delimiter;
      bestCount = count;
    }
  }

  return best;
}

/** Рядки CSV за RFC 4180: лапки, подвоєні лапки, переноси всередині комірки. */
function parseRecords(text: string, delimiter: string, maxRecords: number): string[][] {
  const records: string[][] = [];
  let record: string[] = [];
  let cell = '';
  let quoted = false;

  const endCell = (): void => {
    record.push(cell);
    cell = '';
  };
  const endRecord = (): void => {
    endCell();
    if (record.some((value) => value.trim().length > 0)) records.push(record);
    record = [];
  };

  for (let i = 0; i < text.length && records.length < maxRecords; i += 1) {
    const char = text.charAt(i);

    if (quoted) {
      if (char === '"' && text.charAt(i + 1) === '"') {
        cell += '"';
        i += 1;
      } else if (char === '"') {
        quoted = false;
      } else {
        cell += char;
      }
    } else if (char === '"') {
      quoted = true;
    } else if (char === delimiter) {
      endCell();
    } else if (char === '\n') {
      endRecord();
    } else if (char !== '\r') {
      cell += char;
    }
  }

  if (records.length < maxRecords && (cell.length > 0 || record.length > 0)) endRecord();

  return records;
}

/**
 * Заголовок і до п'яти рядків даних.
 *
 * ⚠ Порожні комірки в кінці заголовка відкидаються (Excel зберігає зайві роздільники), порожні
 * посередині лишаються — майстер покаже їх як стовпці без назви, і крок не пройде, доки їх не
 * назвуть або не приберуть.
 */
export function parseCsvPreview(rawText: string): CsvPreview {
  const text = rawText.replace(/^﻿/, '');
  const records = parseRecords(text, detectDelimiter(text), 6);
  const header = records[0] ?? [];

  let end = header.length;
  while (end > 0 && (header[end - 1] ?? '').trim().length === 0) end -= 1;

  return {
    headers: header.slice(0, end).map((value) => value.trim()),
    sample: records.slice(1),
  };
}

const NumberPattern = /^[+-]?\d+([.,]\d+)?$/;
const DatePattern = /^(\d{4}-\d{2}-\d{2}|\d{1,2}[./]\d{1,2}[./]\d{4})/;
const BoolPattern = /^(true|false|yes|no)$/i;

/**
 * Тип стовпця за кількома значеннями: усі числа — `Decimal`, усі дати — `Date`, усі так/ні — `Bool`.
 *
 * ⚠ Без значень або зі змішаними — `String`: хибно вгаданий вужчий тип гірший за текст, який
 * людина сама звузить на кроці (банер просить перевірити типи).
 */
export function guessKind(values: readonly string[]): string {
  const filled = values.map((v) => v.trim().replace(/\s/g, '')).filter((v) => v.length > 0);
  if (filled.length === 0) return 'String';
  if (filled.every((v) => NumberPattern.test(v))) return 'Decimal';
  if (filled.every((v) => DatePattern.test(v))) return 'Date';
  if (filled.every((v) => BoolPattern.test(v))) return 'Bool';

  return 'String';
}

/** Стовпці з заголовка: перший — ключовий і обов'язковий (як у мокапі), типи вгадані. */
export function draftColumnsFromCsv(preview: CsvPreview): DraftColumn[] {
  return preview.headers.map((header, index) =>
    emptyDraftColumn({
      name: header,
      dataType: guessKind(preview.sample.map((row) => row[index] ?? '')),
      key: index === 0,
      required: index === 0,
    }),
  );
}
