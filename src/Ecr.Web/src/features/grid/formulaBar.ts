import type { TableSliceDto } from '@/api/types';
import { cellText } from './cellValue';
import { cellKey } from './permissions';
import type { FocusedCell } from './selection';
import type { TotalsEdit } from './gridTotals';

/**
 * Що показує рядок формули для комірки під курсором (`UI-08`).
 *
 * ⛔ Вираз береться з контракту зрізу, а не вигадується: `ColumnDto.expression`
 * (UI-25, B4; `Ecr.Application/Documents/Dto/TableSliceDto.cs`). Сервер віддає
 * `null`, коли формули немає або хоч одне посилання в ній читачеві не видне, —
 * тоді рядок формули каже це прямо (`grid.formulaBarNoExpression`), замість
 * показати правдоподібний текст. ✎ 05.11 (HSE401, людина «не бачу формул»):
 * поле в зрізі було, але сюди не доходило - тепер читається напряму.
 *
 * ⚠ Модуль ЧИСТИЙ і не знає ні про RevoGrid, ні про React: він дістає комірку
 * за індексами з тих самих масивів, які сітка отримала пропсами. Арифметики
 * «мінус колонка підпису» тут немає навмисно — індекс колонки береться з
 * `columns[i].prop`, тобто з того самого опису, яким RevoGrid і нумерує
 * колонки; колонка підпису просто не має коду в зрізі й дає `null`.
 */

/** Комірка під курсором, як її показує рядок формули. */
interface FormulaBarModel {
  readonly rowKey: string;
  /** Підпис рядка з моделі сітки (`rowLabelOf`), або технічний ключ. */
  readonly rowLabel: string;
  readonly columnCode: string;
  readonly columnHeader: string;
  /** Позначення одиниці колонки; `null` — безрозмірна. */
  readonly unitSymbol: string | null;
  /** Чи це обчислювана комірка (`Formula`/`Calculated`). */
  readonly isCalculated: boolean;
  /** Текст виразу; `null` — невідомий (див. коментар модуля). */
  readonly expression: string | null;
  /** Значення комірки в канонічному записі; порожній рядок — не заповнено. */
  readonly value: string;
}

/** Звідки рядок формули бере комірку. */
interface FormulaBarInput {
  readonly slice: TableSliceDto;
  /** Колонки, які отримала сітка, — у тому самому порядку, у якому вона їх нумерує. */
  readonly columns: readonly { readonly prop?: string | number }[];
  /** Моделі рядків, які отримала сітка (`gridRows`). */
  readonly rows: readonly Record<string, unknown>[];
  /** Ім'я властивості моделі, під яким лежить підпис рядка. */
  readonly labelProp: string;
  readonly focus: FocusedCell | null;
  /** Незбережені правки зрізу, ключ — `rowKey:columnCode`. */
  readonly pending?: ReadonlyMap<string, TotalsEdit>;
  /** Тексти виразів за кодом колонки — ззовні; порожня, доки сервер їх не віддає. */
  readonly expressionByColumnCode?: ReadonlyMap<string, string>;
}

/**
 * Тип колонки, чия комірка обчислюється, а не вводиться.
 *
 * ⚠ Ті самі два імені, що й у `permissions.decide` (`CalculatedCell`) і
 * `cellValue.NumericColumnTypes`: `Formula` — вираз шаблону, `Calculated` —
 * вихід методології. Для оператора різниці немає — комірку не вводять.
 */
function isCalculatedColumn(dataType: string): boolean {
  return dataType === 'Formula' || dataType === 'Calculated';
}

/**
 * Комірка під курсором; `null` — курсора немає, він поза межами зрізу або
 * стоїть у службовій колонці (підпис рядка), у якої коду в зрізі немає.
 */
export function formulaBarModel(input: FormulaBarInput): FormulaBarModel | null {
  const { slice, columns, rows, labelProp, focus } = input;
  if (focus === null) return null;

  const columnProp = columns[focus.columnIndex]?.prop;
  if (columnProp === undefined) return null;

  const columnCode = String(columnProp);
  const column = slice.columns.find((candidate) => candidate.code === columnCode);
  if (column === undefined) return null;

  const row = slice.rows[focus.rowIndex];
  const model = rows[focus.rowIndex];
  if (row === undefined || model === undefined) return null;

  // ⚠ Незбережена правка перекриває модель: RevoGrid тримає введене у власній
  // копії рядка, тож `rows` про щойно набране число ще не знає, а рядок
  // формули, який показує старе значення комірки з курсором, — це підказка,
  // що суперечить тому, що людина щойно набрала.
  const edit = input.pending?.get(cellKey(row.rowKey, columnCode));
  const value = edit === undefined ? model[columnCode] : edit.value;

  return {
    rowKey: row.rowKey,
    rowLabel: String(model[labelProp] ?? row.rowKey),
    columnCode,
    columnHeader: column.header,
    unitSymbol: column.unitSymbol,
    isCalculated: isCalculatedColumn(column.dataType),
    // UI-25 (B4): вираз приходить у зрізі (`ColumnDto.expression`; сервер віддає `null`, коли формули немає або
    // посилання в ній читачеві не видне). Явна мапа - лише перекриття для тестів і майбутніх джерел.
    expression: input.expressionByColumnCode?.get(columnCode) ?? column.expression ?? null,

    // ⛔ `cellText`, а не `cellDisplay`: рядок формули показує ЗАПИС значення,
    // як поле вводу в Excel, — без групування розрядів і без доповнення
    // нулями до масштабу колонки. Формат показу лишається в самій комірці.
    value: cellText(value),
  };
}
