import { buildXlsx, type Cell } from './airXlsx';

/**
 * D-569 (P-1123-1): a workbook of exactly the layout of the human's test file «7) Параметры_2024_final_Test.xlsx» (ERA v3.0,
 * table 3.3): title rows 1–3, header rows 4–6 with the same texts and merges (B4:B6 merged «Цех»), the column-number row 7, a
 * group row, sources with their substance rows (no totals block, see below). The same layout as `tests/Ecr.Api.Tests/Air401Shape.cs`.
 *
 * ⛔ The customer's file is not committed (sensitivity label «Internal», customer data; the repository is public): only its layout
 * is copied, the values are neutral.
 */
export interface Shape401Substance {
  readonly code: string;
  readonly tons: number;
}

export interface Shape401Source {
  readonly nae: string;
  readonly workshop: string | null;
  readonly name: string;
  readonly substances: readonly Shape401Substance[];
}

const Merges = [
  'A8:AB8', 'J4:J6', 'AB4:AB6', 'Y4:AA5', 'T4:T6', 'V4:V6', 'W4:W6', 'K4:M5', 'X4:X6', 'N5:O5', 'N4:Q4', 'P5:Q5', 'U4:U6',
  'AA1:AB1', 'AA2:AB2', 'A2:J2', 'A4:A6', 'B4:B6', 'R4:R6', 'H4:H6', 'C4:D5', 'E4:E6', 'F4:F6', 'S4:S6', 'A1:J1', 'A3:M3',
  'G4:G6', 'I4:I6',
];

const Width = 28; // A … AB

function row(cells: Readonly<Record<number, Cell>>): Cell[] {
  return Array.from({ length: Width }, (_, i) => cells[i] ?? null);
}

/** Column index (0-based) of a letter: A → 0, AB → 27. */
function at(letter: string): number {
  return letter.length === 1 ? letter.charCodeAt(0) - 65 : (letter.charCodeAt(0) - 64) * 26 + letter.charCodeAt(1) - 65;
}

function cells(entries: readonly (readonly [string, Cell])[]): Record<number, Cell> {
  return Object.fromEntries(entries.map(([letter, value]) => [at(letter), value]));
}

/** The workbook bytes; `workshopHeader` — the text of the merged header B4:B6 (the test file: «Цех»). */
export function build401(sources: readonly Shape401Source[], workshopHeader = 'Цех'): Buffer {
  const rows: Cell[][] = [
    row(cells([['A', 'ЭРА v3.0  ТОО "TEST"'], ['AA', 'Таблица 3.3']])),
    row(cells([['A', 'Параметры выбросов загрязняющих веществ в атмосферу для расчета нормативов допустимых выбросов на 2024 ']])),
    row(cells([['A', 'Тестовый объект, НДВ-2024 (Параметры)']])),
    row(cells([
      ['A', 'Произ-водство'], ['B', workshopHeader], ['C', 'Источник выделения загрязняющих веществ'], ['E', 'Число часов  работы в году'],
      ['F', 'Наименование  источника выброса вредных веществ'], ['G', 'Номер источника выбросов на карте-схеме'], ['H', 'Т И П    И З А'],
      ['I', 'Высота источника выбросов, м'], ['J', 'Диаметр устья трубы, м'],
      ['K', 'Параметры газовоздушной смеси на выходе из трубы при максимально разовой нагрузке'], ['N', 'Координаты источника на карте-схеме,м.'],
      ['R', 'Угол поворота площадного источника, град.'], ['S', 'Hаименование газоочистных установок, тип и мероприятия по сокращению выбросов'],
      ['T', 'Вещество, по которому производится газоочистка'], ['U', 'Коэффи-циент обеспечен-ности газо-очисткой, %'],
      ['V', 'Среднеэксплуа-тационная степень очистки/            максимальная степень очистки, %'], ['W', 'Код вещества'],
      ['X', 'Hаименование вещества'], ['Y', 'Выбросы загрязняющего вещества'], ['AB', 'Год дости-жения НДВ'],
    ])),
    row(cells([['N', 'точ.ист, /1-го конца линейного источника /центра площадного источника'], ['P', '2-го конца линейного источника / длина, ширина площадного источника']])),
    row(cells([
      ['C', 'Наименование'], ['D', 'Количество, шт.'], ['K', 'Скорость, м/с'], ['L', 'Объем смеси,                м3/с'], ['M', 'Темпе-ратура смеси, оС'],
      ['N', 'Х1'], ['O', 'Y1'], ['P', 'Х2'], ['Q', 'Y2'], ['Y', 'г/с'], ['Z', 'мг/нм3'], ['AA', 'т/год'],
    ])),
    Array.from({ length: Width }, (_, i) => (i === 7 ? '7а' : i === 17 ? '16a' : i < 7 ? i + 1 : i < 17 ? i : i - 1)),
    row(cells([['A', 'Технологические установки. Тестовая площадка']])),
  ];
  for (const source of sources) {
    source.substances.forEach((substance, index) => {
      const head: (readonly [string, Cell])[] =
        index === 0
          ? [
              ['A', '101'], ['B', source.workshop], ['C', source.name], ['D', 1], ['E', 8784], ['F', 'Вытяжной воздуховод'], ['G', source.nae],
              ['H', 'Т'], ['I', 9.5], ['J', 0.25], ['K', 3.63], ['L', 0.1781872], ['M', 34.1], ['N', 9597489], ['O', 5145573],
            ]
          : [];
      rows.push(row(cells([...head, ['W', substance.code], ['X', `Substance ${substance.code}`], ['Y', 0.0002], ['Z', 1.263], ['AA', substance.tons], ['AB', 2024]])));
    });
  }
  // ⚠ No totals block: in the test file its t/year cells are formulas (that is what marks the rows as totals), and `buildXlsx`
  // writes no formulas — a label alone would read as a substance row without a code. The C# twin keeps the block with formulas.

  return buildXlsx(rows, 'Лист1', Merges);
}
