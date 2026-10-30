import { buildXlsx, type Cell } from './airXlsx';

/**
 * Air limits e2e (NAE N3/N5): рядки робочої книги в розкладці, яку читає ВБУДОВАНИЙ профіль `ERA_TABLE_33`
 * (двохрядкова шапка: тема + одиниця, рядок номерів колонок, далі дані). Це зменшена копія форми «таблиця 3.3» без
 * об'єднаних клітинок: текст теми повторено в кожній колонці, одиниця (г/с, мг/нм3, т/год) стоїть в останньому рядку шапки.
 */
const Header = [
  ['Таблица 3.3 Параметры выбросов (e2e)'],
  [
    'Источник выделения загрязняющих веществ',
    'Номер источника выбросов на карте-схеме',
    'Т И П  И З А',
    'Высота источника выбросов, м',
    'Код вещества',
    'Наименование вещества',
    'Выбросы загрязняющего вещества',
    'Выбросы загрязняющего вещества',
    'Выбросы загрязняющего вещества',
  ],
  ['Наименование', null, null, null, null, null, 'г/с', 'мг/нм3', 'т/год'],
  [1, 2, 3, 4, 5, 6, 7, 8, 9],
] as const satisfies readonly (readonly Cell[])[];

export interface AirSourceRow {
  readonly nae: string;
  readonly name: string;
  readonly type: 'Т' | 'П1';
  readonly height: number;
  readonly substances: readonly (readonly [code: string, name: string, gs: number | null, mg: number | null, tons: number | null])[];
}

/** Кінцевий розмір вибірки: 3 джерела, 4 рядки речовин, одна порожня т/рік (попередження `regValueEmpty`). */
export const SmallSources: readonly AirSourceRow[] = [
  { nae: '0001', name: 'Boiler stack', type: 'Т', height: 12.5, substances: [['0301', 'Nitrogen dioxide', 0.5, 25, 1.2], ['0337', 'Carbon oxide', 0.25, null, null]] },
  { nae: '0002', name: 'Flare', type: 'Т', height: 30, substances: [['0330', 'Sulphur dioxide', 1.5, null, 3.5]] },
  { nae: '0003', name: 'Tank vent', type: 'П1', height: 4, substances: [['0301', 'Nitrogen dioxide', 0.1, null, 0.4]] },
];

/** Рядки аркуша: перший рядок речовин стоїть у рядку джерела, решта — нижче з порожніми клітинками джерела. */
export function sheetRows(sources: readonly AirSourceRow[]): Cell[][] {
  const rows: Cell[][] = Header.map((r) => [...r]);
  for (const source of sources) {
    source.substances.forEach(([code, name, gs, mg, tons], index) => {
      rows.push([
        index === 0 ? source.name : null,
        index === 0 ? source.nae : null,
        index === 0 ? source.type : null,
        index === 0 ? source.height : null,
        code,
        name,
        gs,
        mg,
        tons,
      ]);
    });
  }

  return rows;
}

export function workbook(sources: readonly AirSourceRow[]): Buffer {
  return buildXlsx(sheetRows(sources));
}

/** Багато джерел для перевірки прокрутки/висоти рядків картки. */
export function manySources(count: number, prefix: number): AirSourceRow[] {
  return Array.from({ length: count }, (_, i): AirSourceRow => ({
    nae: String(prefix + i).padStart(4, '0'),
    name: `Generated source ${String(i + 1)}`,
    type: i % 4 === 3 ? 'П1' : 'Т',
    height: 5 + (i % 20),
    substances: [
      ['0301', 'Nitrogen dioxide', 0.1 + (i % 7) / 10, null, 0.5 + (i % 11) / 10],
      ['0337', 'Carbon oxide', 0.2, 15, 0.9],
    ],
  }));
}
