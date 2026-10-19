import { afterEach, describe, expect, it, vi } from 'vitest';
import type { RegistryBatchResult } from '@/features/registries/rows/api';
import * as rowsApi from '@/features/registries/rows/api';
import { MaxPastedRows } from '../../lib/pasteRows';
import { delimitedToTabs, ImportChunk, isPlanClean, planImport, runImport } from '../importRows';
import { fieldsOf, type PickerData } from '../model';
import { permitType } from './permitFixtures';

vi.mock('@/features/registries/rows/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/registries/rows/api')>()),
  saveBatch: vi.fn(),
}));
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => key,
}));

const save = vi.mocked(rowsApi.saveBatch);
afterEach(() => vi.clearAllMocks());

const fields = fieldsOf(permitType().tables[0]!, new Map());
const picker: PickerData = {
  sources: { shared: { Substances: ['Methane'] }, permitTables: { Places: ['Pond 1'] } },
  ids: {
    Substances: new Map([['Methane', '41']]),
    'permit:Places': new Map([['Pond 1', '31']]),
    PRM_WD_Use: new Map([['Cooling', '21']]),
  },
};

// Порядок стовпців: Name, Annual, Count, Active, Issued, Place, Use, Substance.
const line = (cells: string[]) => cells.join('\t');

describe('planImport', () => {
  it('рядок вставки стає пакетом нового рядка; посилання зіставляються з id без урахування регістру', () => {
    const plan = planImport(line(['A', '1,5', '2', 'так', '01.03.2026', 'pond 1', 'Cooling', 'METHANE']), fields, picker, 5, false);
    expect(plan.issues).toEqual([]);
    expect(plan.rows).toHaveLength(1);
    expect(plan.rows[0]?.clean).toBe(true);
    expect(plan.rows[0]?.item.values).toEqual({
      PERMIT: '5',
      Name: 'A',
      Annual: '1.5',
      Count: '2',
      Active: 'true',
      Issued: '2026-03-01',
      Place: '31',
      Use: '21',
      Substance: '41',
    });
  });

  it('нерозпізнана клітинка не губиться мовчки: рядок позначено, зауваження названо', () => {
    const plan = planImport(
      [line(['A', '12abc', '', '', '', '', '', '']), line(['B', '2', '', '', '', 'Nowhere', '', ''])].join('\n'),
      fields,
      picker,
      5,
      false,
    );
    expect(plan.rows.map((row) => row.clean)).toEqual([false, false]);
    expect(plan.issues.map((issue) => [issue.row, issue.column, issue.reason])).toEqual([
      [1, 'Annual', 'number'],
      [2, 'Place', 'unknownValue'],
    ]);
  });

  it('Int із дробом — зауваження integer', () => {
    const plan = planImport(line(['A', '1', '2,5', '', '', '', '', '']), fields, picker, 5, false);
    expect(plan.issues.map((issue) => issue.reason)).toEqual(['integer']);
    expect(plan.rows[0]?.clean).toBe(false);
  });

  it('заголовок файлу пропускається, нумерація рядків починається з першого даних', () => {
    const plan = planImport(`Name\tAnnual\n${line(['A', '1'])}`, fields, picker, 5, true);
    expect(plan.rows.map((row) => row.index)).toEqual([1]);
    expect(plan.rows[0]?.item.values?.['Name']).toBe('A');
  });

  it('зайві клітинки праворуч рахуються і блокують весь запис', () => {
    const plan = planImport(line(['A', '1', '', '', '', '', '', '', 'x', 'y']), fields, picker, 5, false);
    expect(plan.extraCells).toBe(2);
    expect(isPlanClean(plan)).toBe(false);
  });

  it('«все або нічого»: чистий план — так; порожній, із зауваженням і з нерозпізнаним значенням — ні', () => {
    expect(isPlanClean(planImport(line(['A', '1']), fields, picker, 5, false))).toBe(true);
    expect(isPlanClean(planImport('', fields, picker, 5, false))).toBe(false);
    expect(isPlanClean(planImport(line(['A', '12abc']), fields, picker, 5, false))).toBe(false);
    expect(isPlanClean(planImport(line(['A', '1', '', '', '', 'Nowhere']), fields, picker, 5, false))).toBe(false);
  });

  it('понад 5000 рядків: зауваження limit, план не чистий (рівно 5000 — чистий)', () => {
    const many = (count: number): string => Array.from({ length: count }, (_, index) => line([`N${String(index)}`, '1'])).join('\n');
    const over = planImport(many(MaxPastedRows + 1), fields, picker, 5, false);
    expect(over.issues.map((issue) => issue.reason)).toEqual(['limit']);
    expect(isPlanClean(over)).toBe(false);
    expect(isPlanClean(planImport(many(MaxPastedRows), fields, picker, 5, false))).toBe(true);
  });
});

describe('дублі ключа всередині файлу (все або нічого)', () => {
  const batch = (names: readonly string[]): string => names.map((name) => line([name, '1'])).join('\n');
  const distinct = (count: number): string[] => Array.from({ length: count }, (_, index) => `N${String(index)}`);

  it('дубль між рядками 1 і 501 (різні шматки): план не чистий, зауваження на обох рядках', () => {
    const names = distinct(ImportChunk + 5);
    names[ImportChunk] = names[0] ?? '';
    const plan = planImport(batch(names), fields, picker, 5, false);
    expect(isPlanClean(plan)).toBe(false);
    expect(plan.issues.map((issue) => [issue.row, issue.column, issue.reason])).toEqual([
      [1, 'Name', 'duplicateKey'],
      [ImportChunk + 1, 'Name', 'duplicateKey'],
    ]);
    expect(plan.rows.filter((row) => !row.clean).map((row) => row.index)).toEqual([1, ImportChunk + 1]);
  });

  it('запис не починається: з нечистим планом жоден шматок не пишеться (шлюз діалогу)', async () => {
    const names = distinct(ImportChunk + 5);
    names[ImportChunk] = names[0] ?? '';
    const plan = planImport(batch(names), fields, picker, 5, false);
    // Діалог кличе runImport лише для чистого плану (PasteDialog.check/apply).
    if (isPlanClean(plan)) await runImport('PRM_WD_Limits', plan.rows, false, () => undefined);
    expect(save).not.toHaveBeenCalled();
  });

  it('порівняння без регістру й крайніх пробілів; різні ключі не конфліктують', () => {
    expect(planImport(batch(['Pond', ' pond ']), fields, picker, 5, false).issues.map((issue) => issue.reason)).toEqual([
      'duplicateKey',
      'duplicateKey',
    ]);
    expect(isPlanClean(planImport(batch(['Pond', 'Pond 2']), fields, picker, 5, false))).toBe(true);
  });
});

describe('дублі ключа: канонічна форма (canonicalKey)', () => {
  /** Ключем стає лише названий стовпець (решта - не ключ), щоб перевірити типи частин. */
  const keyedBy = (code: string): typeof fields =>
    fields.map((field) => ({ ...field, column: { ...field.column, key: field.column.code === code } }));
  const names = (values: readonly string[], keyed: typeof fields = fields, ignoreCase?: boolean) =>
    planImport(values.map((name) => line([name, '1'])).join('\n'), keyed, picker, 5, false, ignoreCase);
  const reasons = (plan: ReturnType<typeof planImport>): string[] => plan.issues.map((issue) => issue.reason);

  it('числовий ключ: однакове число в рядках 1 і 501 - дубль, план не чистий', () => {
    const counts = Array.from({ length: ImportChunk + 5 }, (_, index) => String(index + 100));
    counts[0] = '7';
    counts[ImportChunk] = '7';
    const plan = planImport(counts.map((count, index) => line([`N${String(index)}`, '1', count])).join('\n'), keyedBy('Count'), picker, 5, false);
    expect(plan.issues.map((issue) => [issue.row, issue.column, issue.reason])).toEqual([
      [1, 'Count', 'duplicateKey'],
      [ImportChunk + 1, 'Count', 'duplicateKey'],
    ]);
    expect(isPlanClean(plan)).toBe(false);
  });

  it('логічний і датовий ключі зводяться за типом', () => {
    const active = planImport([line(['A', '1', '', 'так']), line(['B', '1', '', 'true'])].join('\n'), keyedBy('Active'), picker, 5, false);
    expect(reasons(active)).toEqual(['duplicateKey', 'duplicateKey']);
    const issued = planImport([line(['A', '1', '', '', '01.03.2026']), line(['B', '1', '', '', '2026-03-01'])].join('\n'), keyedBy('Issued'), picker, 5, false);
    expect(reasons(issued)).toEqual(['duplicateKey', 'duplicateKey']);
  });

  it('текст: внутрішні пробіли, NBSP і крайні пробіли згортаються', () => {
    expect(reasons(names(['Pond  1', ' Pond 1 ']))).toEqual(['duplicateKey', 'duplicateKey']);
  });

  it('текст: NFC - складений і розкладений «é» один ключ', () => {
    expect(reasons(names(['café', 'café']))).toEqual(['duplicateKey', 'duplicateKey']);
  });

  it('ignoreCase=false: «A» і «a» не дубль; за замовчуванням (true) - дубль', () => {
    expect(reasons(names(['A', 'a'], fields, false))).toEqual([]);
    expect(reasons(names(['A', 'a']))).toEqual(['duplicateKey', 'duplicateKey']);
  });
});

describe('delimitedToTabs', () => {
  it('крапка з комою, лапки з комою всередині та подвоєні лапки', () => {
    expect(delimitedToTabs('Name;Note\nA;"x; y"\nB;"say ""hi"""\n')).toBe('Name\tNote\nA\tx; y\nB\tsay "hi"');
  });

  it('кома, BOM, CRLF, порожні рядки відкидаються', () => {
    expect(delimitedToTabs('﻿a,b\r\n\r\n1,2\r\n')).toBe('a\tb\n1\t2');
  });

  it('перенос рядка всередині лапок не розбиває рядок', () => {
    expect(delimitedToTabs('a,b\n"one\ntwo",3')).toBe('a\tb\none two\t3');
  });
});

function ok(count: number, applied = true): RegistryBatchResult {
  return {
    added: count,
    applied,
    deleted: 0,
    dryRun: !applied,
    rows: Array.from({ length: count }, (_, index) => ({ clientRowId: `row-${String(index + 1)}`, entryId: null, errors: [], status: 'added', version: null })),
    unchanged: 0,
    updated: 0,
  };
}

describe('runImport', () => {
  const rows = (count: number) =>
    planImport(Array.from({ length: count }, (_, index) => line([`N${String(index)}`, '1'])).join('\n'), fields, picker, 5, false).rows;

  /** Сервер: перевірка (`dryRun`) і запис проходять; `reject` — рядки, які перевірка відхиляє. */
  function server(reject: ReadonlySet<string> = new Set()): void {
    save.mockImplementation((_code, items, dryRun) =>
      Promise.resolve({
        ...ok(items.length, !dryRun),
        rows: items.map((item) => ({
          clientRowId: item.clientRowId,
          entryId: null,
          errors: dryRun && reject.has(item.clientRowId) ? [{ errorCode: 'e', field: null, messageKey: 'err.x', params: {} }] : [],
          status: 'added',
          version: null,
        })),
      }),
    );
  }

  it('ділить на шматки по ImportChunk, звітує прогрес; перевірка всіх шматків ПЕРЕД першим записом', async () => {
    server();
    const progress: number[] = [];
    const run = await runImport('PRM_WD_Limits', rows(ImportChunk + 5), false, (done) => progress.push(done));
    expect(save.mock.calls.map((call) => [call[2], call[1].length])).toEqual([
      [true, ImportChunk],
      [true, 5],
      [false, ImportChunk],
      [false, 5],
    ]);
    expect(progress).toEqual([ImportChunk, ImportChunk + 5, ImportChunk + 5 + ImportChunk, 2 * (ImportChunk + 5)]);
    expect(run).toMatchObject({ ok: ImportChunk + 5, stopped: false, chunksWritten: 2, refused: false });
  });

  it('відмова перевірки ДРУГОГО шматка: не записано нічого, навіть першого шматка', async () => {
    server(new Set([`row-${String(ImportChunk + 2)}`]));
    const run = await runImport('PRM_WD_Limits', rows(ImportChunk + 5), false, () => undefined);
    expect(save.mock.calls.map((call) => call[2])).toEqual([true, true]);
    expect(run).toMatchObject({ ok: 0, stopped: false, chunksWritten: 0, refused: true });
    expect(run.failures.map((failure) => failure.index)).toEqual([ImportChunk + 2]);
  });

  it('dryRun проходить усі шматки й збирає помилки рядків за номерами плану', async () => {
    save.mockResolvedValue({
      ...ok(2, false),
      rows: [
        { clientRowId: 'row-1', entryId: null, errors: [], status: 'added', version: null },
        { clientRowId: 'row-2', entryId: null, errors: [{ errorCode: 'ECR-REG-4221', field: 'Name', messageKey: 'err.x', params: {} }], status: 'error', version: null },
      ],
    });
    const run = await runImport('PRM_WD_Limits', rows(2), true, () => undefined);
    expect(run.ok).toBe(1);
    expect(run.failures).toEqual([{ index: 2, messages: ['err.x'] }]);
    expect(run.stopped).toBe(false);
  });

  it('відмова середнього шматка ПІД ЧАС запису (гонитва): зупинка, попередній шматок записаний — відома межа', async () => {
    save.mockImplementation((_code, items, dryRun) => {
      const second = items[0]?.clientRowId === `row-${String(ImportChunk + 1)}`;
      if (!dryRun && second) {
        return Promise.resolve({
          ...ok(items.length, false),
          rows: [{ clientRowId: `row-${String(ImportChunk + 1)}`, entryId: null, errors: [{ errorCode: 'e', field: null, messageKey: 'err.y', params: {} }], status: 'error', version: null }],
        });
      }
      return Promise.resolve(ok(items.length, !dryRun));
    });
    const run = await runImport('PRM_WD_Limits', rows(ImportChunk + 3), false, () => undefined);
    expect(save.mock.calls.map((call) => call[2])).toEqual([true, true, false, false]);
    expect(run).toMatchObject({ ok: ImportChunk, stopped: true, chunksWritten: 1, refused: false });
    expect(run.failures[0]?.index).toBe(ImportChunk + 1);
  });
});
