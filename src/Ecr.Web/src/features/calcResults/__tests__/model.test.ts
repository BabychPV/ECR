import { describe, expect, it } from 'vitest';
import { filterParams, isFiltered, NoFilters } from '../api';
import { documentHref, gridItems, latestRunMonth, resolvePage, runCounts, type LoadedTable } from '../model';
import { yearRowsFixture, yearSummaryFixture } from './fixture';

const summary = yearSummaryFixture();
const [air, ghg] = summary.tables as [(typeof summary.tables)[number], (typeof summary.tables)[number]];
const airRows = resolvePage(air, yearRowsFixture('10'));
const ghgRows = resolvePage(ghg, yearRowsFixture('20'));
const all = new Map<string, LoadedTable>([
  ['10', { rows: airRows, total: 3 }],
  ['20', { rows: ghgRows, total: 1 }],
]);
const kinds = (items: ReturnType<typeof gridItems>): string[] => items.map((i) => (i.kind === 'output' ? i.key : i.kind));

/**
 * Модель «Calculation results» (CALC-RESULTS.md): аркуш → таблиця (лише розгорнуті читають рядки) → обладнання ·
 * методологія → виходи → «ще», розв'язання індексів сторінки, параметри фільтрів, адреса джерела.
 * Мутація (локально): у `gridItems` прибрати рядок «ще» для неповної таблиці — червоніє «неповна таблиця».
 */
describe('calcResults model', () => {
  it('розгорнуті таблиці дають смуги обладнання й виходи; згорнута — лише свою смугу', () => {
    expect(kinds(gridItems(summary.tables, all, new Set(), new Set(['10', '20'])))).toEqual([
      'sheet', 'table', 'group', 'r-co2', 'r-rate', 'group', 'r-flare', 'sheet', 'table', 'group', 'r-ghg',
    ]);
    expect(kinds(gridItems(summary.tables, all, new Set(), new Set(['20'])))).toEqual([
      'sheet', 'table', 'sheet', 'table', 'group', 'r-ghg',
    ]);
  });

  it('згорнутий аркуш ховає таблиці; згорнута група — виходи', () => {
    expect(kinds(gridItems(summary.tables, all, new Set(['s1~']), new Set(['10', '20'])))).toEqual([
      'sheet', 'sheet', 'table', 'group', 'r-ghg',
    ]);
    const group = gridItems(summary.tables, all, new Set(), new Set(['10'])).find((i) => i.kind === 'group')!;
    expect(kinds(gridItems(summary.tables, all, new Set([group.key]), new Set(['10'])))).toEqual([
      'sheet', 'table', 'group', 'group', 'r-flare', 'sheet', 'table',
    ]);
  });

  it('неповна таблиця закінчується рядком «ще» з числом завантажених і всіх; не прочитана — «ще» з нуля', () => {
    const partial = new Map<string, LoadedTable>([['10', { rows: airRows.slice(0, 2), total: 3 }]]);
    const items = gridItems(summary.tables, partial, new Set(), new Set(['10', '20']));
    const more = items.filter((i) => i.kind === 'more');
    expect(more.map((i) => (i.kind === 'more' ? [i.table.key, i.loaded, i.total] : null))).toEqual([
      ['10', 2, 3],
      ['20', 0, 1],
    ]);
  });

  it('рядок сторінки бере обладнання, методологію, речовину й одиницю з СВОЄЇ сторінки', () => {
    const flare = airRows.find((r) => r.id === 'r-flare')!;
    expect([flare.equipment.name, flare.methodology?.code, flare.substance?.code, flare.unit?.code]).toEqual(['Flare FS-101', 'M-01', 'CO2', 't']);
    expect([ghgRows[0]!.column?.code, ghgRows[0]!.methodology, ghgRows[0]!.table.key]).toEqual(['GHG_TOTAL', null, '20']);
  });

  it('параметри фільтрів: порожні не передаються, той самий набір — той самий рядок', () => {
    expect(filterParams(NoFilters).toString()).toBe('');
    expect(isFiltered(NoFilters)).toBe(false);
    const filters = { ...NoFilters, query: ' es-002 ', problemsOnly: true, sheet: '2' };
    expect(filterParams(filters).toString()).toBe('q=es-002&sheet=2&problems=true');
    expect(isFiltered(filters)).toBe(true);
  });

  it('лічильники прогонів і місяць результатів за замовчуванням — останній із прогоном', () => {
    expect(runCounts(summary.months)).toEqual({ current: 1, outOfDate: 1, none: 10 });
    expect(latestRunMonth(summary.months)).toBe(2);
  });

  it('адреса джерела — документ на аркуші, таблиці й місяці виходу', () => {
    expect(documentHref(7, 202602, airRows[0])).toBe('/documents/7?periodKey=202602&sheet=AIR&table=T1_2');
    expect(documentHref(7, 202601)).toBe('/documents/7?periodKey=202601');
  });
});
