import type { RowDto, TableSliceDto } from '@/api/types';
import { parseNumber } from './clipboard';
import { decide } from './permissions';
import { columnIndexOf, rowIndexOf } from './rowIndex';
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
export function captureEdit(
  slice: TableSliceDto,
  signal: EditSignal,

  // ⚠ `CL-03`: мапа замість двох лінійних пошуків (рядок і колонка). Обидва
  // виклики мемоїзовані за самими масивами зрізу (`rowIndex.ts`), тож
  // викликач, який нічого не передає, не платить за побудову.
  rows: ReadonlyMap<string, RowDto> = rowIndexOf(slice),
): CapturedEdit | null {
  if (signal.columnCode.length === 0 || signal.rowKey.length === 0) return null;

  const column = columnIndexOf(slice).get(signal.columnCode);
  if (column === undefined) return null;

  if (!decide(slice, signal.rowKey, column).editable) return null;

  const row = rows.get(signal.rowKey);
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

/**
 * Поточне значення комірки; `null` — не заповнювали.
 *
 * ⛔ `CL-03`, найдорожче з трьох місць: знімок undo для вставки кличе цю
 * функцію на КОЖНУ з 30 000 комірок буфера, і кожен виклик був
 * `slice.rows.find(...)` по 500 рядках — до 15 млн порівнянь у синхронному
 * `onPaste`, тобто вкладка, яка не відповідає, доки вставка не добіжить.
 */
export function valueOf(
  slice: TableSliceDto,
  rowKey: string,
  columnCode: string,
  rows: ReadonlyMap<string, RowDto> = rowIndexOf(slice),
): unknown {
  return rows.get(rowKey)?.cells[columnCode] ?? null;
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

  // ⛔ Директива registry-lookup, PR A4: комірка `Lookup` тримає
  // `ValueRegistryEntryId` (`CellValueReader.Read`, бекенд) — ЧИСЛО, не текст
  // показу. `LookupCellEditor` завжди шле сюди або порожній рядок (скасовано
  // вибір), або рядкове представлення `entry.Id`, обраного зі списку, — той
  // самий шлях, що й `Int`/`Decimal` вище: нерозпізнане значення лишається
  // текстом, і сервер відповість `ECR-CELL-0422`, а не мовчазний нуль.
  if (dataType === 'Lookup') {
    const trimmed = raw.trim();
    if (trimmed.length === 0) return null;

    const parsed = Number(trimmed);
    return Number.isFinite(parsed) ? parsed : raw;
  }

  return raw;
}
