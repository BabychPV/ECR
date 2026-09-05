import type { TableSliceDto } from '@/api/types';
import { parseNumber } from './clipboard';
import { decide } from './permissions';
import type { CellEdit } from './undo';
import type { PendingEdit } from './useCellPatch';

/**
 * Захоплення правки, зробленої в grid.
 *
 * ⚠ Винесено з компонента навмисно. Саме тут вирішується, чи потрапить
 * введене значення в збереження — і саме тут був дефект, за якого grid
 * показував введене й **не зберігав його**: таблиця виглядала заповненою,
 * доки її не перевідкриють. Перевірити це на змонтованому веб-компоненті
 * дорого, а на чистій функції — один виклик.
 */

/** Те, що віддає grid після редагування комірки. */
export interface EditSignal {
  /** Код колонки; у RevoGrid це `prop`. */
  columnCode: string;
  /** Ключ рядка з моделі. */
  rowKey: string;
  /** Введене значення до приведення типів. */
  raw: string;
}

/** Захоплена правка разом із кроком історії. */
export interface CapturedEdit {
  pending: PendingEdit;
  step: CellEdit;
  columnHeader: string;
}

/**
 * Приводить правку до збереження або відмовляє.
 *
 * ⛔ Повертає <c>null</c>, якщо комірку редагувати не можна. Право
 * перевіряється тут ЩЕ РАЗ, хоча редактор забороненої комірки і не
 * відкривається: fill-handle і програмна правка проходять іншим шляхом, і
 * саме через них у сітку потрапляло б значення, яке сервер відхилить.
 */
export function captureEdit(slice: TableSliceDto, signal: EditSignal): CapturedEdit | null {
  if (signal.columnCode.length === 0 || signal.rowKey.length === 0) return null;

  const column = slice.columns.find((candidate) => candidate.code === signal.columnCode);
  if (column === undefined) return null;

  if (!decide(slice, signal.rowKey, column).editable) return null;

  const row = slice.rows.find((candidate) => candidate.rowKey === signal.rowKey);
  if (row === undefined) return null;

  const after = coerce(signal.raw, column.dataType);
  const before = row.cells[signal.columnCode] ?? null;

  return {
    pending: {
      rowKey: signal.rowKey,
      columnCode: signal.columnCode,
      value: after,
      isEmpty: false,
      baseVersion: row.rowVersion,
    },
    step: { rowKey: signal.rowKey, columnCode: signal.columnCode, before, after },
    columnHeader: column.header,
  };
}

/** Поточне значення комірки; `null` — не заповнювали. */
export function valueOf(slice: TableSliceDto, rowKey: string, columnCode: string): unknown {
  return slice.rows.find((row) => row.rowKey === rowKey)?.cells[columnCode] ?? null;
}

/**
 * Приводить текст до типу колонки.
 *
 * ⚠ Число, прочитане як текст, впало б на серверній валідації вже після
 * відправки — тобто користувач побачив би помилку там, де її не робив.
 *
 * ⛔ Нерозпізнане число лишається **текстом**, а не стає нулем: сервер
 * відповість `ECR-CELL-0422` із назвою колонки, і це чесніше за мовчазний
 * нуль, який у звіті читається як вимірювання.
 */
export function coerce(raw: string, dataType: string | undefined): unknown {
  if (dataType === 'Decimal' || dataType === 'Int') {
    return parseNumber(raw) ?? (raw.trim().length === 0 ? null : raw);
  }

  if (dataType === 'Bool') {
    const normalized = raw.trim().toLowerCase();

    // ⚠ Порожнє не стає `false`: «не заповнювали» і «ні» — різні стани.
    if (normalized.length === 0) return null;

    return normalized === 'true' || normalized === '1' || normalized === 'так';
  }

  return raw;
}
