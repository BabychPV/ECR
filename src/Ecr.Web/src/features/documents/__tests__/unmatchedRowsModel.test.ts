import { describe, expect, it } from 'vitest';
import { issue, table } from '@/features/calculationIssues/__tests__/fixtures';
import { groupUnmatchedRows, unmatchedRowCount } from '@/features/documents/unmatchedRowsModel';

/**
 * UAT 08.11 b1: банер «рядки без правила» групує за таблицею й аркушем, без повторів «1 (S01), 2 (S02), 1 (S01)».
 *
 * Мутація (перевірено вручну, див. звіт lane): ключ групи за `rowKey` замість таблиці — червоніють 4 з 5
 * (зокрема «однакові ключі різних таблиць»).
 */
describe('groupUnmatchedRows', () => {
  it('однакові ключі рядків різних таблиць - дві групи з назвами таблиць, а не повтор у списку', () => {
    const tables = [table(1, '9.01 Unit 400'), table(2, '9.03 Unit 430')];
    const issues = [
      issue(1, 1, { rowKey: 'S01' }),
      issue(1, 2, { rowKey: 'S02' }),
      issue(2, 1, { rowKey: 'S01' }),
      issue(2, 2, { rowKey: 'S02' }),
    ];

    const groups = groupUnmatchedRows(issues, tables);

    expect(groups.map((g) => [g.sheetName, g.tableName, g.rows.map((r) => r.rowKey)])).toEqual([
      ['Sheet', '9.01 Unit 400', ['S01', 'S02']],
      ['Sheet', '9.03 Unit 430', ['S01', 'S02']],
    ]);
    expect(groups.map((g) => g.tableInstanceId)).toEqual([101, 102]);
    expect(unmatchedRowCount(groups)).toBe(4);
  });

  it('порядок документа (аркуш, таблиця), а не порядок відповіді; рядки - за номером', () => {
    const tables = [table(1, 'First'), table(2, 'Second')];
    const issues = [issue(2, 3), issue(2, 1), issue(1, 2)];

    const groups = groupUnmatchedRows(issues, tables);

    expect(groups.map((g) => g.tableName)).toEqual(['First', 'Second']);
    expect(groups[1]!.rows.map((r) => r.rowNumber)).toEqual([1, 3]);
  });

  it('повтор рядка у відповіді не дублюється; методологія - перша відома', () => {
    const tables = [table(1, 'Only')];
    const issues = [issue(1, 1, { methodologyCode: null }), issue(1, 1, { methodologyCode: 'M9' })];

    const groups = groupUnmatchedRows(issues, tables);

    expect(groups).toHaveLength(1);
    expect(groups[0]!.rows).toHaveLength(1);
    expect(groups[0]!.methodologyCode).toBe('M9');
  });

  it('таблиця поза переліком документа - код таблиці, без переходу, і в кінці', () => {
    const groups = groupUnmatchedRows([issue(7, 1), issue(1, 1)], [table(1, 'Known')]);

    expect(groups.map((g) => [g.tableName, g.tableInstanceId, g.sheetName])).toEqual([
      ['Known', 101, 'Sheet'],
      ['T7', null, ''],
    ]);
  });

  it('b3: підказка однієї колонки - самі значення; кількох - «Колонка = значення», без обмеженої колонки', () => {
    const column = (code: string, en: string) => ({ code, nameL10n: { values: { en } } });
    const groups = groupUnmatchedRows(
      [issue(1, 1, { currentValues: ['X'] }), issue(2, 1, { currentValues: ['A', ''] })],
      [table(1, 'One'), table(2, 'Two')],
      [
        { tableCode: 'T1', sheetCopyId: null, methodologyCodes: ['M1'], columns: [column('C', 'Code')], options: [['0903'], ['0905']] },
        {
          tableCode: 'T2',
          sheetCopyId: null,
          methodologyCodes: ['M2'],
          columns: [column('K', 'Kind'), column('F', 'Fuel')],
          options: [['Gas', ''], ['Coal', 'Dry']],
        },
      ],
    );

    expect(groups[0]!.hint).toEqual({ columns: ['Code'], options: ['0903', '0905'], methodologyCodes: ['M1'] });
    expect(groups[0]!.rows[0]!.currentValues).toEqual(['X']);
    expect(groups[1]!.hint!.options).toEqual(['Kind = Gas', 'Kind = Coal · Fuel = Dry']);
  });

  it('b3: таблиця без підказки - поточні значення не показуються (немає з чим порівняти)', () => {
    const groups = groupUnmatchedRows([issue(1, 1, { currentValues: ['X'] })], [table(1, 'One')], []);

    expect(groups[0]!.hint).toBeNull();
    expect(groups[0]!.rows[0]!.currentValues).toBeNull();
  });

  it('копія аркуша - окрема група з екземпляром копії', () => {
    const tables = [table(1, 'Contract'), { ...table(1, 'Contract', 9), tableInstanceId: 900, copyOrdinal: 2 }];
    const groups = groupUnmatchedRows([issue(1, 1), issue(1, 1, { sheetCopyId: 9 })], tables);

    expect(groups.map((g) => g.tableInstanceId)).toEqual([101, 900]);
  });
});
