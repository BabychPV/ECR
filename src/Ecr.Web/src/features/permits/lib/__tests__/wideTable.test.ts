import { describe, expect, it } from 'vitest';
import type { PermitColumn, PermitRow } from '../columns';
import { dischargeColumns } from '../../__tests__/dischargeFixture';
import {
  CollapseMinColumns,
  WideThreshold,
  collapsedForMode,
  defaultCollapsed,
  groupNames,
  groupSizes,
  groupSpans,
  isWide,
  sumColumn,
  summaryValue,
  toggleGroup,
  visibleItems,
} from '../wideTable';

function num(code: string, group?: string): PermitColumn {
  return { code, label: code, type: 'number', group };
}

function many(count: number, group?: string, prefix = 'c'): PermitColumn[] {
  return Array.from({ length: count }, (_, i) => num(`${prefix}${i}`, group));
}

describe('wideTable: широка таблиця й групи', () => {
  it('межа «широкої» — строго більше 12 стовпців', () => {
    expect(WideThreshold).toBe(12);
    expect(isWide(many(12))).toBe(false);
    expect(isWide(many(13))).toBe(true);
    expect(isWide([])).toBe(false);
  });

  it('фікстура скидання має 31 стовпець у п\'яти групах', () => {
    expect(dischargeColumns).toHaveLength(31);
    expect(groupNames(dischargeColumns)).toEqual([
      'Location of the point',
      'Tributaries',
      'Dates',
      'Contaminated and clean water',
      'Estimated volume by month',
    ]);
    expect([...groupSizes(dischargeColumns).values()]).toEqual([6, 5, 2, 4, 12]);
  });

  it('за замовчуванням згорнуті групи з ≥ 5 стовпців лише в широкій таблиці', () => {
    expect(CollapseMinColumns).toBe(5);
    // Дат (2) і «чистої/забрудненої» (4) групи лишаються розгорнутими.
    expect([...defaultCollapsed(dischargeColumns)]).toEqual([
      'Location of the point',
      'Tributaries',
      'Estimated volume by month',
    ]);
  });

  it('у вузькій таблиці (≤ 12) не згорнуто нічого, навіть групу з 6 стовпців', () => {
    const narrow = [num('a'), ...many(6, 'G', 'g')];
    expect(isWide(narrow)).toBe(false);
    expect(defaultCollapsed(narrow).size).toBe(0);
  });

  it('межа групи: рівно 5 стовпців згортається, 4 — ні', () => {
    const five = [...many(5, 'Five', 'f'), ...many(4, 'Four', 'q'), ...many(4)];
    expect(isWide(five)).toBe(true);
    expect([...defaultCollapsed(five)]).toEqual(['Five']);
  });

  it('перемикач режиму: grouped згортає як за замовчуванням, all розгортає все', () => {
    expect([...collapsedForMode(dischargeColumns, 'grouped')]).toEqual([...defaultCollapsed(dischargeColumns)]);
    expect(collapsedForMode(dischargeColumns, 'all').size).toBe(0);
  });

  it('toggleGroup повертає новий набір і не мутує вхідний', () => {
    const start = new Set(['A']);
    const next = toggleGroup(start, 'B');
    expect([...next].sort()).toEqual(['A', 'B']);
    expect([...start]).toEqual(['A']);
    expect([...toggleGroup(next, 'A')]).toEqual(['B']);
  });

  it('згорнута група — один підсумковий елемент на місці першого стовпця групи', () => {
    const items = visibleItems(dischargeColumns, defaultCollapsed(dischargeColumns));
    // 31 − (6 + 5 + 12) + 3 підсумки = 31 − 23 + 3 = 11.
    expect(items).toHaveLength(11);
    expect(items[0]).toMatchObject({ kind: 'column', column: { code: 'WaterBody' } });
    expect(items[1]).toMatchObject({ kind: 'summary', group: 'Location of the point' });
    expect(items[2]).toMatchObject({ kind: 'summary', group: 'Tributaries' });
    expect(items[3]).toMatchObject({ kind: 'column', column: { code: 'Sub_Start' } });
    expect(items[items.length - 1]).toMatchObject({ kind: 'column', column: { code: 'Annual' } });
    const summary = items[1];
    expect(summary?.kind === 'summary' ? summary.columns : []).toHaveLength(6);
  });

  it('розкидані стовпці згорнутої групи не виводяться окремо, а підсумок стоїть на першому', () => {
    const columns: PermitColumn[] = [num('a', 'G'), num('x'), num('b', 'G'), num('y')];
    const items = visibleItems(columns, new Set(['G']));
    expect(items.map((i) => (i.kind === 'summary' ? `Σ${i.group}` : i.column.code))).toEqual(['ΣG', 'x', 'y']);
  });

  it('без згортання видно всі стовпці в початковому порядку', () => {
    const items = visibleItems(dischargeColumns, new Set());
    expect(items).toHaveLength(31);
    expect(items.every((i) => i.kind === 'column')).toBe(true);
  });

  it('відрізки верхнього рядка шапки зливають суміжні елементи однієї групи', () => {
    const spans = groupSpans(visibleItems(dischargeColumns, new Set()));
    expect(spans[0]).toEqual({ group: null, span: 1, start: 0 });
    expect(spans[1]).toEqual({ group: 'Location of the point', span: 6, start: 1 });
    expect(spans[2]).toEqual({ group: 'Tributaries', span: 5, start: 7 });
    expect(spans.reduce((s, x) => s + x.span, 0)).toBe(31);
  });

  it('згорнута група в шапці займає одну колонку', () => {
    const spans = groupSpans(visibleItems(dischargeColumns, defaultCollapsed(dischargeColumns)));
    expect(spans.find((s) => s.group === 'Location of the point')).toMatchObject({ span: 1 });
    expect(spans.reduce((s, x) => s + x.span, 0)).toBe(11);
  });

  it('дві різні групи поспіль не зливаються', () => {
    const columns = [num('a', 'A'), num('b', 'B')];
    expect(groupSpans(visibleItems(columns, new Set())).map((s) => s.group)).toEqual(['A', 'B']);
  });
});

describe('wideTable: підсумки', () => {
  const row = (values: Record<string, string | number | null>): PermitRow => ({ id: 'r', ...values });

  it('Σ групи: числові стовпці додаються, не-числа в комірках пропускаються', () => {
    const cols = [num('a'), num('b'), num('c')];
    expect(summaryValue(row({ a: 1.5, b: 2, c: null }), cols)).toBe(3.5);
    expect(summaryValue(row({ a: 1, b: '2', c: 3 }), cols)).toBe(4);
  });

  it('Σ не страждає від хвостів float (0.1 + 0.2 = 0.3)', () => {
    expect(summaryValue(row({ a: 0.1, b: 0.2 }), [num('a'), num('b')])).toBe(0.3);
  });

  it('якщо в групі є не-числовий стовпець — підсумку немає (null), а не вигаданий 0', () => {
    const cols: PermitColumn[] = [num('a'), { code: 'b', label: 'b', type: 'text' }];
    expect(summaryValue(row({ a: 5, b: 'x' }), cols)).toBeNull();
    expect(summaryValue(row({}), [])).toBeNull();
  });

  it('Σ річної групи фікстури рівна сумі місяців', () => {
    const months = dischargeColumns.filter((c) => c.group === 'Estimated volume by month');
    const values = Object.fromEntries(months.map((c, i) => [c.code, i + 1]));
    expect(summaryValue(row(values), months)).toBe(78);
  });

  it('sumColumn підсумовує лише скінченні числа', () => {
    const rows = [row({ x: 1 }), row({ x: 2.25 }), row({ x: null }), row({ x: 'text' }), row({})];
    expect(sumColumn(rows, 'x')).toBe(3.25);
    expect(sumColumn([], 'x')).toBe(0);
    expect(sumColumn([row({ x: Number.NaN }), row({ x: Infinity })], 'x')).toBe(0);
  });
});
