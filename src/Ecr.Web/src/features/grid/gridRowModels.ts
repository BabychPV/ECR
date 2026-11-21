import type { TableSliceDto } from '@/api/types';
import { editorValueOf } from './cellValue';
import { effectiveDefault } from './rowDefaults';

/** Рядок у моделі grid: значення за кодами колонок плюс службовий ключ. */
export type GridRow = Record<string, unknown> & { __rowKey: string };

type SliceRow = TableSliceDto['rows'][number];

/**
 * Ім'я властивості моделі, під яким живе ПІДПИС рядка.
 *
 * ⛔ Подвійне підкреслення — та сама домовленість, що й у `__rowKey`: службове
 * поле не має права зіткнутися з кодом колонки таблиці. Код колонки —
 * латиниця, цифри й підкреслення (`EcrCode`), тож формально зіткнення
 * можливе; префікс робить його неможливим на практиці й водночас читається як
 * «це не дані».
 *
 * ⚠ Ця колонка НЕ входить у `slice.columns`, тому вставка з буфера
 * (`planPaste` отримує саме `slice.columns.map(c => c.code)`), збереження
 * (`PATCH` шле коди колонок) і стани комірок її не бачать узагалі. Це не
 * випадковість, а причина, чому підпис зроблено окремою колонкою, а не
 * псевдоколонкою в самому зрізі.
 */
export const RowLabelProp = '__rowLabel';

/**
 * Підпис рядка, яким його бачить оператор.
 *
 * ⚠ Запасний варіант — `rowKey`, і це не вигадка: так само робить сторінка
 * версії шаблону (`TemplateVersionPage`, `row.label ?? row.rowKey`). Порожній
 * підпис у формі з фіксованими рядками гірший за технічний ключ: ключ
 * принаймні дає за що зачепитися очима й що назвати в листі підтримці.
 */
export function rowLabelOf(row: SliceRow): string {
  return row.label ?? row.rowKey;
}

/**
 * Модель ОДНОГО рядка grid; порожні комірки беруться з `defaultValue` колонки (ФВ-3.8).
 *
 * @param overrides Значення поверх зрізу (підтверджені, незбережені, відхилені — `shownOverrides` сітки) — лише
 * цього рядка, за кодом колонки.
 */
function rowModel(row: SliceRow, columns: TableSliceDto['columns'], overrides: ReadonlyMap<string, unknown> | undefined): GridRow {
  const model: GridRow = { __rowKey: row.rowKey };

  // ⚠ Підпис кладеться в модель ЗАВЖДИ, а колонка для нього з'являється лише
  // там, де є що показати (`hasRowLabels`). Умовне поле зробило б форму
  // рядка непостійною, а `RevoGrid` читає значення за іменем властивості —
  // відсутність поля й порожній підпис виглядали б однаково.
  model[RowLabelProp] = rowLabelOf(row);

  for (const column of columns) {
    // ⛔ `U-24`: модель рядка — це те, що RevoGrid кладе в поле РЕДАКТОРА
    // (подвійний клік, F2, початок друку). Тому тут десяткове вже без
    // хвостових нулів сховища (`editorValueOf`, `cellValue.ts`).
    model[column.code] = editorValueOf(
      overrides?.has(column.code)
        ? overrides.get(column.code)
        : (row.cells[column.code] ?? effectiveDefault(row, column) ?? ''),
      column,
    );
  }

  return model;
}

/** Перекриття, розкладені за рядками: `rowKey` → (`columnCode` → значення). */
function overridesByRow(slice: TableSliceDto, overrides: ReadonlyMap<string, unknown> | undefined): Map<string, Map<string, unknown>> {
  const byRow = new Map<string, Map<string, unknown>>();
  if (overrides === undefined || overrides.size === 0) return byRow;

  // ⚠ Ключ перекриття — `cellKey(rowKey, columnCode)` = `rowKey:columnCode`. Код колонки — `EcrCode` (латиниця,
  // цифри, підкреслення), двокрапки в ньому не буває, тож ОСТАННЯ двокрапка ділить ключ однозначно, навіть якщо
  // двокрапка є в ключі рядка. Перекриття колонки, якої в зрізі немає, моделі не стосується — як і доти.
  const codes = new Set(slice.columns.map((column) => column.code));
  for (const [key, value] of overrides) {
    const at = key.lastIndexOf(':');
    if (at < 0) continue;
    const code = key.slice(at + 1);
    if (!codes.has(code)) continue;
    const rowKey = key.slice(0, at);
    const own = byRow.get(rowKey) ?? new Map<string, unknown>();
    own.set(code, value);
    byRow.set(rowKey, own);
  }

  return byRow;
}

/** Рядки grid без кешу — еталон для перевірок і перший прохід (PSS-18). */
export function gridRows(slice: TableSliceDto, overrides?: ReadonlyMap<string, unknown>): GridRow[] {
  const byRow = overridesByRow(slice, overrides);

  return slice.rows.map((row) => rowModel(row, slice.columns, byRow.get(row.rowKey)));
}

interface CachedRow {
  readonly columns: TableSliceDto['columns'];
  readonly overrides: ReadonlyMap<string, unknown> | undefined;
  readonly model: GridRow;
}

function sameOverrides(a: ReadonlyMap<string, unknown> | undefined, b: ReadonlyMap<string, unknown> | undefined): boolean {
  if (a === b) return true;
  if ((a?.size ?? 0) !== (b?.size ?? 0)) return false;
  if (a === undefined || b === undefined) return true;
  for (const [code, value] of a) {
    if (!b.has(code) || !Object.is(b.get(code), value)) return false;
  }
  return true;
}

/**
 * Рядки grid з кешем моделі НА РЯДОК (PSS-18).
 *
 * ⛔ Доти `gridRows` перебудовував УСІ комірки таблиці на кожну зміну незбережених правок (`shownOverrides` сітки
 * змінюється на кожне натискання): 500×60 = 30 000 викликів `editorValueOf` і 500 нових об'єктів на одну правку
 * (замір: ~21 мс на правку без тротлінгу → ~0,4 мс). Тепер модель рядка будується заново, лише коли змінився сам рядок
 * зрізу (об'єкт — `applyPatchToSlice` лишає незачеплені рядки тими самими), набір колонок або перекриття САМЕ ЦЬОГО
 * рядка.
 *
 * ⛔ RevoGrid пише введене прямо в об'єкт моделі рядка (`source[i][prop] = val`). Тому кеш тримає ЛИШЕ модель,
 * побудовану для ПОТОЧНИХ перекриттів рядка, і замінюється цілком на кожному виклику: прийняте введення змінює
 * перекриття рядка (правка в сховищі) — модель будується заново; повернення до збереженого (Undo, «Discard») знову
 * змінює перекриття — і знову нова модель, а не та, у яку RevoGrid колись вписав введене. Введення, що перекриттів не
 * змінило (те саме значення), лишає в моделі те саме значення — як і доти, коли пам'ять перебудовувалась лише на зміну
 * `data`/`shownOverrides`.
 *
 * ⚠ Кеш живе в замиканні (одне на сітку, `useRef` у `DocumentGrid`) і тримає рядки слабко (`WeakMap`): зріз, що пішов
 * із кешу запитів, звільняє і моделі.
 */
export function createGridRowsCache(): (slice: TableSliceDto, overrides?: ReadonlyMap<string, unknown>) => GridRow[] {
  let cache = new WeakMap<SliceRow, CachedRow>();

  return (slice, overrides) => {
    const byRow = overridesByRow(slice, overrides);
    const next = new WeakMap<SliceRow, CachedRow>();

    const rows = slice.rows.map((row) => {
      const own = byRow.get(row.rowKey);
      const hit = cache.get(row);
      const model =
        hit !== undefined && hit.columns === slice.columns && sameOverrides(hit.overrides, own)
          ? hit.model
          : rowModel(row, slice.columns, own);

      next.set(row, { columns: slice.columns, overrides: own, model });
      return model;
    });
    cache = next;

    return rows;
  };
}