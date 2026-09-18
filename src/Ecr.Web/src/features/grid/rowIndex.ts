import type { ColumnDto, RowDto, TableSliceDto } from '@/api/types';

/**
 * Пошук рядка й колонки зрізу за ключем — за сталий час (`CL-03`,
 * `DIRECTIVE-14-ARCH.md` §3.5).
 *
 * ⛔ До цього модуля кожне звернення до рядка було `slice.rows.find(...)`, і
 * стояло воно рівно в двох найгарячіших місцях клієнта:
 *   • `cellState.ts` — на **кожну видиму комірку кожного перемальовування**;
 *   • `edits.ts` — на кожну з 30 000 комірок вставки (знімок undo).
 * На таблиці 500×60 це до 15 млн порівнянь рядків у синхронному `onPaste`,
 * тобто вкладка, яка не відповідає, доки вставка не добіжить.
 *
 * ⚠ Мемоїзація — за МАСИВОМ `slice.rows`, а не за самим зрізом і не за
 * `tableInstanceId`. Масив — те, що насправді визначає вміст мапи: новий зріз
 * із кеша TanStack Query приносить новий масив (і мапа будується заново), а
 * новий об'єкт-обгортка з тими самими рядками не коштує нічого. Ключ
 * `tableInstanceId` був би відверто небезпечний: після збереження зріз той
 * самий, а рядки — інші.
 *
 * ⚠ `WeakMap`, а не `Map`: запис живе рівно доти, доки живий сам масив рядків.
 * Звичайна мапа тримала б у пам'яті кожен зріз, який колись показували, —
 * тобто до 91 таблиці × кожна версія після кожного збереження, за вісім годин
 * зміни (та сама природа дефекту, що й `CL-06`).
 *
 * ⚠ Мапа будується **на місці читання**, а не передається згори, і це свідоме
 * відхилення від букви `CL-03` («параметром у всі три місця»). Єдиний
 * викликач усіх трьох — `DocumentGrid.tsx`, а він у цьому пакеті недоторканний
 * (рядок плану `D14-12` іде окремо). Обидва входи лишилися: параметр `rows`
 * необов'язковий, тож коли `DocumentGrid` дійде черга, мемоїзована згори мапа
 * підставляється без зміни цих функцій.
 */

const rowCache = new WeakMap<readonly RowDto[], ReadonlyMap<string, RowDto>>();
const columnCache = new WeakMap<readonly ColumnDto[], ReadonlyMap<string, ColumnDto>>();

/** Рядки зрізу за `rowKey`. */
export function rowIndexOf(slice: TableSliceDto): ReadonlyMap<string, RowDto> {
  const rows: readonly RowDto[] = slice.rows;
  const known = rowCache.get(rows);
  if (known !== undefined) return known;

  const index = new Map<string, RowDto>();

  // ⚠ Перший запис виграє: два рядки з однаковим `rowKey` — дефект даних, але
  // `find()` віддавав саме перший, і поведінка тут не змінюється разом зі
  // швидкістю.
  for (const row of rows) {
    if (!index.has(row.rowKey)) index.set(row.rowKey, row);
  }

  rowCache.set(rows, index);

  return index;
}

/** Колонки зрізу за кодом. */
export function columnIndexOf(slice: TableSliceDto): ReadonlyMap<string, ColumnDto> {
  const columns: readonly ColumnDto[] = slice.columns;
  const known = columnCache.get(columns);
  if (known !== undefined) return known;

  const index = new Map<string, ColumnDto>();
  for (const column of columns) {
    if (!index.has(column.code)) index.set(column.code, column);
  }

  columnCache.set(columns, index);

  return index;
}
