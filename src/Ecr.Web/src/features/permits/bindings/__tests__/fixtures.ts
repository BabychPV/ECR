import type { TemplateTable } from '@/features/templates/ctor/ctorModel';
import type { PermitBinding, PermitBindingPreview } from '../../api/bindingsTypes';

/** Заготовки для тестів прив'язок (PM-11). */

export const k = (key: string): string => `⟦permits.sheetBinding.${key}⟧`;
export const kp = (key: string, params: Record<string, string | number>): string =>
  `⟦permits.sheetBinding.${key} (${Object.entries(params)
    .map(([name, value]) => `${name}=${String(value)}`)
    .join(', ')})⟧`;

export function binding(over: Partial<PermitBinding> = {}): PermitBinding {
  return {
    id: 5,
    code: 'PB41_Emissions',
    sheetTableDefId: 41,
    sheetTableCode: 'Emissions',
    permitTypeCode: 'AirPermit',
    permitTableCode: 'AirEmissions',
    mode: 'Rows',
    selectionRule: 'ByFacilityPeriodEnd',
    headerFieldDefId: null,
    headerFieldCode: null,
    isActive: true,
    rowVersion: 'AAAA',
    columns: [
      { ordinal: 1, permitFieldCode: 'Source', role: 'RowKey', sheetColumnCode: 'Source', sheetColumnDefId: 1 },
      { ordinal: 2, permitFieldCode: 'Permitted_t_year', role: 'Limit', sheetColumnCode: 'Limit', sheetColumnDefId: 3 },
    ],
    ...over,
  };
}

export function sheetTable(over: Partial<TemplateTable> = {}): TemplateTable {
  const column = (id: number, code: string, isReadOnly: boolean) => ({
    id,
    code,
    dataType: 'String',
    displayFormat: null,
    formulaDialect: null,
    formulaExpression: null,
    headerL10n: { values: { en: code } },
    isHidden: false,
    isReadOnly,
    isRequired: false,
    ordinal: id,
    unitSymbol: null,
  });

  return {
    id: 41,
    code: 'Emissions',
    nameL10n: { values: { en: 'Emission sources' } },
    ordinal: 1,
    rowMode: 'Dynamic',
    maxDynamicRows: 200,
    rows: [],
    columns: [column(1, 'Source', true), column(2, 'Substance', true), column(3, 'Limit', true), column(4, 'Actual', false)],
    ...over,
  } as unknown as TemplateTable;
}

export function preview(over: Partial<PermitBindingPreview> = {}): PermitBindingPreview {
  return {
    binding: binding(),
    columns: [
      { columnCode: 'Source', header: 'Source', dataType: 'String', locked: true, role: 'RowKey', unit: null },
      { columnCode: 'Limit', header: 'Limit', dataType: 'Decimal', locked: true, role: 'Limit', unit: 't/yr' },
      { columnCode: 'Actual', header: 'Actual', dataType: 'Decimal', locked: false, role: 'Fact', unit: 't/yr' },
    ],
    permit: { entryId: 9, number: 'KZ-001', reasonIfNone: null, validFrom: '2026-01-01', validTo: '2027-01-01' },
    rows: [
      { rowKey: 'P1', cells: { Source: 'Boiler', Limit: 12.5, Actual: null }, locked: ['Source', 'Limit'] },
      { rowKey: 'P2', cells: { Source: 'Flare', Limit: 3, Actual: null }, locked: ['Source', 'Limit'] },
    ],
    warnings: [],
    ...over,
  };
}
