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
const SliceStaleTime = 5 * 60_000;

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
    // ⛔ Perf: `replaceEqualDeep` (structural sharing, query-core 5.62.9) робить `keys.includes(key)` на кожен ключ
    // плоскої мапи, тобто O(n²): `cellPermissions` ~77 тис. ключів на аркуші Land — 7–25 с заморозки головного
    // потоку на КОЖЕН перезапит зрізу (і ~150 мс на кожен `setQueryData` у `onMutate`). Зріз і так замінюється
    // цілком, а посилальну стабільність частин (`cellPermissions` тощо) тримає `sliceApply`/`applyStylePatches`
    // через spread, тож порівнювати вміст нема навіщо. Діє і на `setQueryData` (опції запиту).
    structuralSharing: false,
  });
}

/** Куди дісталася зміна. */
interface SliceScope {
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
function tablesInScope(client: QueryClient, scope: SliceScope): ReadonlySet<number> | null {
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
  await invalidateScopes(client, [scope]);
}

/**
 * L3: перерахунок документа пише в КІЛЬКА періодів (задача формул одна на документ) - усі вони в наміру одним
 * проходом. ⚠ Не цикл по `invalidateSlices`: другий виклик позначав би зрізи першого періоду «застарілими без
 * перезапиту» (вони поза ЙОГО наміром) і перетирав би щойно початий перезапит.
 */
export async function invalidateSlicesForPeriods(
  client: QueryClient,
  documentId: number,
  periodKeys: readonly number[],
): Promise<void> {
  await invalidateScopes(
    client,
    [...new Set(periodKeys)].map((periodKey) => ({ documentId, periodKey })),
  );
}

async function invalidateScopes(client: QueryClient, scopes: readonly SliceScope[]): Promise<void> {
  const idsByPeriod = new Map<number, ReadonlySet<number> | null>();
  for (const scope of scopes) idsByPeriod.set(scope.periodKey, tablesInScope(client, scope));

  const inScope = (query: Query): boolean => {
    const address = sliceAddressOf(query.queryKey);
    if (address === null) return false;
    const ids = idsByPeriod.get(address.periodKey);
    if (ids === undefined) return false;

    return ids === null || ids.has(address.tableInstanceId);
  };

  // C1-01: у межах наміру перезапитуються лише ВИДИМІ зрізи. Приховані сітки режиму «одна
  // таблиця» (`display: none` у `SheetTables`) лише позначаються застарілими й дочитуються самі,
  // коли їх знову покажуть (`DocumentGrid`, ефект на `hidden`).
  await client.invalidateQueries({ predicate: (query) => inScope(query) && !isHiddenSlice(query) });

  // X2-05: прихований зріз, чию інвалідацію ПРОПУЩЕНО (перерахунок, імпорт) — єдиний, що
  // його треба перечитати при показі; власне збереження теж лишає `isInvalidated`.
  recordMissedWhileHidden(client);

  await client.invalidateQueries({
    predicate: (query) => isSliceKey(query.queryKey) && (!inScope(query) || isHiddenSlice(query)),
    refetchType: 'none',
  });
}

/**
 * LIVE-REFRESH B2/B3: перечитати РІВНО названі зрізи (кнопка «Refresh» аркуша, банер «Updated by …»).
 *
 * ⚠ Ті самі ключі кешу, що тримає `DocumentGrid`: перезапит без перезавантаження сторінки. Видимий
 * змонтований зріз перезапитується; прихований (`C1-01`) лише стає застарілим і дочитується при показі
 * (`takeMissedInvalidation`); незмонтований — застарілий, прочитає свіже сам при монтуванні.
 *
 * ⚠ Незбережені правки живуть НЕ в зрізі, а в `pendingStore` — перезапит їх не чіпає; комірка, яку
 * тим часом змінив хтось інший, іде чинним правилом конфлікту (`knownVersions.ts`, AN-39/L8-20).
 */
export async function refreshSlices(
  client: QueryClient,
  periodKey: number,
  tableInstanceIds: Iterable<number>,
): Promise<void> {
  const ids = new Set(tableInstanceIds);
  if (ids.size === 0) return;

  const named = (query: Query): boolean => {
    const address = sliceAddressOf(query.queryKey);

    return address !== null && address.periodKey === periodKey && ids.has(address.tableInstanceId);
  };

  for (const query of client.getQueryCache().findAll({ predicate: (candidate) => named(candidate) && isHiddenSlice(candidate) })) {
    const address = sliceAddressOf(query.queryKey);
    if (address !== null) missedWhileHidden.add(sliceId(address.tableInstanceId, address.periodKey));
  }

  await Promise.all([
    client.invalidateQueries({ predicate: (query) => named(query) && !isHiddenSlice(query) }),
    client.invalidateQueries({ predicate: (query) => named(query) && isHiddenSlice(query), refetchType: 'none' }),
  ]);
}

/**
 * Зрізи, чиї сітки змонтовані, але приховані (`C1-01`): ключ `tableInstanceId:periodKey` →
 * скільки прихованих спостерігачів його тримає.
 *
 * ⛔ Реєстр саме ПРИХОВАНИХ, а не видимих: зріз, про який ніхто нічого не заявив (інший
 * споживач, стек «усі таблиці», тест), лишається на старій поведінці — перезапитується. Помилка в
 * реєстрації може коштувати зайвого запиту, але не застарілих даних на екрані.
 *
 * ⚠ Лічильник, а не множина: дві сітки одного зрізу (теоретично) не мають знімати позначку одна
 * за одну.
 */
const hiddenSlices = new Map<string, number>();

function sliceId(tableInstanceId: number, periodKey: number): string {
  return `${String(tableInstanceId)}:${String(periodKey)}`;
}

/**
 * Приховані зрізи, яких торкнулася ЧУЖА інвалідація (`invalidateSlices`, `markSlicesStale`) — і
 * які ще не дочитані при показі (`X2-05`).
 *
 * ⛔ `isInvalidated` зріз отримує й після КОЖНОГО власного збереження (`applyPatchLocally`
 * позначає його застарілим без запиту), тож за самою позначкою сітка, що знову стала видимою,
 * перечитувала найважчий `GET` системи, хоча нічого не пропустила. Пропущена інвалідація — лише
 * та, що пройшла тут, поки сітка була прихована.
 */
const missedWhileHidden = new Set<string>();

function recordMissedWhileHidden(client: QueryClient): void {
  for (const query of client.getQueryCache().findAll({ predicate: (candidate) => isHiddenSlice(candidate) })) {
    const address = sliceAddressOf(query.queryKey);
    if (address !== null) missedWhileHidden.add(sliceId(address.tableInstanceId, address.periodKey));
  }
}

/** Чи пропущено інвалідацію зрізу, поки сітку було приховано; читання знімає позначку. */
export function takeMissedInvalidation(tableInstanceId: number, periodKey: number): boolean {
  return missedWhileHidden.delete(sliceId(tableInstanceId, periodKey));
}

function isHiddenSlice(query: Query): boolean {
  const address = sliceAddressOf(query.queryKey);
  if (address === null) return false;

  return (hiddenSlices.get(sliceId(address.tableInstanceId, address.periodKey)) ?? 0) > 0;
}

/**
 * Позначає зріз прихованим; повертає функцію, що знімає позначку (для прибирання ефекту).
 *
 * Прихований зріз не перезапитується інвалідацією `invalidateSlices`, а лише стає застарілим.
 */
export function markSliceHidden(tableInstanceId: number, periodKey: number): () => void {
  const id = sliceId(tableInstanceId, periodKey);
  hiddenSlices.set(id, (hiddenSlices.get(id) ?? 0) + 1);

  let released = false;

  return () => {
    if (released) return;
    released = true;

    const left = (hiddenSlices.get(id) ?? 1) - 1;
    if (left > 0) hiddenSlices.set(id, left);
    else hiddenSlices.delete(id);
  };
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
  recordMissedWhileHidden(client);
  await client.invalidateQueries({ queryKey: queryKeys.slices.all(), refetchType: 'none' });
}
