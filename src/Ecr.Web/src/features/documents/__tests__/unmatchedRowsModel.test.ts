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
        { tableCode: 'T1', sheetCopyId: null, methodologies: [{ code: 'M1', nameL10n: null }], columns: [column('C', 'Code')], options: [['0903'], ['0905']] },
        {
          tableCode: 'T2',
          sheetCopyId: null,
          methodologies: [{ code: 'M2', nameL10n: { values: { en: 'Fuel combustion' } } }],
          columns: [column('K', 'Kind'), column('F', 'Fuel')],
          options: [['Gas', ''], ['Coal', 'Dry']],
        },
      ],
    );

    expect(groups[0]!.hint).toEqual({ columns: ['Code'], options: ['0903', '0905'], methodologies: ['M1'] });
    expect(groups[0]!.rows[0]!.currentValues).toEqual(['X']);
    // fg6 F13 P3: назви колонок — один раз («Kind, Fuel»), варіант — значення через « / », необмежена — «any».
    expect(groups[1]!.hint!.columns).toEqual(['Kind', 'Fuel']);
    expect(groups[1]!.hint!.options).toEqual(['Gas / ⟦document.unmatchedRows.anyValue⟧', 'Coal / Dry']);
    // Людині — назва методології; код — лише коли назви немає (M1).
    expect(groups[1]!.hint!.methodologies).toEqual(['Fuel combustion']);
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

  // B66: помилка кроку методики — своя група з кроком і кодом на рядок; рядки, відсіяні фільтром активності (D-425), —
  // ОДИН підсумок таблиці без поштучних рядків, кількість — із сервера. Мутації: не розпізнавати `RowInactive` (рядок іде
  // «без правила») або рахувати `rows.length` замість `inactiveCount` у `unmatchedRowCount` — тест червоний.
  it('B66: StepError — група з кроком і кодом; RowInactive — підсумок таблиці з кількістю', () => {
    const tables = [table(1, 'Units')];
    const issues = [
      issue(1, 4, { rowKey: 'R4', kind: 'StepError', stepCode: 'Total', errorCode: '#VALUE', affectedCount: 3 }),
      issue(1, 2, { rowKey: 'R2', kind: 'RowInactive', affectedCount: 1500 }),
      issue(1, 7, { rowKey: 'R7' }),
    ];

    const groups = groupUnmatchedRows(issues, tables);

    const step = groups.find((g) => g.stepError)!;
    expect(step.rows.map((r) => [r.rowKey, r.stepError])).toEqual([
      ['R4', { step: 'Total', error: '#VALUE', failedSteps: 3, trace: null, methodologyId: null, monthNumber: null }],
    ]);
    expect(step.traceOff).toEqual([]);
    expect(step.hint).toBeNull();
    const inactive = groups.find((g) => g.inactive)!;
    expect(inactive.rows).toEqual([]);
    expect(inactive.inactiveCount).toBe(1500);
    expect(groups.find((g) => !g.stepError && !g.inactive)!.rows.map((r) => r.rowKey)).toEqual(['R7']);
    expect(unmatchedRowCount(groups)).toBe(1 + 1500 + 1);
  });

  // B66 (F13 C6): документ річної розкладки віддає по підсумку RowInactive на кожен місяць тієї самої таблиці (рядки
  // річного аркуша — ті самі). Підсумок групи — найбільша місячна кількість (скільки рядків таблиці відсіяно в найгіршому
  // місяці), а не сума: сума рахувала б той самий рядок по разу на місяць. Мутація: сумувати `affectedCount` — тест червоний.
  it('B66: рік — підсумки RowInactive місяців однієї таблиці дають найбільшу кількість, а не суму', () => {
    const tables = [table(1, 'Units')];
    const issues = [
      issue(1, 2, { rowKey: 'R2', kind: 'RowInactive', affectedCount: 5, monthNumber: 1 }),
      issue(1, 2, { rowKey: 'R2', kind: 'RowInactive', affectedCount: 7, monthNumber: 2 }),
      issue(1, 3, { rowKey: 'R3', kind: 'RowInactive', affectedCount: 6, monthNumber: 3 }),
    ];

    const groups = groupUnmatchedRows(issues, tables);

    expect(groups).toHaveLength(1);
    expect(groups[0]!.inactiveCount).toBe(7);
    expect(unmatchedRowCount(groups)).toBe(7);
  });
});
