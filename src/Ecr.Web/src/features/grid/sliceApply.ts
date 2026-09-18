import type { PatchCellsRequest, PatchCellsResponse, RowDto, TableSliceDto } from '@/api/types';

/**
 * Застосування відповіді `PATCH` до вже прочитаного зрізу (`CL-01`,
 * `DIRECTIVE-14-ARCH.md` §3.5).
 *
 * ⛔ Що тут виправляється. `useCellPatch` після **кожного** успішного
 * збереження робив `invalidateQueries(['table-slice', id, period])` — тобто
 * автозбереження коштувало `PATCH` **плюс найважчий `GET` системи**
 * (`GetTableSliceHandler`, бюджет p95 1.5 с на 500×60). При автозбереженні
 * раз на 500 мс один оператор давав до двох повних читань зрізу на секунду.
 *
 * ⛔ І цей `GET` навіть не досягав мети. Перерахунок ставиться в чергу й іде
 * АСИНХРОННО (`PatchCellsHandler` → `EnqueueAsync`), тож відповідь на
 * негайний перезапит приходила здебільшого РАНІШЕ за нього — зі старими
 * обчисленими значеннями. Тобто платили найдорожчим запитом системи за дані,
 * які й так доведеться перечитувати.
 *
 * ⚠ Все, що потрібно показати одразу, вже є у відповіді й у самому запиті:
 * власні значення оператора (їх він і ввів) і нові `rowVersions`. Округлення
 * при вставці теж уже враховане — `rounding.ts` округлює ДО надсилання, тож у
 * запиті лежить саме те число, що пішло на сервер (`ФВ-9.16c`, `D-116`).
 *
 * ⚠ Обчислені колонки цим шляхом НЕ оновлюються, і це чесно названо, а не
 * приховано: їх приносить перерахунок, за яким клієнт зможе стежити лише
 * тоді, коли `PatchCellsResponse` понесе ідентифікатор задачі. Зараз контракт
 * його не має (`schema.d.ts`: `appliedCells`, `rowVersions`, `validation` — і
 * все), тож друга половина `CL-01` — серверна зміна й окремий рядок плану.
 */

/**
 * Новий зріз із застосованими правками батчу.
 *
 * ⚠ Повертає **новий** об'єкт (і нові об'єкти лише для зачеплених рядків):
 * TanStack Query порівнює посилання, і мутація на місці лишила б сітку з
 * тими самими даними на екрані.
 *
 * ⚠ Рядок, якого в зрізі немає (створення рядка динамічної таблиці), мовчки
 * пропускається: у відповіді немає ні `ordinal`, ні `rowKind`, ні підпису, а
 * вигаданий рядок у сітці — гірше за рядок, який з'явиться після
 * перечитування (`DocumentGrid` робить його сам, `addRow.onSuccess`).
 */
export function applyPatchToSlice(
  slice: TableSliceDto,
  request: PatchCellsRequest,
  response: PatchCellsResponse,
): TableSliceDto {
  // ⚠ Чужий зріз не чіпаємо: батч адресується екземпляру таблиці, і
  // застосувати його до сусіднього означало б показати числа не в тій
  // таблиці. Дешева перевірка, яка ловить помилку виклику, а не даних.
  if (slice.tableInstanceId !== request.tableInstanceId) return slice;

  const byRowKey = new Map(request.rows.map((row) => [row.rowKey, row] as const));
  if (byRowKey.size === 0) return slice;

  let changed = false;

  const rows = slice.rows.map((row) => {
    const patched = byRowKey.get(row.rowKey);
    if (patched === undefined) return row;

    changed = true;

    return {
      ...row,
      cells: applyCells(row.cells, patched.cells),

      // ⚠ Версія — З ВІДПОВІДІ, і лише якщо сервер її назвав. Лишити стару
      // означало б, що наступний патч того самого рядка піде зі старим
      // `baseVersion` і дістане 409 на власних змінах — той самий конфлікт
      // із самим собою, від якого застерігає `useCellPatch`.
      rowVersion: response.rowVersions[row.rowKey] ?? row.rowVersion,
    } satisfies RowDto;
  });

  return changed ? { ...slice, rows } : slice;
}

/**
 * Значення комірок рядка після батчу.
 *
 * ⛔ Три різні наміри `R-B4` розрізняються й тут, бо інакше локальне
 * застосування розійшлося б із тим, що записав сервер:
 *   • `isEmpty: true` — **явна порожнеча**: ключ присутній зі значенням `null`;
 *   • `value: null` без `isEmpty` — **стерти**: ключа немає взагалі;
 *   • решта — значення як є.
 * Різниця між «тут свідомо порожньо» і «не заповнювали» видима в сітці
 * (`emptiness.ts`) і в подачі аркуша, тож злити їх не можна навіть у кеші.
 */
function applyCells(
  current: Record<string, unknown>,
  cells: PatchCellsRequest['rows'][number]['cells'],
): Record<string, unknown> {
  const next: Record<string, unknown> = { ...current };

  for (const cell of cells) {
    if (cell.isEmpty) {
      next[cell.columnCode] = null;
      continue;
    }

    if (cell.value === null || cell.value === undefined) {
      delete next[cell.columnCode];
      continue;
    }

    next[cell.columnCode] = cell.value;
  }

  return next;
}
