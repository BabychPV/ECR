import { describe, it, expect } from 'vitest';
import type { RegistryRow } from '@/features/registries/rows/api';
import {
  draftOf,
  duplicateKeys,
  isDirty,
  newDraft,
  parseBlock,
  pastedBool,
  planBlockPaste,
  problemsByRow,
  setCell,
  toBatch,
  validateCell,
  type RegistryField,
  type RegistryKey,
  type RowDraft,
} from '../rowModel';

/**
 * Модель табличного редактора даних довідника (`ФВ-8.12`, FEATURE-REGISTRY-TABLES §8.4).
 *
 * ⚠ Мутаційні докази (перевірено руками, 2026-09-30; кожна мутація — червоний тест):
 *   - `toBatch` без `baseVersion` для наявного рядка → «наявний рядок несе baseVersion» червоний;
 *   - `setCell` без зняття повернутого значення → «повернення до збереженого знімає правку» червоний;
 *   - `duplicateKeys` без `key.ignoreCase` (завжди `true`) → «регістр важить, коли ключ…» червоний;
 *   - `validateCell` без гілки коми → «кома в десятковому» червоний;
 *   - `problemsByRow` губить `field` → «помилки звіту адресуються рядком і полем» червоний.
 */

const field = (code: string, dataType: string, isRequired = false): RegistryField => ({
  code,
  dataType,
  id: code.length,
  isRequired,
  isScopeField: false,
  lookupRegistryDefId: null,
  nameL10n: { values: { en: code } },
  unitId: null,
});

const row = (id: number, values: Record<string, string>): RegistryRow => ({
  id,
  code: `E${String(id)}`,
  display: `Entry ${String(id)}`,
  parentEntryId: null,
  validFrom: null,
  validTo: null,
  version: `v${String(id)}`,
  values: Object.fromEntries(Object.entries(values).map(([k, v]) => [k, { value: v, display: null, unit: null }])),
});

const key = (fieldCodes: string[], ignoreCase = true, isActive = true): RegistryKey => ({
  code: 'PK',
  fieldCodes,
  id: 1,
  ignoreCase,
  isActive,
  isPrimary: true,
  nameL10n: { values: { en: 'Primary' } },
});

describe('чернетка рядка', () => {
  it('повернення до збереженого знімає правку — рядок знову чистий', () => {
    const stored = row(7, { T_C: '49.99' });
    const edited = setCell(draftOf(stored), stored, 'T_C', '50');
    expect(isDirty(edited)).toBe(true);

    const back = setCell(edited, stored, 'T_C', ' 49.99 ');
    expect(back.values).toEqual({});
    expect(isDirty(back)).toBe(false);
  });

  it('порожній текст — очищення поля (null), а не порожній рядок', () => {
    const stored = row(7, { NOTE: 'x' });
    expect(setCell(draftOf(stored), stored, 'NOTE', '  ').values).toEqual({ NOTE: null });
  });
});

describe('пакет із чернеток', () => {
  it('наявний рядок несе baseVersion і лише змінені поля', () => {
    const stored = row(7, { A: '1', B: '2' });
    const draft = setCell(draftOf(stored), stored, 'B', '3');

    expect(toBatch([draft])).toEqual([
      { clientRowId: 'e:7', op: 'upsert', id: 7, code: null, baseVersion: 'v7', values: { B: '3' } },
    ]);
  });

  it('новий рядок: без id і baseVersion, порожні поля не надсилаються, ручний код обрізано', () => {
    let draft: RowDraft = { ...newDraft(1), code: ' K1 ' };
    draft = setCell(draft, undefined, 'A', '5');
    draft = setCell(draft, undefined, 'B', '');

    expect(toBatch([draft])).toEqual([
      { clientRowId: 'n:1', op: 'upsert', id: null, code: 'K1', baseVersion: null, values: { A: '5' } },
    ]);
  });

  it('видалення наявного — op delete з версією; новий, позначений до видалення, не йде зовсім', () => {
    const stored = row(9, {});
    const items = toBatch([{ ...draftOf(stored), deleted: true }, { ...newDraft(2), deleted: true }, draftOf(row(10, {}))]);

    expect(items).toEqual([{ clientRowId: 'e:9', op: 'delete', id: 9, code: null, baseVersion: 'v9', values: null }]);
  });
});

describe('перевірка комірки до сервера', () => {
  it('кома в десятковому — окрема підказка, крапка проходить', () => {
    const decimal = field('MOL', 'Decimal');
    expect(validateCell(decimal, '12,5')).toBe('decimalDot');
    expect(validateCell(decimal, '12.4246690')).toBeNull();
    expect(validateCell(decimal, 'abc')).toBe('notNumber');
  });

  it('обовʼязкове порожнє, ціле з дробом, неіснуюча дата', () => {
    expect(validateCell(field('A', 'String', true), null)).toBe('required');
    expect(validateCell(field('A', 'String'), null)).toBeNull();
    expect(validateCell(field('N', 'Int'), '1.5')).toBe('notInteger');
    expect(validateCell(field('D', 'Date'), '2026-02-30')).toBe('notDate');
    expect(validateCell(field('D', 'Date'), '2026-02-28')).toBeNull();
  });
});

describe('дублі ключа в сітці', () => {
  const fields = [field('STREAM', 'Lookup', true), field('CASE', 'String', true)];

  it('той самий ключ з іншим регістром і пробілами — дубль обох рядків', () => {
    const marks = duplicateKeys(
      [
        { rowKey: 'e:1', values: { STREAM: '162', CASE: '370 Winter' } },
        { rowKey: 'e:2', values: { STREAM: '162', CASE: '370 Summer' } },
        { rowKey: 'n:1', values: { STREAM: '162', CASE: ' 370  WINTER ' } },
      ],
      [key(['STREAM', 'CASE'])],
      fields,
    );

    expect(marks.get('n:1')).toEqual({ keyCode: 'PK', otherRow: 1 });
    expect(marks.get('e:1')).toEqual({ keyCode: 'PK', otherRow: 3 });
    expect(marks.has('e:2')).toBe(false);
  });

  it('регістр важить, коли ключ його не ігнорує; вимкнений ключ не діє; порожня частина не рахується', () => {
    const rows = [
      { rowKey: 'a', values: { STREAM: '1', CASE: 'x' } },
      { rowKey: 'b', values: { STREAM: '1', CASE: 'X' } },
      { rowKey: 'c', values: { STREAM: '1', CASE: null } },
      { rowKey: 'd', values: { STREAM: '1', CASE: null } },
    ];

    expect(duplicateKeys(rows, [key(['STREAM', 'CASE'], false)], fields).size).toBe(0);
    expect(duplicateKeys(rows, [key(['STREAM', 'CASE'], true, false)], fields).size).toBe(0);
    expect([...duplicateKeys(rows, [key(['STREAM', 'CASE'], true)], fields).keys()]).toEqual(['b', 'a']);
  });
});

describe('звіт пакета і вставка', () => {
  it('помилки звіту адресуються рядком і полем', () => {
    const map = problemsByRow({
      applied: false,
      dryRun: true,
      added: 0,
      updated: 0,
      deleted: 0,
      unchanged: 0,
      rows: [
        { clientRowId: 'e:1', entryId: 1, errors: [], status: 'updated', version: null },
        {
          clientRowId: 'n:1',
          entryId: null,
          status: 'error',
          version: null,
          errors: [{ field: 'CASE', errorCode: 'ECR-REG-4092', messageKey: 'err.ECR-REG-4092.keyTaken', params: { entryCode: 'E9' } }],
        },
      ],
    });

    expect([...map.keys()]).toEqual(['n:1']);
    expect(map.get('n:1')?.[0]).toMatchObject({ field: 'CASE', params: { entryCode: 'E9' } });
  });

  it('блок з Excel лягає від активної комірки; зайві стовпці відкидаються, рядки — за край', () => {
    const cells = planBlockPaste(
      [
        ['a', ' b ', 'c'],
        ['d', 'e', 'f'],
      ],
      4,
      1,
      ['F0', 'F1', 'F2'],
    );

    expect(cells).toEqual([
      { rowIndex: 4, field: 'F1', text: 'a' },
      { rowIndex: 4, field: 'F2', text: 'b' },
      { rowIndex: 5, field: 'F1', text: 'd' },
      { rowIndex: 5, field: 'F2', text: 'e' },
    ]);
  });

  it('буфер Excel: CRLF, табуляції, хвостовий перенос не дає порожнього рядка', () => {
    expect(parseBlock('a\tb\r\nc\td\r\n')).toEqual([
      ['a', 'b'],
      ['c', 'd'],
    ]);
  });

  it('так/ні з Excel кількома мовами; невідоме — не вгадується', () => {
    expect(pastedBool('Yes')).toBe('true');
    expect(pastedBool('0')).toBe('false');
    expect(pastedBool('maybe')).toBeNull();
  });
});
