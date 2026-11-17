import { describe, expect, it } from 'vitest';
import {
  isInsideStringOrComment,
  scriptCompletions,
  type ScriptCompletionContext,
  type ScriptInputTable,
} from '../scriptCompletion';
import type { ScriptOutlineDto } from '../types';

const tables: ScriptInputTable[] = [
  {
    tableCode: 'T3_00',
    columns: [
      { code: 'Land_Fuel', header: 'Fuel', dataType: 'Registry', isMonthColumn: false },
      { code: 'Land_Hours_M01', dataType: 'Number', isMonthColumn: true },
      { code: 'Land_Hours_M02', dataType: 'Number', isMonthColumn: true },
    ],
  },
  {
    tableCode: 'T10_03',
    columns: [{ code: 'Gtu_Month', dataType: 'Number', isMonthColumn: false }],
  },
];

const outline: ScriptOutlineDto = {
  tables: [{ code: 'T3_00' }, { code: 'T10_03', alias: 'Gtu' }],
  inputs: [],
  outputs: [{ code: 'OUT_A', unit: 'g_per_s', perSubstance: false }],
  substances: [],
  imports: ['Common'],
  constants: [
    { name: 'k1', kind: 'scalar', line: 1 },
    { name: 'EF', kind: 'map', line: 2 },
  ],
};

function at(text: string, extra: Partial<ScriptCompletionContext> = {}): ReturnType<typeof scriptCompletions> {
  const marker = text.indexOf('|');
  const source = marker === -1 ? text : text.replace('|', '');
  return scriptCompletions({
    text: source,
    offset: marker === -1 ? source.length : marker,
    outline,
    tables,
    functions: [
      { name: 'Max', signature: 'Max(a, b, …)' },
      { name: '$agg', signature: null },
    ],
    headerFields: ['OrgCode', 'Year'],
    ...extra,
  });
}

const labels = (items: { label: string }[]): string[] => items.map((i) => i.label);

describe('підказки: верхній рівень', () => {
  const items = at('');

  it('ключові слова', () => {
    expect(labels(items)).toEqual(expect.arrayContaining(['rows', 'foreach', 'reject', 'table', 'visible']));
    expect(items.find((i) => i.label === 'entry')?.insertText).toContain('${1:REGISTRY}');
  });

  it('усі заготовки з плану', () => {
    const snippets = items.filter((i) => i.kind === 'snippet').map((i) => i.label);
    expect(snippets).toEqual(
      expect.arrayContaining([
        'rows from',
        'const map',
        'output',
        'visible var',
        'foreach substance',
        'foreach repeat',
        'if else',
        'table + aggregate',
        'reject when',
      ]),
    );
    expect(items.filter((i) => i.kind === 'snippet').every((i) => i.isSnippet)).toBe(true);
  });

  it('агрегати з умовою where', () => {
    const sum = items.find((i) => i.label === 'Sum');
    expect(sum?.insertText).toContain('where');
    expect(items.find((i) => i.label === 'Count')?.insertText).toContain('${1:Alias}');
  });

  it('імена const, output, import і псевдонімів', () => {
    expect(labels(items)).toEqual(expect.arrayContaining(['k1', 'EF', 'OUT_A', 'Common', 'Gtu']));
  });

  it('змінні й функції з тексту; var — лише вище курсора', () => {
    const result = at('var before = 1;\nfunction f(a) = a;\n|\nvar after = 2;');
    expect(labels(result)).toEqual(expect.arrayContaining(['before', 'f']));
    expect(labels(result)).not.toContain('after');
  });

  it('вбудовані функції; хостові ($…) приховані', () => {
    expect(labels(items)).toContain('Max');
    expect(labels(items)).not.toContain('$agg');
    expect(items.find((i) => i.label === 'Max' && i.detail === 'Max(a, b, …)')).toBeDefined();
  });

  it('змінна foreach видна всередині', () => {
    const result = at('foreach S in (1, 2) { var x = | }');
    expect(labels(result)).toContain('S');
  });
});

describe('підказки: після крапки', () => {
  it('row. — колонки вхідних таблиць (без таблиці-псевдоніма)', () => {
    const result = at('var x = row.|');
    expect(labels(result)).toContain('Land_Fuel');
    expect(labels(result)).not.toContain('Gtu_Month');
    expect(result.find((i) => i.label === 'Land_Fuel')?.documentation).toBe('Fuel');
  });

  it('row. — місячні колонки згортаються в одну з індексом [m]', () => {
    const result = at('var x = row.|');
    const hours = result.filter((i) => i.label === 'Land_Hours');
    expect(hours).toHaveLength(1);
    expect(hours[0]?.insertText).toBe('Land_Hours[${1:period.Month}]');
    expect(hours[0]?.isSnippet).toBe(true);
  });

  it('row. без outline — усі таблиці', () => {
    expect(labels(at('row.|', { outline: null }))).toEqual(
      expect.arrayContaining(['Land_Fuel', 'Gtu_Month']),
    );
  });

  it('row.part — теж колонки (фільтрує Monaco)', () => {
    expect(labels(at('row.Lan|'))).toContain('Land_Fuel');
  });

  it('Alias. — колонки таблиці псевдоніма з outline', () => {
    expect(labels(at('var m = Max(Gtu.|'))).toEqual(['Gtu_Month']);
  });

  it('Alias. — псевдонім, оголошений лише в тексті', () => {
    const result = at('table G2 = T3_00;\nvar a = Sum(G2.|', { outline: null });
    expect(labels(result)).toContain('Land_Fuel');
  });

  it('псевдонім із кодом шаблону [Tpl.]Code', () => {
    const result = at('table G3 = TPL.T10_03;\nSum(G3.|', { outline: null });
    expect(labels(result)).toEqual(['Gtu_Month']);
  });

  it('period. — члени періоду', () => {
    expect(labels(at('period.|'))).toEqual([
      'Days', 'Hours', 'Seconds', 'Start', 'End', 'Year', 'Month', 'EndKey',
    ]);
  });

  it('header. — передані поля', () => {
    expect(labels(at('header.|'))).toEqual(['OrgCode', 'Year']);
  });

  it('S. у foreach substance — лише code', () => {
    expect(labels(at('foreach substance S in ("A") { var c = S.|'))).toEqual(['code']);
  });

  it('невідомий кваліфікатор і ланцюжок a.b.c — порожньо', () => {
    expect(at('Unknown.|')).toEqual([]);
    expect(at('EF.Summer.|')).toEqual([]);
  });
});

describe('підказки: рядки й коментарі', () => {
  it('всередині рядка й коментаря — нічого', () => {
    expect(at('var s = "ro|w";')).toEqual([]);
    expect(at('// co|mment')).toEqual([]);
    expect(at('/* a | b */')).toEqual([]);
  });

  it('після закритого рядка/коментаря — знову працює', () => {
    expect(at('var s = "x"; |').length).toBeGreaterThan(0);
    expect(at('/* a */ |').length).toBeGreaterThan(0);
    expect(at('// c\n|').length).toBeGreaterThan(0);
  });

  it('isInsideStringOrComment: екранована лапка не закриває рядок', () => {
    const text = 'var s = "a\\"b';
    expect(isInsideStringOrComment(text, text.length)).toBe(true);
  });
});
