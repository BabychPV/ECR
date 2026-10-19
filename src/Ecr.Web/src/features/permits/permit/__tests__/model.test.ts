import { describe, expect, it } from 'vitest';
import {
  deleteRowItem,
  fieldProblem,
  fieldsOf,
  initialForm,
  loadRow,
  newRowItem,
  pickerOptions,
  toRowValues,
  totalSums,
  updateRowItem,
  wireValue,
  type PickerData,
} from '../model';
import { permitType, registryRow } from './permitFixtures';

/**
 * Модель екрана дозволу: переклад стовпців типу в стовпці таблиці, рядків сервера в рядки таблиці й значень форми в
 * пакет `entries/batch`. Усе тут — чисті функції; доказ «падає, коли зламати» — у мутаційному записі наприкінці
 * файлу `PermitPage.test.tsx`.
 */
const units = new Map([[7, 't/yr']]);
const limits = fieldsOf(permitType().tables[0]!, units);

describe('fieldsOf', () => {
  it('типи сервера стають типами таблиці, область списку — через ref/choices', () => {
    expect(limits.map((field) => [field.column.code, field.column.type])).toEqual([
      ['Name', 'text'],
      ['Annual', 'number'],
      ['Count', 'number'],
      ['Active', 'bool'],
      ['Issued', 'date'],
      ['Place', 'link'],
      ['Use', 'choice'],
      ['Substance', 'link'],
    ]);
    const byCode = Object.fromEntries(limits.map((field) => [field.column.code, field.column]));
    expect(byCode['Place']?.ref).toBe('Places (this permit)');
    expect(byCode['Substance']?.ref).toBe('Substances');
    expect(byCode['Use']?.choices).toEqual(['Cooling', 'Process']);
    expect(byCode['Annual']?.unit).toBe('t/yr');
    expect(byCode['Name']).toMatchObject({ key: true, required: true });
  });

  it('стовпці йдуть за порядком `order`, а не за порядком у відповіді', () => {
    const type = permitType();
    const shuffled = { ...type.tables[0]!, columns: [...type.tables[0]!.columns].reverse() };
    expect(fieldsOf(shuffled, units)[0]?.column.code).toBe('Name');
  });
});

describe('loadRow / initialForm', () => {
  const server = registryRow(11, {
    Name: 'Outfall A',
    Annual: '10.5',
    Active: 'true',
    Issued: '2026-03-01T00:00:00',
    Place: ['31', 'Pond 1'],
  });

  it('значення типізуються, посилання показують підпис цілі, але зберігають id', () => {
    const loaded = loadRow(limits, server);
    expect(loaded.row).toMatchObject({ id: '11', Name: 'Outfall A', Annual: 10.5, Active: true, Issued: '2026-03-01', Place: 'Pond 1' });
    expect(loaded.lookupIds).toEqual({ Place: '31' });
    expect(loaded.version).toBe('v11');
    expect(loaded.row['Count']).toBeNull();
  });

  it('форма: логічне завжди true/false, дата без часу, посилання — id', () => {
    const form = initialForm(limits, loadRow(limits, server));
    expect(form).toMatchObject({ Name: 'Outfall A', Annual: '10.5', Active: 'true', Issued: '2026-03-01', Place: '31', Count: '' });
    expect(initialForm(limits)['Active']).toBe('false');
  });
});

describe('fieldProblem / wireValue', () => {
  const field = (code: string) => limits.find((entry) => entry.column.code === code)!;

  it('число: кома, пробіли, нерозпізнане; Int не приймає дріб', () => {
    expect(fieldProblem(field('Annual'), '1 234,5')).toBeNull();
    expect(fieldProblem(field('Annual'), '12abc')).toBe('notNumber');
    expect(fieldProblem(field('Count'), '2,5')).toBe('notInteger');
    expect(fieldProblem(field('Count'), '3')).toBeNull();
    expect(fieldProblem(field('Annual'), '  ')).toBeNull();
  });

  it('дата: ISO чи Д.М.Р; неіснуюча — помилка', () => {
    expect(fieldProblem(field('Issued'), '2026-02-30')).toBe('notDate');
    expect(fieldProblem(field('Issued'), '05.03.2026')).toBeNull();
    expect(wireValue(field('Issued'), '05.03.2026')).toBe('2026-03-05');
  });

  it('у пакет число іде інваріантно, а не з комою', () => {
    expect(wireValue(field('Annual'), '12,5')).toBe('12.5');
    expect(wireValue(field('Name'), '  Outfall  ')).toBe('Outfall');
  });
});

describe('пакети рядка', () => {
  const field = (code: string) => limits.find((entry) => entry.column.code === code)!;

  it('новий рядок: поле композиції PERMIT = id дозволу, порожні поля не йдуть, код присвоює сервер', () => {
    const form = { ...initialForm(limits), Name: 'New', Annual: '5,5', Place: '31' };
    const item = newRowItem('c1', 5, limits, form);
    expect(item).toEqual({
      clientRowId: 'c1',
      op: 'upsert',
      id: null,
      code: null,
      baseVersion: null,
      values: { PERMIT: '5', Name: 'New', Annual: '5.5', Active: 'false', Place: '31' },
    });
  });

  it('наявний рядок: лише змінені поля, очищене — null, токен конкуренції іде як baseVersion', () => {
    const loaded = loadRow(limits, registryRow(11, { Name: 'A', Annual: '10', Place: ['31', 'Pond 1'] }));
    const original = initialForm(limits, loaded);
    const item = updateRowItem('c2', loaded, limits, { ...original, Annual: '11', Place: '' }, original);
    expect(item).toEqual({
      clientRowId: 'c2',
      op: 'upsert',
      id: 11,
      code: null,
      baseVersion: 'v11',
      values: { Annual: '11', Place: null },
    });
  });

  it('нічого не змінено — пакета немає', () => {
    const loaded = loadRow(limits, registryRow(11, { Name: 'A', Annual: '10' }));
    const original = initialForm(limits, loaded);
    expect(updateRowItem('c3', loaded, limits, { ...original, Annual: '10.0' }, original)).toBeNull();
    expect(field('Annual')).toBeDefined();
  });

  it('видалення несе id і токен', () => {
    const loaded = loadRow(limits, registryRow(12, { Name: 'B' }));
    expect(deleteRowItem('d', loaded)).toEqual({ clientRowId: 'd', op: 'delete', id: 12, code: null, baseVersion: 'v12', values: null });
  });
});

describe('пікери', () => {
  const data: PickerData = {
    sources: {
      shared: { Substances: ['Methane', 'Benzene', 'Methane'] },
      permitTables: { Places: ['Pond 1'] },
    },
    ids: {
      Substances: new Map([['Methane', '41'], ['Benzene', '42']]),
      'permit:Places': new Map([['Pond 1', '31']]),
      PRM_WD_Use: new Map([['Cooling', '21']]),
    },
  };
  const options = (code: string) => pickerOptions(limits.find((entry) => entry.column.code === code)!, data);

  it('спільний — дублі прибрано; таблиця цього дозволу — її рядки; тип — лише ті підписи, що мають id', () => {
    expect(options('Substance')).toEqual([{ value: '41', label: 'Methane' }, { value: '42', label: 'Benzene' }]);
    expect(options('Place')).toEqual([{ value: '31', label: 'Pond 1' }]);
    // «Process» немає серед записів реєстру списку, тож записати його неможливо — варіанта немає.
    expect(options('Use')).toEqual([{ value: '21', label: 'Cooling' }]);
  });

  it('toRowValues підставляє підпис за id, щоб дубль ключа рахувався за тим, що бачить людина', () => {
    const values = toRowValues(limits, { ...initialForm(limits), Name: 'X', Place: '31', Annual: '1,5' }, (column, id) =>
      pickerOptions(limits.find((entry) => entry.column.code === column.code)!, data).find((o) => o.value === id)?.label ?? null,
    );
    expect(values).toMatchObject({ Name: 'X', Place: 'Pond 1', Annual: 1.5, Active: false });
  });
});

describe('totalSums', () => {
  it('підсумок головного стовпця; без стовпця чи значення — порожньо', () => {
    expect(totalSums('Annual', '30.25')).toEqual({ Annual: 30.25 });
    expect(totalSums(null, '30')).toEqual({});
    expect(totalSums('Annual', null)).toEqual({});
  });
});
