import { describe, expect, it } from 'vitest';
import {
  DraftModelError,
  ListScopes,
  addColumn,
  addTable,
  appendColumns,
  countChanges,
  draftOf,
  findColumn,
  findTable,
  freeCode,
  isValidCode,
  keyColumns,
  moveColumn,
  newColumn,
  normalizeColumn,
  referencesTo,
  removeColumn,
  removeTable,
  renameTable,
  sameDraft,
  scopeKeyOf,
  updateColumn,
  withListScope,
} from '../draftModel';
import { column, draftFixture, permitType, table } from './fixtures';

/**
 * Робоча копія типу (PM-08b).
 *
 * Мутаційний доказ (виконано вручну, відновлено): у `normalizeColumn` `required: column.key ? true : …` →
 * `required: column.required` валить 5 тестів (інваріант ключа); у `appendColumns` `if (taken.has(…))` →
 * `if (false)` валить 2 («повтор коду», «усе або нічого»); у `normalizeColumn` `choices` без прив'язки до
 * області валить «перехід між областями».
 */

describe('кодові правила', () => {
  it.each(['A', 'Water_body', 'x9', 'a'.repeat(64)])('«%s» допустимий', (code) => {
    expect(isValidCode(code)).toBe(true);
  });
  it.each(['', '9a', '_a', 'a-b', 'a b', 'a'.repeat(65), 'Назва'])('«%s» недопустимий', (code) => {
    expect(isValidCode(code)).toBe(false);
  });

  it('freeCode: очищає символи, не починає з цифри й не повторює зайняте', () => {
    expect(freeCode('Monitoring points', [])).toBe('Monitoring_points');
    expect(freeCode('9 lines', [])).toBe('C_9_lines');
    expect(freeCode('Monitoring points', ['monitoring_POINTS'])).toBe('Monitoring_points_2');
    expect(freeCode('Назва', [])).toBe('Item');
    expect(freeCode('Назва', ['item', 'Item_2'])).toBe('Item_3');
  });
});

describe('інваріант: ключовий стовпець завжди обовʼязковий', () => {
  it('normalizeColumn піднімає required для ключа', () => {
    expect(normalizeColumn(column('A', { key: true, required: false })).required).toBe(true);
  });

  it('updateColumn: позначили ключем — стало обовʼязковим', () => {
    const next = updateColumn(draftFixture(), 'Discharge', 'Annual', { key: true });
    expect(findColumn(findTable(next, 'Discharge')!, 'Annual')).toMatchObject({ key: true, required: true });
  });

  it('addColumn і appendColumns тримають інваріант', () => {
    const one = addColumn(draftFixture(), 'Storage', column('K', { key: true, required: false }));
    expect(findColumn(findTable(one, 'Storage')!, 'K')!.required).toBe(true);
    const many = appendColumns(draftFixture(), 'Storage', [column('K1', { key: true }), column('K2', { key: true })]);
    expect(keyColumns(findTable(many, 'Storage')!).every((c) => c.required)).toBe(true);
  });

  it('addTable тримає інваріант для стовпців майстра', () => {
    const next = addTable(draftFixture(), { code: 'Points', nameL10n: { en: 'P' }, columns: [column('Name', { key: true })] });
    expect(findColumn(findTable(next, 'Points')!, 'Name')!.required).toBe(true);
  });

  it('draftOf нормалізує те, що прийшло з сервера', () => {
    const type = permitType();
    type.tables[0]!.columns[2] = { ...type.tables[0]!.columns[2]!, key: true, required: false };
    expect(draftOf(type).tables[0]!.columns[2]!.required).toBe(true);
  });

  it('знімання ключа не знімає обовʼязковість (її вирішує людина)', () => {
    const next = updateColumn(draftFixture(), 'Storage', 'Place', { key: false });
    expect(findColumn(findTable(next, 'Storage')!, 'Place')!.required).toBe(true);
  });
});

describe('таблиці', () => {
  it('addTable додає в кінець з order і стовпцями від 1', () => {
    const next = addTable(draftFixture(), {
      code: 'Points',
      nameL10n: { en: 'Points' },
      columns: [column('Name', { key: true }), column('Lat')],
    });
    expect(next.tables.map((t) => [t.code, t.order])).toEqual([['Discharge', 1], ['Storage', 2], ['Points', 3]]);
    expect(findTable(next, 'Points')!.columns.map((c) => c.order)).toEqual([1, 2]);
    expect(findTable(next, 'Points')!.rules).toEqual([]);
  });

  it('addTable: код зайнятий (без огляду на регістр) чи недопустимий — DraftModelError', () => {
    expect(() => addTable(draftFixture(), { code: 'discharge', nameL10n: {}, columns: [] })).toThrow(
      expect.objectContaining({ reason: 'tableExists' }),
    );
    expect(() => addTable(draftFixture(), { code: '1x', nameL10n: {}, columns: [] })).toThrow(DraftModelError);
  });

  it('removeTable прибирає таблицю й переномерує; невідомий код — помилка', () => {
    const next = removeTable(draftFixture(), 'Discharge');
    expect(next.tables.map((t) => [t.code, t.order])).toEqual([['Storage', 1]]);
    expect(() => removeTable(draftFixture(), 'Nope')).toThrow(expect.objectContaining({ reason: 'tableMissing' }));
  });

  it('renameTable міняє назву, не код', () => {
    const next = renameTable(draftFixture(), 'storage', { en: 'Places' });
    expect(findTable(next, 'Storage')).toMatchObject({ code: 'Storage', nameL10n: { en: 'Places' } });
  });

  it('referencesTo знаходить стовпці, що беруть значення з таблиці', () => {
    const draft = addColumn(
      draftFixture(),
      'Discharge',
      withListScope(newColumn('Where', { en: 'Where' }, 'Lookup'), 'permit', ['Storage']),
    );
    expect(referencesTo(draft, 'storage')).toEqual([{ tableCode: 'Discharge', columnCode: 'Where' }]);
    expect(referencesTo(draft, 'Discharge')).toEqual([]);
  });
});

describe('стовпці: унікальність коду і порядок', () => {
  it('повтор коду без огляду на регістр — помилка, чернетка не змінюється', () => {
    const draft = draftFixture();
    expect(() => addColumn(draft, 'Discharge', column('water_BODY'))).toThrow(expect.objectContaining({ reason: 'columnExists' }));
    expect(findTable(draft, 'Discharge')!.columns).toHaveLength(3);
  });

  it('appendColumns усе або нічого: повтор усередині пачки не залишає половини', () => {
    const draft = draftFixture();
    expect(() => appendColumns(draft, 'Storage', [column('A'), column('a')])).toThrow(DraftModelError);
    expect(findTable(draft, 'Storage')!.columns).toHaveLength(1);
  });

  it('appendColumns додає пачку в кінець з наступними order', () => {
    const next = appendColumns(draftFixture(), 'Storage', [column('Jan'), column('Feb')]);
    expect(findTable(next, 'Storage')!.columns.map((c) => [c.code, c.order])).toEqual([['Place', 1], ['Jan', 2], ['Feb', 3]]);
  });

  it('недопустимий код стовпця і зарезервований PERMIT не проходять далі валідатора, але модель кидає лише на формат', () => {
    expect(() => addColumn(draftFixture(), 'Storage', column('a-b'))).toThrow(expect.objectContaining({ reason: 'codeInvalid' }));
  });

  it('removeColumn переномерує; невідомий — помилка', () => {
    const next = removeColumn(draftFixture(), 'Discharge', 'Sub_period');
    expect(findTable(next, 'Discharge')!.columns.map((c) => [c.code, c.order])).toEqual([['Water_body', 1], ['Annual', 2]]);
    expect(() => removeColumn(draftFixture(), 'Discharge', 'Nope')).toThrow(expect.objectContaining({ reason: 'columnMissing' }));
  });

  it('moveColumn: на початок, у кінець, за межі — обрізається', () => {
    const codes = (d: ReturnType<typeof draftFixture>): string[] => findTable(d, 'Discharge')!.columns.map((c) => c.code);
    expect(codes(moveColumn(draftFixture(), 'Discharge', 'Annual', 0))).toEqual(['Annual', 'Water_body', 'Sub_period']);
    expect(codes(moveColumn(draftFixture(), 'Discharge', 'Water_body', 99))).toEqual(['Sub_period', 'Annual', 'Water_body']);
    expect(findTable(moveColumn(draftFixture(), 'Discharge', 'Annual', 0), 'Discharge')!.columns.map((c) => c.order)).toEqual([1, 2, 3]);
  });

  it('updateColumn не міняє код і order', () => {
    const next = updateColumn(draftFixture(), 'Discharge', 'Annual', { nameL10n: { en: 'Yearly' }, unitId: 9 });
    expect(findColumn(findTable(next, 'Discharge')!, 'Annual')).toMatchObject({ code: 'Annual', order: 3, unitId: 9, nameL10n: { en: 'Yearly' } });
  });
});

describe('нормалізація типу й області списку', () => {
  it('не Decimal губить точність і масштаб; Decimal без точності губить масштаб', () => {
    expect(normalizeColumn(column('A', { dataType: 'Int', precision: 5, scale: 2 }))).toMatchObject({ precision: null, scale: null });
    expect(normalizeColumn(column('A', { dataType: 'Decimal', precision: null, scale: 2 })).scale).toBeNull();
    expect(normalizeColumn(column('A', { dataType: 'Decimal', precision: 9, scale: 2 }))).toMatchObject({ precision: 9, scale: 2 });
  });

  it('одиниця лишається лише в Int і Decimal', () => {
    expect(normalizeColumn(column('A', { dataType: 'Int', unitId: 3 })).unitId).toBe(3);
    expect(normalizeColumn(column('A', { dataType: 'Decimal', unitId: 3 })).unitId).toBe(3);
    expect(normalizeColumn(column('A', { dataType: 'String', unitId: 3 })).unitId).toBeNull();
    expect(normalizeColumn(column('A', { dataType: 'Date', unitId: 3 })).unitId).toBeNull();
  });

  it('не Lookup губить усі налаштування списку', () => {
    const next = normalizeColumn(column('A', { dataType: 'String', listScope: ListScopes.type, choices: ['x'], listRegistryCode: 'R', siblingTableCode: 'T' }));
    expect(next).toMatchObject({ listScope: null, choices: null, listRegistryCode: null, siblingTableCode: null });
  });

  it('перехід між областями лишає лише поля нової', () => {
    const base = newColumn('Pick', { en: 'Pick' }, 'Lookup');
    const shared = { ...withListScope(base, 'shared'), listRegistryCode: 'Substances' };
    const toType = withListScope(shared, 'type');
    expect(toType).toMatchObject({ listScope: 'TypeOnly', listRegistryCode: null, siblingTableCode: null, choices: [] });
    const withChoices = { ...toType, choices: ['a', 'b'] };
    const toPermit = withListScope(withChoices, 'permit', ['Storage']);
    expect(toPermit).toMatchObject({ listScope: 'SamePermitTable', siblingTableCode: 'Storage', choices: null, listRegistryCode: null });
    const back = withListScope(toPermit, 'shared');
    expect(back).toMatchObject({ listScope: 'SharedRegistry', siblingTableCode: null, choices: null });
  });

  it('scopeKeyOf: лише для Lookup', () => {
    expect(scopeKeyOf(column('A'))).toBeNull();
    expect(scopeKeyOf(withListScope(newColumn('A', {}, 'Lookup'), 'permit'))).toBe('permit');
  });
});

describe('порівняння з збереженим', () => {
  it('sameDraft не зважає на порядок ключів обʼєктів', () => {
    const a = draftFixture();
    const b = { ...a, nameL10n: { ru: 'Сброс воды', en: 'Water discharge' } };
    expect(sameDraft(a, b)).toBe(true);
    expect(sameDraft(a, { ...a, category: 'Air' })).toBe(false);
  });

  it('countChanges: нуль для однакових', () => {
    expect(countChanges(draftFixture(), draftFixture())).toBe(0);
  });

  it('countChanges рахує по одній правці: стовпець, таблиця, назва, порядок', () => {
    const saved = draftFixture();
    expect(countChanges(saved, addColumn(saved, 'Storage', column('New')))).toBe(1);
    expect(countChanges(saved, removeColumn(saved, 'Discharge', 'Annual'))).toBe(1);
    expect(countChanges(saved, updateColumn(saved, 'Discharge', 'Annual', { unitId: 1 }))).toBe(1);
    expect(countChanges(saved, removeTable(saved, 'Storage'))).toBe(1);
    expect(countChanges(saved, { ...saved, nameL10n: { en: 'X' } })).toBe(1);
    expect(countChanges(saved, moveColumn(saved, 'Discharge', 'Annual', 0))).toBe(1);
    const two = updateColumn(addColumn(saved, 'Storage', column('New')), 'Discharge', 'Annual', { unitId: 1 });
    expect(countChanges(saved, two)).toBe(2);
  });

  it('видалення стовпця не рахується ще й як зміна порядку', () => {
    const saved = draftFixture();
    expect(countChanges(saved, removeColumn(saved, 'Discharge', 'Water_body'))).toBe(1);
  });

  it('table() — стабільний помічник', () => {
    expect(table('X', []).columns).toEqual([]);
  });
});
