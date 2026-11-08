import { buildXlsx, type Cell } from './airXlsx';

/**
 * P1 06.11: a SANITIZED workbook in the shape of the human's real file («7) Параметры_2024_final.xlsx», ERA v3.0 table 3.3):
 * three title rows, a THREE-row header with the same merged cells (C4:D5, K4:M5, N4:Q4, N5:O5, P5:Q5, Y4:AA5 and the
 * vertical A4:A6 …), a column-number row, a section row merged across the whole width, and sources whose cells are merged
 * DOWN over their substance rows. Names, numbers and coordinates are invented; the real file is never committed.
 */
const Width = 28; // A … AB

const Merges = [
  'A1:J1', 'A2:J2', 'A3:M3',
  'A4:A6', 'B4:B6', 'C4:D5', 'E4:E6', 'F4:F6', 'G4:G6', 'H4:H6', 'I4:I6', 'J4:J6', 'K4:M5', 'N4:Q4', 'N5:O5', 'P5:Q5',
  'R4:R6', 'S4:S6', 'T4:T6', 'U4:U6', 'V4:V6', 'W4:W6', 'X4:X6', 'Y4:AA5', 'AB4:AB6',
];

function row(cells: Readonly<Record<string, Cell>>): Cell[] {
  const out: Cell[] = Array.from({ length: Width }, () => null);
  for (const [letter, value] of Object.entries(cells)) {
    const index = letter.length === 1 ? letter.charCodeAt(0) - 65 : 26 + (letter.charCodeAt(1) - 65);
    out[index] = value;
  }

  return out;
}

const Title: Cell[][] = [
  row({ A: 'ЭРА v3.0 (e2e)', AA: 'Таблица 3.3' }),
  row({ A: 'Параметры выбросов загрязняющих веществ в атмосферу для расчета нормативов (e2e)' }),
  row({ A: 'Тестовый комплекс, НДВ-2024 (e2e)' }),
  row({
    A: 'Произ-водство', B: 'Цех', C: 'Источник выделения загрязняющих веществ', E: 'Число часов  работы в году',
    F: 'Наименование  источника выброса вредных веществ', G: 'Номер источника выбросов на карте-схеме', H: 'Т И П    И З А',
    I: 'Высота источника выбросов, м', J: 'Диаметр устья трубы, м',
    K: 'Параметры газовоздушной смеси на выходе из трубы при максимальной разовой нагрузке',
    N: 'Координаты источника на карте-схеме,м.', R: 'Угол поворота площадного источника, град.',
    S: 'Hаименование газоочистных установок, тип и мероприятия по сокращению выбросов', T: 'Вещество, по которому производится газоочистка',
    U: 'Коэффи-циент обеспечен-ности газо-очисткой, %', V: 'Среднеэксплуа-тационная степень очистки/ максимальная степень очистки, %',
    W: 'Код вещества', X: 'Hаименование вещества', Y: 'Выбросы загрязняющего вещества', AB: 'Год дости-жения НДВ',
  }),
  row({ N: 'точ.ист, /1-го конца линейного источника /центра площадного источника', P: '2-го конца линейного источника / длина, ширина площадного источника' }),
  row({
    C: 'Наименование', D: 'Количество, шт.', K: 'Скорость, м/с', L: 'Объем смеси, м3/с', M: 'Темпе-ратура смеси, оС',
    N: 'Х1', O: 'Y1', P: 'Х2', Q: 'Y2', Y: 'г/с', Z: 'мг/нм3', AA: 'т/год',
  }),
  row(Object.fromEntries(
    ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z', 'AA', 'AB']
      .map((letter, i) => [letter, i === 7 ? '7а' : i === 17 ? '16a' : i < 7 ? i + 1 : i < 17 ? i : i - 1]),
  )),
  row({ A: 'Технологические установки (e2e). Площадка 1' }),
];

export interface RealShapeSource {
  readonly nae: string;
  readonly name: string;
  readonly workshop: string;
  readonly height: number;
  readonly substances: readonly (readonly [code: string, name: string, gs: number, tons: number])[];
}

export const RealShapeSources: readonly RealShapeSource[] = [
  { nae: '0001', name: 'UNIT E2E-001. Гидравлический блок (e2e)', workshop: 'Цех 1', height: 9.5, substances: [['0301', 'Азота диоксид (e2e)', 0.002, 0.063], ['0337', 'Углерод оксид (e2e)', 0.01, 0.3]] },
  { nae: '0002', name: 'UNIT E2E-002. Факел (e2e)', workshop: 'Цех 1', height: 30, substances: [['0330', 'Сера диоксид (e2e)', 1.5, 3.5]] },
  { nae: '0003', name: 'UNIT E2E-003. Резервуар (e2e)', workshop: 'Цех 2', height: 4, substances: [['0301', 'Азота диоксид (e2e)', 0.1, 0.4], ['0330', 'Сера диоксид (e2e)', 0.2, 0.5], ['0337', 'Углерод оксид (e2e)', 0.3, 0.6]] },
];

/**
 * The workbook: source columns A…V merged down over each source's substance rows, as in the real form. `workshopHeader` —
 * another header text of column B (row 4) instead of «Цех», so a test can name an added column after it without the others
 * matching it (pass 3b).
 */
export function realShapeWorkbook(sources: readonly RealShapeSource[], workshopHeader?: string): Buffer {
  const rows: Cell[][] = Title.map((r) => [...r]);
  const header = rows[3];
  if (workshopHeader !== undefined && header !== undefined) header[1] = workshopHeader; // B4
  const merges = [...Merges, 'A8:AB8'];

  for (const source of sources) {
    const first = rows.length + 1;
    source.substances.forEach(([code, name, gs, tons], index) => {
      rows.push(
        row({
          ...(index === 0
            ? {
                A: '101', B: source.workshop, C: source.name, D: 1, E: 8784, F: 'Труба (e2e)', G: source.nae, H: 'Т',
                I: source.height, J: 0.25, K: 3.6, L: 0.18, M: 34.1, N: 9597000, O: 5145000,
              }
            : {}),
          W: code, X: name, Y: gs, AA: tons, AB: 2024,
        }),
      );
    });
    const last = rows.length;
    if (last > first) {
      for (const letter of ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V']) {
        merges.push(`${letter}${String(first)}:${letter}${String(last)}`);
      }
    }
  }

  return buildXlsx(rows, 'Лист1', merges);
}
