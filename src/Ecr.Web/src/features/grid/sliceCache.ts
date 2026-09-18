import type { Query, QueryClient } from '@tanstack/react-query';
import { isSliceKey, queryKeys, sliceAddressOf } from '@/api/queryKeys';
import type { DocumentTableDto } from '@/api/types';

/**
 * Адресна інвалідація зрізів (`CL-02`, `DIRECTIVE-14-ARCH.md` §3.5).
 *
 * ⛔ Що тут виправляється. Три місця —`ImportPanel`, `SheetActions`,
 * `PeriodsPage` — робили `invalidateQueries({ queryKey: ['table-slice'] })`,
 * тобто збіг за ПРЕФІКСОМ: під нього підпадає кожен змонтований зріз. На
 * документі чинного розміру це до 91 таблиці, і всі вони перезапитуються
 * одночасно — найважчим запитом системи, по одному на таблицю.
 *
 * ⚠ `refetchType` за замовчуванням — `'active'`, тобто перезапитуються лише
 * зрізи зі змонтованим спостерігачем. Це вже фільтр, але недостатній:
 * `SheetTables` монтує сітки за прокруткою і НЕ розмонтовує їх (там живуть
 * незбережені правки й історія Undo), тож «активних» на аркуші рівно стільки,
 * скільки таблиць людина проїхала.
 *
 * **Що робить цей модуль.** Ділить зрізи надвоє:
 *   • у межах наміру (той самий документ/аркуш/період) — звичайна
 *     інвалідація з перезапитом активних;
 *   • решта — `refetchType: 'none'`: позначені застарілими, але без жодного
 *     запиту. Вони перечитаються самі, коли сітку змонтують наступного разу
 *     (перемикання аркуша розмонтовує сітки попереднього).
 */

/** Скільки зріз вважається свіжим (`CL-02`: `staleTime` ≥ 5 хв). */
export const SliceStaleTime = 5 * 60_000;

/**
 * Політика кешу зрізів: без перезапиту на фокус вікна, свіжість 5 хв.
 *
 * ⛔ `refetchOnWindowFocus` увімкнений за замовчуванням, і саме на цьому
 * екрані він коштує найдорожче: оператор звіряє числа з Excel, повертається
 * до вкладки через 31 с — і кожен змонтований зріз (до 91 на аркуші)
 * перезапитується одночасно. За ті 31 с у документі, який він сам тримає
 * відкритим, не змінилося нічого.
 *
 * ⚠ Функція, а не рядок усередині `App.tsx`: політику треба перевіряти, а
 * `App.tsx` тягне за собою роутер з усіма сторінками. Тут вона перевіряється
 * на порожньому `QueryClient` за десяток мілісекунд.
 *
 * ⚠ Дефолти за ПРЕФІКСОМ ключа: діють і на зрізи, яких у кеші ще немає, —
 * тобто не залежать від того, чи встиг `DocumentGrid` змонтуватися.
 */
export function applySliceCachePolicy(client: QueryClient): void {
  client.setQueryDefaults(queryKeys.slices.all(), {
    staleTime: SliceStaleTime,
    refetchOnWindowFocus: false,
  });
}

/** Куди дісталася зміна. */
export interface SliceScope {
  /** Період; зрізи інших періодів перезапитувати нема причини. */
  readonly periodKey: number;

  /** Документ; `undefined` — намір ширший за документ (перерахунок проєкту). */
  readonly documentId?: number;

  /** Аркуш документа; `undefined` — усі аркуші документа. */
  readonly sheetDefId?: number;
}

/**
 * Екземпляри таблиць у межах наміру; `null` — перелік невідомий.
 *
 * ⚠ Перелік береться з УЖЕ прочитаного кеша (`['document-tables', …]`, той
 * самий ключ, що тримає `DocumentPage`), а не новим запитом: інвалідація не
 * має права сама ходити в мережу заради того, щоб вирішити, кого не питати.
 *
 * ⚠ Порожнього кеша достатньо, щоб чесно відповісти «не знаю» (`null`), і
 * тоді намір звужується лише періодом — стара поведінка в межах одного
 * періоду, не гірша за неї.
 */
export function tablesInScope(client: QueryClient, scope: SliceScope): ReadonlySet<number> | null {
  if (scope.documentId === undefined) return null;

  const tables = client.getQueryData<DocumentTableDto[]>([
    'document-tables',
    scope.documentId,
    scope.periodKey,
  ]);

  if (tables === undefined) return null;

  const ids = new Set<number>();
  for (const table of tables) {
    if (scope.sheetDefId !== undefined && table.sheetDefId !== scope.sheetDefId) continue;
    ids.add(table.tableInstanceId);
  }

  return ids;
}

/**
 * Перезапитує зрізи в межах наміру; решту лише позначає застарілими.
 *
 * ⚠ Два виклики, а не один із `predicate`: `refetchType` задається на ВЕСЬ
 * виклик, і «перезапитати ці, але не ті» іншим способом не виражається.
 */
export async function invalidateSlices(client: QueryClient, scope: SliceScope): Promise<void> {
  const ids = tablesInScope(client, scope);

  const inScope = (query: Query): boolean => {
    const address = sliceAddressOf(query.queryKey);
    if (address === null) return false;
    if (address.periodKey !== scope.periodKey) return false;

    return ids === null || ids.has(address.tableInstanceId);
  };

  await client.invalidateQueries({ predicate: inScope });

  await client.invalidateQueries({
    predicate: (query) => isSliceKey(query.queryKey) && !inScope(query),
    refetchType: 'none',
  });
}

/**
 * Позначає застарілими ВСІ зрізи, не запитуючи жодного.
 *
 * ⚠ Для намірів, ширших за період: перерахунок усього проєкту (`periodKey:
 * null` у запиті) зачіпає будь-який документ будь-якого періоду, і чесного
 * звуження тут немає. Але й перезапитувати нема чого: екран, з якого його
 * ставлять, — адміністративний, жодної сітки на ньому не змонтовано, а ті, що
 * змонтують потім, прочитають свіже самі.
 */
export async function markSlicesStale(client: QueryClient): Promise<void> {
  await client.invalidateQueries({ queryKey: queryKeys.slices.all(), refetchType: 'none' });
}
