import { describe, expect, it } from 'vitest';
import {
  allGroupKeys,
  documentHref,
  filterRows,
  FormulaMethodology,
  gridItems,
  hasProblem,
  latestRunMonth,
  NoFilters,
  runCounts,
} from '../model';
import { yearViewFixture } from './fixture';

const view = yearViewFixture();
const label = (row: (typeof view.rows)[number]): string => view.equipment[row.equipment]?.name ?? '';
const byId = (id: string) => view.rows.find((r) => r.id === id)!;

/**
 * Модель «Calculation results» (CALC-RESULTS.md): групи аркуш → таблиця → обладнання · методологія, згортання,
 * клієнтські фільтри, «проблеми», адреса джерела.
 * Мутація (локально): у `hasProblem` прибрати перевірку застарілого місяця — червоніє «застаріле значення — проблема».
 */
describe('calcResults model', () => {
  it('будує смуги трьох рівнів і рядки виходів у порядку сервера', () => {
    const items = gridItems(view, view.rows, new Set());
    expect(items.map((i) => (i.kind === 'band' ? `b${String(i.level)}` : i.key))).toEqual([
      'b0', 'b1', 'b2', 'r-co2', 'r-rate', 'b2', 'r-flare', 'b0', 'b1', 'b2', 'r-ghg',
    ]);
  });

  it('згорнута таблиця ховає обладнання й виходи, «Collapse all» лишає лише аркуші', () => {
    const items = gridItems(view, view.rows, new Set(['s1~/t0']));
    expect(items.filter((i) => i.kind === 'output').map((i) => i.key)).toEqual(['r-ghg']);
    const all = gridItems(view, view.rows, allGroupKeys(view, view.rows));
    expect(all.every((i) => i.kind === 'band' && i.level === 0)).toBe(true);
    expect(all).toHaveLength(2);
  });

  it('фільтрує за пошуком (назва й IDCode), аркушем, методологією, формулою і речовиною', () => {
    expect(filterRows(view, { ...NoFilters, query: 'es-002' }, label).map((r) => r.id)).toEqual(['r-flare']);
    expect(filterRows(view, { ...NoFilters, sheet: '2' }, label).map((r) => r.id)).toEqual(['r-ghg']);
    expect(filterRows(view, { ...NoFilters, methodology: FormulaMethodology }, label).map((r) => r.id)).toEqual(['r-ghg']);
    expect(filterRows(view, { ...NoFilters, methodology: 'M-01', substance: 'CO2' }, label).map((r) => r.id)).toEqual(['r-co2', 'r-flare']);
  });

  it('застаріле значення — проблема; пропуск усередині року — проблема; пропуск до першого значення — ні', () => {
    expect(hasProblem(view, byId('r-co2'))).toBe(true); // лютий застарілий
    expect(hasProblem(view, byId('r-rate'))).toBe(false); // січень є, далі нічого
    expect(hasProblem(view, byId('r-flare'))).toBe(true); // лютий застарілий
    expect(hasProblem(view, { ...byId('r-ghg'), states: 'vavnnnnnnnnn' })).toBe(true);
    expect(filterRows(view, { ...NoFilters, problemsOnly: true }, label).map((r) => r.id)).toEqual(['r-co2', 'r-flare']);
  });

  it('лічильники прогонів і місяць результатів за замовчуванням — останній із прогоном', () => {
    expect(runCounts(view)).toEqual({ current: 1, outOfDate: 1, none: 10 });
    expect(latestRunMonth(view)).toBe(2);
  });

  it('адреса джерела — документ на аркуші, таблиці й місяці виходу', () => {
    expect(documentHref(view, 202602, byId('r-co2'))).toBe('/documents/7?periodKey=202602&sheet=AIR&table=T1_2');
    expect(documentHref(view, 202601)).toBe('/documents/7?periodKey=202601');
  });
});
