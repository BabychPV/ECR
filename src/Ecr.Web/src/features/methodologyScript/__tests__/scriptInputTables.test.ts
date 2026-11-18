import { describe, expect, it } from 'vitest';
import {
  assembleCompletionBase,
  functionInfo,
  inputTablesFor,
  inputTablesFromDtos,
  liveTemplateVersionId,
  namedTemplateCodes,
  scriptTableCodes,
  templatesToRead,
  type ScriptTemplateSource,
} from '../scriptInputTables';
import { scriptCompletions } from '../scriptCompletion';
import { outline, outlineTable } from './fixtures';

function source(templateCode: string, tables: Record<string, string[]>): ScriptTemplateSource {
  return {
    templateCode,
    structure: {
      sheets: [
        {
          tables: Object.entries(tables).map(([code, columns]) => ({
            code,
            columns: columns.map((column) => ({
              code: column,
              dataType: 'Decimal',
              headerL10n: { values: { en: `H ${column}` } },
            })),
          })),
        },
      ],
    } as never,
  };
}

const templates = [
  { id: 1, code: 'HSE', isArchived: false },
  { id: 2, code: 'OTHER', isArchived: false },
  { id: 3, code: 'OLD', isArchived: true },
];

describe('контекст підказок із наявних API', () => {
  it('структури читаються лише коли скрипт назвав таблиці; названий шаблон звужує вибір', () => {
    expect(templatesToRead(outline(), templates)).toEqual([]);
    expect(templatesToRead(null, templates)).toEqual([]);

    const anonymous = outline({ tables: [outlineTable('T')] });
    expect(templatesToRead(anonymous, templates).map((t) => t.code)).toEqual(['HSE', 'OTHER']);

    const named = outline({ tables: [outlineTable('T', null, 'OTHER')] });
    expect(templatesToRead(named, templates).map((t) => t.code)).toEqual(['OTHER']);

    // Невідомий код шаблону не лишає редактор без колонок: беруться всі неархівні.
    const unknown = outline({ tables: [outlineTable('T', null, 'NOPE')] });
    expect(templatesToRead(unknown, templates)).toHaveLength(2);
    expect(namedTemplateCodes(named)).toEqual(['OTHER']);
  });

  it('чинна версія шаблону — опублікована з найбільшим Id', () => {
    expect(liveTemplateVersionId([])).toBeNull();
    expect(liveTemplateVersionId([{ id: 9, status: 'Draft' }])).toBeNull();
    expect(
      liveTemplateVersionId([
        { id: 3, status: 'Published' },
        { id: 7, status: 'Published' },
        { id: 9, status: 'Draft' },
        { id: 11, status: 'Deprecated' },
      ]),
    ).toBe(7);
  });

  it('колонки беруться для таблиць зі скрипта; шаблон із outline має перевагу', () => {
    const sources = [source('HSE', { T1: ['A', 'B'] }), source('OTHER', { T1: ['X'], T2: ['Y'] })];

    const tables = inputTablesFor(
      outline({ tables: [outlineTable('T1'), outlineTable('T2', 'Alias'), outlineTable('NONE')] }),
      sources,
    );
    expect(tables.map((t) => [t.tableCode, t.columns.map((c) => c.code)])).toEqual([
      ['T1', ['A', 'B']],
      ['T2', ['Y']],
    ]);
    expect(tables[0]?.columns[0]).toMatchObject({ dataType: 'Decimal' });

    const pinned = inputTablesFor(outline({ tables: [outlineTable('T1', null, 'OTHER')] }), sources);
    expect(pinned[0]?.columns.map((c) => c.code)).toEqual(['X']);
  });

  it('підпис вбудованої функції показує арність і тип результату', () => {
    expect(functionInfo({ name: 'Sum', resultType: 'Number', minArgs: 1, maxArgs: null }).signature).toBe('Sum(1…) → Number');
    expect(functionInfo({ name: 'Round', resultType: 'Number', minArgs: 1, maxArgs: 2 }).signature).toBe('Round(1…2) → Number');
    expect(functionInfo({ name: 'Now', resultType: null, minArgs: 0, maxArgs: 0 }).signature).toBe('Now(0)');
  });

  it('зібраний контекст дає підказки колонок після row. і функції на верхньому рівні', () => {
    const o = outline({ tables: [outlineTable('T1')] });
    const base = assembleCompletionBase({
      outline: o,
      sources: [source('HSE', { T1: ['Land_Fuel'] })],
      functions: [{ name: 'Round', resultType: 'Number', minArgs: 1, maxArgs: 2 }],
      headerFields: ['Company'],
    });

    const afterRow = scriptCompletions({ ...base, text: 'var x = row.', offset: 12 });
    expect(afterRow.map((i) => i.label)).toEqual(['Land_Fuel']);

    const top = scriptCompletions({ ...base, text: 'var x = ', offset: 8 });
    expect(top.find((i) => i.label === 'Round')?.detail).toBe('Round(1…2) → Number');

    const header = scriptCompletions({ ...base, text: 'var x = header.', offset: 15 });
    expect(header.map((i) => i.label)).toEqual(['Company']);
  });
});

describe('колонки з script/tables', () => {
  const dtos = [
    {
      requested: 'HSE.Fuel',
      templateCode: 'HSE',
      tableCode: 'Fuel',
      columns: [
        { code: 'Hours', dataType: 'Decimal', isMonthColumn: false, monthNumber: null, unitCode: 'h' },
        { code: 'Jan', dataType: 'Decimal', isMonthColumn: true, monthNumber: 1, unitCode: null },
      ],
      monthAliases: [],
    },
  ];

  it('коди запиту: Шаблон.Таблиця, якщо шаблон названо; без повторів', () => {
    const o = outline({
      tables: [outlineTable('Fuel', null, 'HSE'), outlineTable('Stream'), outlineTable('Fuel', 'F2', 'HSE')],
    });
    expect(scriptTableCodes(o)).toEqual(['HSE.Fuel', 'Stream']);
    expect(scriptTableCodes(null)).toEqual([]);
  });

  it('відповідь → таблиці підказок із місячними колонками', () => {
    expect(inputTablesFromDtos(dtos)).toEqual([
      {
        tableCode: 'Fuel',
        columns: [
          { code: 'Hours', header: null, dataType: 'Decimal', isMonthColumn: false },
          { code: 'Jan', header: null, dataType: 'Decimal', isMonthColumn: true },
        ],
      },
    ]);
  });

  it('assembleCompletionBase: живі таблиці мають перевагу, порожні — запасний шлях зі структур', () => {
    const o = outline({ tables: [outlineTable('Fuel')] });
    const sources = [source('HSE', { Fuel: ['FromStructure'] })];
    const live = assembleCompletionBase({
      outline: o, sources, functions: [], headerFields: [], liveTables: inputTablesFromDtos(dtos),
    });
    expect(live.tables[0]?.columns.map((c) => c.code)).toEqual(['Hours', 'Jan']);

    const fallback = assembleCompletionBase({
      outline: o, sources, functions: [], headerFields: [], liveTables: [],
    });
    expect(fallback.tables[0]?.columns.map((c) => c.code)).toEqual(['FromStructure']);
  });

  it('F13: запит не має більше за ліміт сервера (50 кодів)', () => {
    const many = outline({
      tables: Array.from({ length: 60 }, (_, i) => outlineTable(`T${String(i)}`)),
    });
    const codes = scriptTableCodes(many);
    expect(codes).toHaveLength(50);
    expect(codes[0]).toBe('T0');
    expect(codes[49]).toBe('T49');
  });
});
