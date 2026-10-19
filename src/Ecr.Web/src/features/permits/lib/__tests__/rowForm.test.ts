import { describe, expect, it } from 'vitest';
import type { PermitColumn } from '../columns';
import { keyColumns, keyPart, requiredColumns, validateRow, type RowValues } from '../rowForm';

/**
 * Мутаційний доказ (вручну, відновлено): прибрати `row !== editing` — «редагування самого себе не
 * дубль» червоніє; замінити `isEmptyCell` на `=== ''` — червоніють «null / пробіли»; повернути
 * `String(v || '')` замість `keyPart` — червоніє «ключ 0».
 */
const columns: PermitColumn[] = [
  { code: 'Substance', label: 'Substance', type: 'link', required: true, key: true, ref: 'Substances' },
  { code: 'Code', label: 'Code', type: 'text', derived: true, required: true },
  { code: 'Site', label: 'Site', type: 'link', required: true, key: true, ref: 'Sites' },
  { code: 'Emission', label: 'Emission', type: 'number', required: true },
  { code: 'Rate', label: 'Rate', type: 'number' },
];

const row = (v: Record<string, string | number | boolean | null>): RowValues => v;

describe('rowForm: обовʼязкові', () => {
  it('requiredColumns не включає похідні, keyColumns — лише ключові', () => {
    expect(requiredColumns(columns).map((c) => c.code)).toEqual(['Substance', 'Site', 'Emission']);
    expect(keyColumns(columns).map((c) => c.code)).toEqual(['Substance', 'Site']);
  });

  it('усе заповнено — ok', () => {
    expect(validateRow(columns, row({ Substance: 'Soot', Site: 'S1', Emission: 0.5 }))).toEqual({ ok: true });
  });

  it('порожні обовʼязкові перелічені в порядку стовпців', () => {
    expect(validateRow(columns, row({ Substance: '', Site: null, Emission: 1 }))).toEqual({
      ok: false,
      kind: 'required',
      missing: ['Substance', 'Site'],
    });
  });

  it('рядок із самих пробілів і undefined — порожні; 0 і false — НЕ порожні', () => {
    expect(validateRow(columns, row({ Substance: '   ', Emission: 1 }))).toMatchObject({
      kind: 'required',
      missing: ['Substance', 'Site'],
    });
    expect(validateRow(columns, row({ Substance: 'A', Site: 'B', Emission: 0 }))).toEqual({ ok: true });

    const flag: PermitColumn[] = [{ code: 'f', label: 'f', type: 'bool', required: true }];
    expect(validateRow(flag, row({ f: false }))).toEqual({ ok: true });
    expect(validateRow(flag, row({}))).toMatchObject({ kind: 'required', missing: ['f'] });
  });

  it('похідний обовʼязковий стовпець (Code) не вимагається від людини', () => {
    const result = validateRow(columns, row({ Substance: 'A', Site: 'B', Emission: 1, Code: null }));
    expect(result).toEqual({ ok: true });
  });
});

describe('rowForm: дубль складеного ключа', () => {
  const existing = [
    row({ Substance: 'Soot', Site: 'S1', Emission: 1 }),
    row({ Substance: 'Soot', Site: 'S2', Emission: 2 }),
  ];

  it('той самий набір ключових значень — дубль із переліком ключових стовпців', () => {
    expect(validateRow(columns, row({ Substance: 'Soot', Site: 'S1', Emission: 9 }), existing)).toEqual({
      ok: false,
      kind: 'duplicate',
      keyColumns: ['Substance', 'Site'],
    });
  });

  it('збіг лише за частиною ключа — не дубль', () => {
    expect(validateRow(columns, row({ Substance: 'Soot', Site: 'S3', Emission: 1 }), existing)).toEqual({ ok: true });
    expect(validateRow(columns, row({ Substance: 'Xylene', Site: 'S1', Emission: 1 }), existing)).toEqual({ ok: true });
  });

  it('пробіли по краях не роблять значення іншим', () => {
    expect(validateRow(columns, row({ Substance: ' Soot ', Site: 'S1 ', Emission: 1 }), existing)).toMatchObject({
      kind: 'duplicate',
    });
  });

  it('редагування збереженого рядка не конфліктує із самим собою, але конфліктує з іншим', () => {
    const [first, second] = existing as [RowValues, RowValues];
    expect(validateRow(columns, { ...first, Emission: 5 }, existing, first)).toEqual({ ok: true });
    // Переносимо перший рядок на ключ другого — це вже дубль.
    expect(validateRow(columns, { ...first, Site: 'S2' }, existing, first)).toMatchObject({ kind: 'duplicate' });
    expect(second).toBeDefined();
  });

  it('обовʼязкові перевіряються ПЕРШИМИ: з порожнім ключем про дубль не йдеться', () => {
    expect(validateRow(columns, row({ Substance: 'Soot', Site: '', Emission: 1 }), existing)).toMatchObject({
      kind: 'required',
    });
  });

  it('таблиця без ключових стовпців дублів не знає', () => {
    const noKey: PermitColumn[] = [{ code: 'a', label: 'a', type: 'text', required: true }];
    expect(validateRow(noKey, row({ a: 'x' }), [row({ a: 'x' })])).toEqual({ ok: true });
  });

  it('числовий ключ 0 не зливається з порожнім (у мокапі `0 || ""` давало дубль)', () => {
    const cols: PermitColumn[] = [{ code: 'n', label: 'n', type: 'number', key: true }];
    expect(validateRow(cols, row({ n: 0 }), [row({ n: null })])).toEqual({ ok: true });
    expect(validateRow(cols, row({ n: 0 }), [row({ n: 0 })])).toMatchObject({ kind: 'duplicate' });
    expect(keyPart(0)).toBe('0');
    expect(keyPart(false)).toBe('false');
    expect(keyPart(null)).toBe('');
    expect(keyPart('  ')).toBe('');
  });

  it('число та рядок із тим самим записом — один ключ', () => {
    const cols: PermitColumn[] = [{ code: 'n', label: 'n', type: 'number', key: true }];
    expect(validateRow(cols, row({ n: 7 }), [row({ n: '7' })])).toMatchObject({ kind: 'duplicate' });
  });
});
