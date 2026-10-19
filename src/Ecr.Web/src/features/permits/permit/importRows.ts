import { serverMessage } from '@/features/registries/rc816/serverMessage';
import { saveBatch, type RegistryBatchItem } from '@/features/registries/rows/api';
import type { PermitCellValue } from '../lib/columns';
import { parsePastedRows } from '../lib/pasteRows';
import { newRowItem, type PermitField, type PickerData, pickerOptions } from './model';

/*
 * Вставка з Excel і імпорт файлу в таблицю дозволу (PM-07).
 *
 * ⚠ Окремого «пакетного» ендпоінта рядків дозволу немає: рядки пишуться наявним `entries/batch` довідника таблиці
 * (до 2000 рядків за виклик). Тут блок розбирається `parsePastedRows` (PM-06), зіставляється з пікерами й іде
 * шматками по {@link ImportChunk} рядків. Кожен шматок атомарний (`applied: false` = не записано нічого).
 *
 * ⛔ Рішення 19.10: «все або нічого». Зауваження розбору, понад 5000 рядків (`MaxPastedRows`) чи відмова сервера на
 * перевірці блокують ВЕСЬ запис; перевірка всіх шматків йде ДО першого запису. Єдиний виняток, якого клієнт не
 * контролює, - відмова середнього шматка під час запису (гонитва): це `stopped` і відома межа.
 */

/** Рядків за один виклик `entries/batch` (межа сервера — 2000). */
export const ImportChunk = 500;

export type ImportIssueReason = 'number' | 'bool' | 'date' | 'limit' | 'integer' | 'unknownValue';

/** Клітинка, яку не вдалось прочитати: рядок вставки (з 1), стовпець, сирий текст. */
export interface ImportIssue {
  readonly row: number;
  readonly column: string;
  readonly text: string;
  readonly reason: ImportIssueReason;
}

export interface ImportRow {
  /** Номер рядка вставки (з 1, без порожніх). */
  readonly index: number;
  readonly item: RegistryBatchItem;
  /** Рядок прочитано без зауважень. */
  readonly clean: boolean;
}

export interface ImportPlan {
  readonly rows: readonly ImportRow[];
  readonly issues: readonly ImportIssue[];
  /** Клітинок правіше за останній стовпець таблиці (відкинуто). */
  readonly extraCells: number;
}

function isoText(value: PermitCellValue): string {
  if (value === null || value === undefined) return '';
  return typeof value === 'boolean' ? (value ? 'true' : 'false') : String(value);
}

/**
 * Блок тексту → пакет нових рядків. Рядок із нерозпізнаною клітинкою (`number`, `bool`, `date`, значення, якого
 * немає у списку) НЕ додається мовчки з порожньою клітинкою: він лишається в плані як «з зауваженням» і на запис не
 * йде, поки текст не виправлено.
 */
export function planImport(
  text: string,
  fields: readonly PermitField[],
  picker: PickerData,
  permitId: number,
  skipHeader: boolean,
): ImportPlan {
  const body = skipHeader ? text.replace(/\r\n?/g, '\n').split('\n').slice(1).join('\n') : text;
  const parsed = parsePastedRows(body, fields.map((field) => field.column));
  const issues: ImportIssue[] = parsed.issues.map((issue) => ({ ...issue }));
  const bad = new Set<number>(parsed.issues.map((issue) => issue.row));

  // Підпис → id для кожного пікера, без урахування регістру й крайніх пробілів.
  const idByLabel = new Map<string, Map<string, string>>();
  for (const field of fields) {
    if (field.column.type !== 'choice' && field.column.type !== 'link') continue;
    idByLabel.set(
      field.column.code,
      new Map(pickerOptions(field, picker).map((option) => [option.label.trim().toLowerCase(), option.value])),
    );
  }

  const rows: ImportRow[] = parsed.rows.map((row, position) => {
    const index = position + 1;
    const form: Record<string, string> = {};

    for (const field of fields) {
      const { column } = field;
      const value = row.values[column.code];
      let cell = isoText(value);

      const labels = idByLabel.get(column.code);
      if (labels !== undefined && cell !== '') {
        const id = labels.get(cell.trim().toLowerCase());
        if (id === undefined) {
          issues.push({ row: index, column: column.code, text: cell, reason: 'unknownValue' });
          bad.add(index);
          cell = '';
        } else {
          cell = id;
        }
      }

      if (column.type === 'number' && field.source.dataType === 'Int' && typeof value === 'number' && !Number.isInteger(value)) {
        issues.push({ row: index, column: column.code, text: String(value), reason: 'integer' });
        bad.add(index);
      }
      form[column.code] = cell;
    }

    return { index, item: newRowItem(`row-${String(index)}`, permitId, fields, form), clean: !bad.has(index) };
  });

  return { rows, issues, extraCells: parsed.extraCells };
}

/** Рядок плану, що не пройшов перевірку сервера, і чому. */
export interface RowFailure {
  readonly index: number;
  readonly messages: readonly string[];
}

export interface BatchRun {
  /** Скільки рядків записано (для прогону `dryRun` — скільки пройшло б). */
  readonly ok: number;
  readonly failures: readonly RowFailure[];
  /** Прогін обірвано відмовою шматка під час запису (недетермінована гонитва після успішної перевірки). */
  readonly stopped: boolean;
  /** Скільки шматків записано до зупинки. */
  readonly chunksWritten: number;
  /** Перевірка ВСІХ шматків перед записом відхилила рядки: не записано нічого. */
  readonly refused: boolean;
}

/** Межа розміру CSV-файлу, байт (5 МБ): більший не читається. */
export const MaxImportFileMb = 5;
export const MaxImportFileBytes = MaxImportFileMb * 1024 * 1024;

/** План можна перевіряти й писати лише цілком: жодного зауваження, зайвих клітинок і хоч один рядок. */
export function isPlanClean(plan: ImportPlan): boolean {
  return plan.rows.length > 0 && plan.issues.length === 0 && plan.extraCells === 0;
}

/** Один прохід усіма шматками з `dryRun`: помилки рядків за номерами плану. */
async function sweepDryRun(
  code: string,
  rows: readonly ImportRow[],
  byClient: ReadonlyMap<string, number>,
  onProgress: (done: number) => void,
): Promise<{ ok: number; failures: RowFailure[] }> {
  const failures: RowFailure[] = [];
  let ok = 0;
  for (let start = 0; start < rows.length; start += ImportChunk) {
    const chunk = rows.slice(start, start + ImportChunk);
    const result = await saveBatch(code, chunk.map((row) => row.item), true);
    failures.push(
      ...result.rows
        .filter((row) => row.errors.length > 0)
        .map((row) => ({
          index: byClient.get(row.clientRowId) ?? 0,
          messages: row.errors.map((error) => serverMessage(error.messageKey, error.params)),
        })),
    );
    ok += result.rows.filter((row) => row.errors.length === 0).length;
    onProgress(start + chunk.length);
  }
  return { ok, failures };
}

/**
 * «Все або нічого» (рішення 19.10). `dryRun` проходить усі шматки й збирає помилки рядків. Справжній запис СПЕРШУ
 * проганяє `dryRun` усіх шматків і, якщо сервер відхилив хоч один рядок, не пише нічого (`refused`); лише чиста
 * перевірка відкриває запис шматками. Відмова середнього шматка вже під час запису (гонитва з чужою правкою) —
 * `stopped`: попередні шматки лишаються, це відома межа (FEATURE-PERMITS.md).
 */
export async function runImport(
  code: string,
  rows: readonly ImportRow[],
  dryRun: boolean,
  onProgress: (done: number, total: number) => void,
): Promise<BatchRun> {
  const byClient = new Map(rows.map((row) => [row.item.clientRowId, row.index]));
  const total = dryRun ? rows.length : rows.length * 2;

  const verified = await sweepDryRun(code, rows, byClient, (done) => onProgress(done, total));
  if (dryRun) return { ok: verified.ok, failures: verified.failures, stopped: false, chunksWritten: 0, refused: false };
  if (verified.failures.length > 0) {
    return { ok: 0, failures: verified.failures, stopped: false, chunksWritten: 0, refused: true };
  }

  let ok = 0;
  let chunksWritten = 0;
  for (let start = 0; start < rows.length; start += ImportChunk) {
    const chunk = rows.slice(start, start + ImportChunk);
    const result = await saveBatch(code, chunk.map((row) => row.item), false);
    onProgress(rows.length + start + chunk.length, total);
    if (!result.applied) {
      const failures = result.rows
        .filter((row) => row.errors.length > 0)
        .map((row) => ({
          index: byClient.get(row.clientRowId) ?? 0,
          messages: row.errors.map((error) => serverMessage(error.messageKey, error.params)),
        }));
      return { ok, failures, stopped: true, chunksWritten, refused: false };
    }
    ok += chunk.length;
    chunksWritten += 1;
  }
  return { ok, failures: [], stopped: false, chunksWritten, refused: false };
}

/** CSV/TSV з файлу → текст із табуляціями між клітинками (формат вставки). Роздільник — найчастіший у першому рядку. */
export function delimitedToTabs(text: string): string {
  const source = text.replace(/^﻿/, '');
  const firstLine = source.split(/\r?\n/, 1)[0] ?? '';
  const delimiter = [';', ',', '\t']
    .map((candidate) => ({ candidate, count: firstLine.split(candidate).length }))
    .sort((a, b) => b.count - a.count)[0]?.candidate ?? ',';

  const rows: string[][] = [[]];
  let cell = '';
  let quoted = false;
  const push = (): void => {
    rows[rows.length - 1]?.push(cell);
    cell = '';
  };

  for (let index = 0; index < source.length; index += 1) {
    const char = source.charAt(index);
    if (quoted) {
      if (char === '"' && source.charAt(index + 1) === '"') {
        cell += '"';
        index += 1;
      } else if (char === '"') {
        quoted = false;
      } else {
        cell += char === '\n' || char === '\r' || char === '\t' ? ' ' : char;
      }
    } else if (char === '"') {
      quoted = true;
    } else if (char === delimiter) {
      push();
    } else if (char === '\n' || char === '\r') {
      if (char === '\r' && source.charAt(index + 1) === '\n') index += 1;
      push();
      rows.push([]);
    } else {
      cell += char;
    }
  }
  push();

  return rows
    .filter((row) => row.some((value) => value.trim() !== ''))
    .map((row) => row.map((value) => value.replace(/\t/g, ' ')).join('\t'))
    .join('\n');
}
