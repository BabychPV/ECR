import { describe, expect, it } from 'vitest';
import type { PermitBinding } from '../../api/bindingsTypes';
import type { PermitTypeTable } from '../../api/types';
import { column, table } from '../../typeEditor/dialogs/__tests__/fixtures';
import {
  bindingCodeFor,
  completeMapping,
  draftOfBinding,
  draftProblem,
  emptyDraft,
  mappingProblem,
  reasonKind,
  refreshSummary,
  suggestMapping,
  toRequest,
  warningKind,
  type BindingDraft,
  type SheetTableRef,
} from '../bindingModel';

/** Модель прив'язки (PM-11): розкладка, перевірки, тіло запиту, ключі причин. */

const permitTable: PermitTypeTable = table({
  code: 'AirEmissions',
  columns: [
    column({ code: 'Source', nameL10n: { en: 'Source' }, key: true, order: 1 }),
    column({ code: 'Substance', nameL10n: { en: 'Substance' }, key: true, order: 2 }),
    column({ code: 'Permitted_t_year', nameL10n: { en: 'Limit' }, dataType: 'Decimal', key: false, required: false, order: 3 }),
    column({ code: 'Note', nameL10n: { en: 'Note' }, key: false, required: false, order: 4 }),
  ],
});

const sheet: SheetTableRef = {
  id: 41,
  code: 'Emissions',
  rowMode: 'Dynamic',
  columns: [
    { id: 1, code: 'Source', isReadOnly: true, name: 'Source' },
    { id: 2, code: 'Substance', isReadOnly: true, name: 'Substance' },
    { id: 3, code: 'Permitted_t_year', isReadOnly: true, name: 'Limit' },
    { id: 4, code: 'Actual', isReadOnly: false, name: 'Actual' },
  ],
};

const draft = (over: Partial<BindingDraft> = {}): BindingDraft => ({
  ...emptyDraft(),
  permitTypeCode: 'AirPermit',
  permitTableCode: 'AirEmissions',
  mapping: suggestMapping(permitTable, sheet),
  ...over,
});

describe('bindingCodeFor', () => {
  it('код складається з Id таблиці версії й її коду та вміщується в 64 символи', () => {
    expect(bindingCodeFor({ id: 41, code: 'Emissions' })).toBe('PB41_Emissions');
    expect(bindingCodeFor({ id: 41, code: 'X'.repeat(80) })).toHaveLength(64);
  });
});

describe('suggestMapping', () => {
  it('ключі → RowKey, відомий стовпець ліміту → Limit, стовпець без пари за кодом — не використовується', () => {
    const roles = Object.fromEntries(suggestMapping(permitTable, sheet).map((row) => [row.fieldCode, row.role]));
    expect(roles).toEqual({ Source: 'RowKey', Substance: 'RowKey', Permitted_t_year: 'Limit', Note: 'None' });
  });

  it('стовпець аркуша, який користувач може правити, не пропонується (сервер відхилив би bindingNotReadOnly)', () => {
    const editable: SheetTableRef = { ...sheet, columns: sheet.columns.map((c) => (c.code === 'Source' ? { ...c, isReadOnly: false } : c)) };
    const source = suggestMapping(permitTable, editable).find((row) => row.fieldCode === 'Source');
    expect(source).toEqual({ fieldCode: 'Source', role: 'None', sheetColumnId: null });
  });
});

describe('mappingProblem', () => {
  it('розкладка за замовчуванням без вад', () => {
    expect(mappingProblem(draft(), sheet)).toBeNull();
  });

  it('без ключа рядка — noRowKey', () => {
    const rows = draft().mapping.map((row) => (row.role === 'RowKey' ? { ...row, role: 'Info' as const } : row));
    expect(mappingProblem(draft({ mapping: rows }), sheet)).toEqual({ kind: 'noRowKey' });
  });

  it('два ліміти — manyLimits', () => {
    const rows = draft().mapping.map((row) => (row.fieldCode === 'Substance' ? { ...row, role: 'Limit' as const } : row));
    expect(mappingProblem(draft({ mapping: rows }), sheet)).toEqual({ kind: 'manyLimits' });
  });

  it('стовпець не обрано — noColumn з кодом поля', () => {
    const rows = draft().mapping.map((row) => (row.fieldCode === 'Note' ? { ...row, role: 'Info' as const } : row));
    expect(mappingProblem(draft({ mapping: rows }), sheet)).toEqual({ kind: 'noColumn', fieldCode: 'Note' });
  });

  it('один стовпець аркуша двічі — duplicateColumn', () => {
    const rows = draft().mapping.map((row) => (row.fieldCode === 'Substance' ? { ...row, sheetColumnId: 1 } : row));
    expect(mappingProblem(draft({ mapping: rows }), sheet)).toEqual({ kind: 'duplicateColumn', columnCode: 'Source' });
  });

  it('стовпець, який правлять люди, — notReadOnly', () => {
    const rows = draft().mapping.map((row) => (row.fieldCode === 'Note' ? { ...row, role: 'Info' as const, sheetColumnId: 4 } : row));
    expect(mappingProblem(draft({ mapping: rows }), sheet)).toEqual({ kind: 'notReadOnly', columnCode: 'Actual' });
  });

  it('у режимі Formulas зіставлення не перевіряється', () => {
    expect(mappingProblem(draft({ mode: 'Formulas', mapping: [] }), sheet)).toBeNull();
  });
});

describe('draftProblem', () => {
  it('послідовність вимог: тип → таблиця → поле шапки → рядки лише в Dynamic/Mixed', () => {
    expect(draftProblem(emptyDraft(), sheet)).toBe('typeRequired');
    expect(draftProblem({ ...emptyDraft(), permitTypeCode: 'AirPermit' }, sheet)).toBe('tableRequired');
    expect(draftProblem(draft({ selectionRule: 'FromHeaderLookup' }), sheet)).toBe('headerFieldRequired');
    expect(draftProblem(draft({ selectionRule: 'FromHeaderLookup', headerFieldDefId: 9 }), sheet)).toBeNull();
    expect(draftProblem(draft(), { ...sheet, rowMode: 'Fixed' })).toBe('rowsNeedDynamic');
    expect(draftProblem(draft({ mode: 'Formulas' }), { ...sheet, rowMode: 'Fixed' })).toBeNull();
    expect(draftProblem(draft(), { ...sheet, rowMode: 'Mixed' })).toBeNull();
  });
});

describe('toRequest', () => {
  it('режим Rows: стовпці з роллю, невикористані не їдуть, поле шапки не їде для ByFacilityPeriodEnd', () => {
    const request = toRequest(draft({ headerFieldDefId: 9 }), sheet);
    expect(request).toMatchObject({
      permitTypeCode: 'AirPermit',
      permitTableCode: 'AirEmissions',
      sheetTableDefId: 41,
      mode: 'Rows',
      selectionRule: 'ByFacilityPeriodEnd',
      headerFieldDefId: null,
      isActive: true,
    });
    expect(request.columns).toEqual([
      { permitFieldCode: 'Source', role: 'RowKey', sheetColumnDefId: 1 },
      { permitFieldCode: 'Substance', role: 'RowKey', sheetColumnDefId: 2 },
      { permitFieldCode: 'Permitted_t_year', role: 'Limit', sheetColumnDefId: 3 },
    ]);
  });

  it('режим Formulas: стовпців немає; FromHeaderLookup несе поле шапки', () => {
    const request = toRequest(draft({ mode: 'Formulas', selectionRule: 'FromHeaderLookup', headerFieldDefId: 9 }), sheet);
    expect(request.columns).toEqual([]);
    expect(request.headerFieldDefId).toBe(9);
  });
});

describe('draftOfBinding', () => {
  const binding = {
    code: 'PB41_Emissions',
    id: 1,
    isActive: true,
    mode: 'Rows',
    permitTableCode: 'AirEmissions',
    permitTypeCode: 'AirPermit',
    rowVersion: 'x',
    selectionRule: 'FromHeaderLookup',
    headerFieldCode: 'Permit',
    headerFieldDefId: 9,
    sheetTableCode: 'Emissions',
    sheetTableDefId: 41,
    columns: [
      { ordinal: 1, permitFieldCode: 'Source', role: 'RowKey', sheetColumnCode: 'Source', sheetColumnDefId: 1 },
      { ordinal: 2, permitFieldCode: 'Note', role: 'Strange', sheetColumnCode: 'Source', sheetColumnDefId: 2 },
    ],
  } satisfies PermitBinding;

  it('повертає правило, поле шапки й розкладку за таблицею дозволу; невідома роль стає Info', () => {
    const result = draftOfBinding(binding, permitTable);
    expect(result.selectionRule).toBe('FromHeaderLookup');
    expect(result.headerFieldDefId).toBe(9);
    expect(result.mapping.map((row) => [row.fieldCode, row.role, row.sheetColumnId])).toEqual([
      ['Source', 'RowKey', 1],
      ['Substance', 'None', null],
      ['Permitted_t_year', 'None', null],
      ['Note', 'Info', 2],
    ]);
  });
});

describe('випадки причин і попереджень', () => {
  it('кожна причина reasonIfNone розпізнається, невідома й null стають other', () => {
    expect(['noFacility', 'noPermit', 'headerEmpty', 'headerUnknown'].map(reasonKind)).toEqual(['noFacility', 'noPermit', 'headerEmpty', 'headerUnknown']);
    expect(reasonKind('weird')).toBe('other');
    expect(reasonKind(null)).toBe('other');
  });

  it('попередження розпізнається за хвостом коду; невідоме — other', () => {
    expect(warningKind({ code: 'binding.warning.inactive', text: 'x' })).toBe('inactive');
    expect(warningKind({ code: 'notRowsBinding', text: 'x' })).toBe('notRowsBinding');
    expect(warningKind({ code: 'binding.warning.permitNotValidAtPeriodEnd', text: 'x' })).toBe('notValidAtPeriodEnd');
    expect(warningKind({ code: 'noPermit', text: 'x' })).toBe('planFailed');
    expect(warningKind({ code: 'something.new', text: 'x' })).toBe('other');
  });
});

describe('completeMapping', () => {
  it('порожня розкладка — пропозиція; наявна доповнюється невикористаними стовпцями у порядку таблиці', () => {
    expect(completeMapping(permitTable, [], sheet)).toEqual(suggestMapping(permitTable, sheet));

    const current = [{ fieldCode: 'Note', role: 'Info' as const, sheetColumnId: 2 }];
    expect(completeMapping(permitTable, current, sheet).map((row) => [row.fieldCode, row.role])).toEqual([
      ['Source', 'None'],
      ['Substance', 'None'],
      ['Permitted_t_year', 'None'],
      ['Note', 'Info'],
    ]);
  });
});
describe('refreshSummary', () => {
  it('переносить числа, ключі збережених рядків і попередження; unchanged лише коли нічого не сталося', () => {
    const changed = refreshSummary({ added: 2, updated: 1, removedFromPermit: ['P7'], warnings: [{ code: 'a', text: 'b' }] });
    expect(changed).toMatchObject({ added: 2, updated: 1, keptKeys: ['P7'], unchanged: false });
    expect(changed.warnings).toHaveLength(1);
    expect(refreshSummary({ added: 0, updated: 0, removedFromPermit: [], warnings: [] }).unchanged).toBe(true);
    expect(refreshSummary({ added: 0, updated: 0, removedFromPermit: ['P1'], warnings: [] }).unchanged).toBe(false);
  });
});
