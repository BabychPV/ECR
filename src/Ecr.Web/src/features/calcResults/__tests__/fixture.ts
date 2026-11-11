import type { CalculationYearView } from '../api';

const L = (en: string) => ({ values: { en } });
const none = (n: number): (string | null)[] => Array.from({ length: n }, () => null);

/**
 * Огляд року для тестів: два аркуші, методологія з адитивним (t) і неадитивним (kg/s) виходами, формула шаблону,
 * лютий застарілий, березень без прогону, решта не відкрита.
 */
export function yearViewFixture(): CalculationYearView {
  const months = Array.from({ length: 12 }, (_, i) => ({
    month: i + 1,
    periodKey: 202601 + i,
    periodState: i < 3 ? 'open' : 'notOpened',
    runState: i === 0 ? 'current' : i === 1 ? 'outOfDate' : i === 2 ? 'noRun' : 'notOpened',
    calculatedAt: i < 2 ? '2026-02-14T09:12:00Z' : null,
  }));
  return {
    documentId: 7,
    year: 2026,
    months,
    tables: [
      { sheetDefId: 1, sheetCode: 'AIR', sheetNameL10n: L('Air emissions'), sheetCopyId: null, tableDefId: 10, tableCode: 'T1_2', tableNameL10n: L('Combustion sources') },
      { sheetDefId: 2, sheetCode: 'GHG', sheetNameL10n: L('Greenhouse gases'), sheetCopyId: null, tableDefId: 20, tableCode: 'T4_1', tableNameL10n: L('Direct GHG') },
    ],
    equipment: [
      { table: 0, rowKey: 'R1', labelL10n: null, name: 'Boiler B-1', idCode: 'ES-001' },
      { table: 0, rowKey: 'R2', labelL10n: null, name: 'Flare FS-101', idCode: 'ES-002' },
      { table: 1, rowKey: 'R9', labelL10n: L('Total site'), name: null, idCode: null },
    ],
    methodologies: [{ methodologyId: 5, code: 'M-01', nameL10n: L('Stationary combustion'), version: '2.0' }],
    substances: [{ id: 3, code: 'CO2', nameL10n: L('Carbon dioxide') }],
    units: [
      { id: 1, code: 't', symbolL10n: L('t'), dimension: 'Mass', additive: true },
      { id: 2, code: 'kg_per_s', symbolL10n: L('kg·s⁻¹'), dimension: 'MassFlow', additive: false },
    ],
    columns: [{ columnDefId: 99, code: 'GHG_TOTAL', headerL10n: L('GHG total') }],
    rows: [
      { id: 'r-co2', equipment: 0, methodology: 0, outputCode: 'E_CO2', column: null, substance: 0, unit: 0, values: ['1.5', '2.5', ...none(10)], states: 'vvnnnnnnnnnn', yearTotal: '4', monthsIncluded: 2 },
      { id: 'r-rate', equipment: 0, methodology: 0, outputCode: 'RATE', column: null, substance: null, unit: 1, values: ['0.7', ...none(11)], states: 'vannnnnnnnnn', yearTotal: null, monthsIncluded: 0 },
      { id: 'r-flare', equipment: 1, methodology: 0, outputCode: 'E_CO2', column: null, substance: 0, unit: 0, values: [null, '3', ...none(10)], states: 'avnnnnnnnnnn', yearTotal: '3', monthsIncluded: 1 },
      { id: 'r-ghg', equipment: 2, methodology: null, outputCode: 'GHG_TOTAL', column: 0, substance: null, unit: 0, values: ['10', null, ...none(10)], states: 'vannnnnnnnnn', yearTotal: '10', monthsIncluded: 1 },
    ],
    freshnessHidden: false,
  } as unknown as CalculationYearView;
}
