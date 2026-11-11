import type { CalculationYearRows, CalculationYearSummary } from '../api';

const L = (en: string) => ({ values: { en } });
const none = (n: number): (string | null)[] => Array.from({ length: n }, () => null);

const months = Array.from({ length: 12 }, (_, i) => ({
  month: i + 1,
  periodKey: 202601 + i,
  periodState: i < 3 ? 'open' : 'notOpened',
  runState: i === 0 ? 'current' : i === 1 ? 'outOfDate' : i === 2 ? 'noRun' : 'notOpened',
  calculatedAt: i < 2 ? '2026-02-14T09:12:00Z' : null,
}));

const air = { key: '10', sheetDefId: 1, sheetCode: 'AIR', sheetNameL10n: L('Air emissions'), sheetCopyId: null, tableDefId: 10, tableCode: 'T1_2', tableNameL10n: L('Combustion sources') };
const ghg = { key: '20', sheetDefId: 2, sheetCode: 'GHG', sheetNameL10n: L('Greenhouse gases'), sheetCopyId: null, tableDefId: 20, tableCode: 'T4_1', tableNameL10n: L('Direct GHG') };

/** Сторінки таблиць: 10 — два котли методології (t і kg/s), 20 — формула шаблону. */
const pages: Record<string, CalculationYearRows> = {
  '10': {
    table: '10',
    total: 3,
    offset: 0,
    equipment: [
      { table: 0, rowKey: 'R1', labelL10n: null, name: 'Boiler B-1', idCode: 'ES-001' },
      { table: 0, rowKey: 'R2', labelL10n: null, name: 'Flare FS-101', idCode: 'ES-002' },
    ],
    methodologies: [{ methodologyId: 5, code: 'M-01', nameL10n: L('Stationary combustion'), version: '2.0' }],
    substances: [{ id: 3, code: 'CO2', nameL10n: L('Carbon dioxide') }],
    units: [
      { id: 1, code: 't', symbolL10n: L('t'), dimension: 'Mass', additive: true },
      { id: 2, code: 'kg_per_s', symbolL10n: L('kg·s⁻¹'), dimension: 'MassFlow', additive: false },
    ],
    columns: [],
    rows: [
      { id: 'r-co2', equipment: 0, methodology: 0, outputCode: 'E_CO2', column: null, substance: 0, unit: 0, values: ['1.5', '2.5', ...none(10)], states: 'vvnnnnnnnnnn', yearTotal: '4', monthsIncluded: 2 },
      { id: 'r-rate', equipment: 0, methodology: 0, outputCode: 'RATE', column: null, substance: null, unit: 1, values: ['0.7', ...none(11)], states: 'vannnnnnnnnn', yearTotal: null, monthsIncluded: 0 },
      { id: 'r-flare', equipment: 1, methodology: 0, outputCode: 'E_CO2', column: null, substance: 0, unit: 0, values: [null, '3', ...none(10)], states: 'avnnnnnnnnnn', yearTotal: '3', monthsIncluded: 1 },
    ],
  },
  '20': {
    table: '20',
    total: 1,
    offset: 0,
    equipment: [{ table: 0, rowKey: 'R9', labelL10n: L('Total site'), name: null, idCode: null }],
    methodologies: [],
    substances: [],
    units: [{ id: 1, code: 't', symbolL10n: L('t'), dimension: 'Mass', additive: true }],
    columns: [{ columnDefId: 99, code: 'GHG_TOTAL', headerL10n: L('GHG total') }],
    rows: [
      { id: 'r-ghg', equipment: 0, methodology: null, outputCode: 'GHG_TOTAL', column: 0, substance: null, unit: 0, values: ['10', null, ...none(10)], states: 'vannnnnnnnnn', yearTotal: '10', monthsIncluded: 1 },
    ],
  },
} as unknown as Record<string, CalculationYearRows>;

/** Пошук «сервера» фікстури: назва, IDCode, ключ рядка (як `CalculationYearFilter.Query`). */
function matches(page: CalculationYearRows, row: CalculationYearRows['rows'][number], q: string): boolean {
  if (q === '') return true;
  const e = page.equipment[row.equipment];
  return [e?.name, e?.idCode, e?.rowKey].some((text) => (text ?? '').toLowerCase().includes(q.toLowerCase()));
}

/** Сторінка таблиці `table` з пошуком `q` (зсув і розмір фікстура ігнорує — рядків менше за сторінку). */
export function yearRowsFixture(table: string, q = ''): CalculationYearRows {
  const page = pages[table];
  if (page === undefined) return { table, total: 0, offset: 0, equipment: [], methodologies: [], substances: [], units: [], columns: [], rows: [] } as unknown as CalculationYearRows;
  const rows = page.rows.filter((row) => matches(page, row, q));
  return { ...page, total: rows.length, rows };
}

/** Зведення: дві таблиці на двох аркушах, лютий застарілий, березень без прогону, решта не відкрита. */
export function yearSummaryFixture(q = ''): CalculationYearSummary {
  const counted = [air, ghg].map((table) => {
    const rows = pages[table.key]?.rows.length ?? 0;
    const matched = yearRowsFixture(table.key, q).total;
    return { ...table, rows, matched, problems: table.key === '10' ? Math.min(matched, 2) : 0 };
  });
  const matchedRows = counted.reduce((sum, table) => sum + table.matched, 0);
  return {
    documentId: 7,
    year: 2026,
    months,
    tables: counted,
    methodologies: [{ code: 'M-01', nameL10n: L('Stationary combustion') }],
    substances: [{ code: 'CO2', nameL10n: L('Carbon dioxide') }],
    hasFormulas: true,
    totalRows: 4,
    matchedRows,
    problemRows: counted.reduce((sum, table) => sum + table.problems, 0),
    freshnessHidden: false,
  } as unknown as CalculationYearSummary;
}

/** Відповідь «сервера» фікстури за адресою (`…/year/summary` або `…/year/rows`). */
export function yearFixtureFor(url: string): CalculationYearSummary | CalculationYearRows | null {
  const params = new URL(url, 'http://localhost').searchParams;
  const q = params.get('q') ?? '';
  if (url.includes('/calculation-results/year/summary')) return yearSummaryFixture(q);
  if (url.includes('/calculation-results/year/rows')) return yearRowsFixture(params.get('table') ?? '', q);
  return null;
}
