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

// ✎ Бюджет D-132: політика кешу зрізів — у `./sliceCachePolicy` (її ставить `app/queryClient`, тобто вхідний чанк),
// щоб решта цього модуля не їхала в кожен маршрут. Реекспорт — для наявних імпортів.
export { applySliceCachePolicy } from './sliceCachePolicy';

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

/**
 * PERF-09: стан «одного польоту» інвалідації на клієнт запитів.
 *
 * ⛔ Що виправляється. `invalidateQueries` за замовчуванням має `cancelRefetch: true`: кожен новий виклик ОБРИВАЄ
 * запит зрізу, що вже летить, і шле такий самий знову. Сервер уже почав працювати над обірваним (≈ 20 послідовних
 * звернень до БД на зріз), тож N подій поспіль (завершення задачі, чужий перехід статусу, перерахунок, імпорт)
 * коштують серверу N повних зрізів на кожну видиму таблицю, а клієнту — жодного зайвого результату.
 *
 * ⚠ Тепер: поки перезапит летить, нові наміри лише ЗАПИСУЮТЬСЯ, а після його завершення виконуються ОДНИМ
 * об'єднаним проходом. Тобто ≤ 2 запити на зріз на серію подій, і жодного відповіді «до події» під виглядом
 * свіжої: проход, який стартував ПІСЛЯ події, бачить її наслідки.
 */
interface Flight {
  running: boolean;
  queued: { scopes: SliceScope[]; waiters: { resolve: () => void; reject: (reason: unknown) => void }[] } | null;
}

const flights = new WeakMap<QueryClient, Flight>();

/** Наміри, відкладені, поки вкладка прихована: клієнт запитів → що перечитати, коли вкладка знову видима. */
const deferredWhileTabHidden = new Map<QueryClient, SliceScope[]>();

function isTabHidden(): boolean {
  return typeof document !== 'undefined' && document.visibilityState === 'hidden';
}

/**
 * PERF-09: прихована вкладка не перечитує зрізи (до 91 запиту заради екрана, якого ніхто не бачить) — усі зрізи лише
 * стають застарілими, а наміри перечитуються ОДИН раз, коли вкладка знову видима.
 */
function deferWhileTabHidden(client: QueryClient, scopes: readonly SliceScope[]): Promise<void> {
  const known = deferredWhileTabHidden.get(client);
  if (known !== undefined) {
    known.push(...scopes);
  } else {
    deferredWhileTabHidden.set(client, [...scopes]);
    const onVisible = (): void => {
      if (isTabHidden()) return;
      document.removeEventListener('visibilitychange', onVisible);
      const pending = deferredWhileTabHidden.get(client) ?? [];
      deferredWhileTabHidden.delete(client);
      void invalidateScopes(client, pending);
    };
    document.addEventListener('visibilitychange', onVisible);
  }
  recordMissedWhileHidden(client);

  return client.invalidateQueries({ predicate: (query) => isSliceKey(query.queryKey), refetchType: 'none' });
}

function invalidateScopes(client: QueryClient, scopes: readonly SliceScope[]): Promise<void> {
  if (isTabHidden()) return deferWhileTabHidden(client, scopes);

  let flight = flights.get(client);
  if (flight === undefined) {
    flight = { running: false, queued: null };
    flights.set(client, flight);
  }

  const current = flight;
  if (current.running) {
    const queued = (current.queued ??= { scopes: [], waiters: [] });
    queued.scopes.push(...scopes);

    return new Promise<void>((resolve, reject) => queued.waiters.push({ resolve, reject }));
  }

  current.running = true;

  return runInvalidate(client, scopes).finally(() => void drainFlight(client, current));
}

/** Виконує накопичені за час польоту наміри одним проходом; повторює, доки нові не припиняться. */
async function drainFlight(client: QueryClient, flight: Flight): Promise<void> {
  while (flight.queued !== null) {
    const queued = flight.queued;
    flight.queued = null;
    try {
      await runInvalidate(client, queued.scopes);
      for (const waiter of queued.waiters) waiter.resolve();
    } catch (error) {
      for (const waiter of queued.waiters) waiter.reject(error);
    }
  }
  flight.running = false;
}

async function runInvalidate(client: QueryClient, scopes: readonly SliceScope[]): Promise<void> {
  // ⚠ Наміри з різних документів/аркушів одного періоду НЕ зливаються за періодом: кожен має свій перелік таблиць.
  const entries = scopes.map((scope) => ({ periodKey: scope.periodKey, ids: tablesInScope(client, scope) }));

  const inScope = (query: Query): boolean => {
    const address = sliceAddressOf(query.queryKey);
    if (address === null) return false;

    return entries.some(
      (entry) => entry.periodKey === address.periodKey && (entry.ids === null || entry.ids.has(address.tableInstanceId)),
    );
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

/**
 * PERF-08: позначає зрізи документа за період застарілими БЕЗ перезапиту.
 *
 * ⚠ Для випадку «слідкувач за задачею здався, а чи є сітка, що доведе справу, невідомо»: змонтована видима
 * сітка підхоплює задачу власним спостерігачем, а незмонтована прочитає свіже при монтуванні — на відміну від
 * `invalidateSlicesForPeriods` це не б'є по сервері 91 запитом заради задачі, яка, можливо, ще в черзі.
 */
export async function markDocumentPeriodStale(client: QueryClient, documentId: number, periodKey: number): Promise<void> {
  const ids = tablesInScope(client, { documentId, periodKey });
  recordMissedWhileHidden(client);
  await client.invalidateQueries({
    predicate: (query) => {
      const address = sliceAddressOf(query.queryKey);

      return address !== null && address.periodKey === periodKey && (ids === null || ids.has(address.tableInstanceId));
    },
    refetchType: 'none',
  });
}
