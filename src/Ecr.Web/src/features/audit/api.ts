import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch, apiFetchResponse } from '@/api/client';
import type { components } from '@/api/schema';
import type { CellChangePage } from '@/api/types';

/*
 * ⛔ Адреса записана ПОВНІСТЮ і поруч із викликом `apiFetch`, а не збирається
 * з помічника. Сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає в
 * коді клієнта саме літерали `/api/v1/…` разом із функцією поруч; винесений у
 * помічник префікс зробив би дію «недосяжною з інтерфейсу» для сторожа.
 */

/** Походження зміни — ті самі чотири значення, що пише `aud.CellChange.Origin`. */
export const cellChangeOrigins = ['UserEdit', 'Import', 'Recalculation', 'Migration'] as const;

/** Календарна дата без години — рівно те, що віддає `<input type="date">`. */
const DateOnly = /^(\d{4})-(\d{2})-(\d{2})$/;

/**
 * КОНТРАКТ МЕЖІ ВІКНА. Обрано ОДИН варіант і записано тут.
 *
 * ⛔ **`From` і `To` — календарні дати, обидві ВКЛЮЧНО.** Названий у `To` день
 * належить вікну цілком. Саме так підписи «From / To» читає людина, і саме
 * тому напис не змінюється.
 *
 * ⛔ **Сервер лишається незмінним, і це не компроміс.** У сервера `from`/`to` —
 * не дати, а МИТТЄВОСТІ UTC (`ChangedAt >= @from AND ChangedAt < @to`,
 * `AuditReader`), і `to` виключне. Додати добу там (`to.AddDays(1)`) означало б
 * зіпсувати кожного, хто передає справжню миттєвість: сторінка історії однієї
 * комірки, CSV-експорт, наскрізні тести — усі вони надсилають `…T17:20:00Z`, і
 * «плюс доба» зсунула б їм вікно на добу вперед. До того ж сервер не знає
 * поясу того, хто дивиться, тож «кінець доби» на сервері не має значення.
 * Доба губилася РІВНО ТУТ — у місці, де календарна дата стає миттєвістю, а це
 * місце одне, і воно клієнтське.
 *
 * ⚠ **Пояс — браузерний, свідомо.** `ChangedAt` зберігається в UTC, а на екран
 * його друкує `Timestamp` → `Intl.DateTimeFormat` БЕЗ `timeZone`, тобто в
 * поясі браузера. Межа вікна мусить жити в тому самому поясі, у якому людина
 * читає рядки, — інакше екран ховав би рядок, надрукований час якого лежить
 * усередині набраних дат, і це був би той самий дефект, лише на годину.
 *
 * ⚠ **Чому НЕ пояс майданчика, як у `PeriodsPage`/`ArchiveJob`.** `Site time
 * zone` — властивість ПРОЄКТУ (`Project.TimeZoneId`); періоди й архівація
 * живуть усередині одного проєкту, тож у них це значення визначене. Журнал
 * аудиту наскрізний: запит без `documentId` іде по всіх проєктах, і єдиного
 * поясу майданчика для нього не існує. Третій спосіб не вигадано — узято той
 * самий принцип («межа в тому ж поясі, що й показане значення»), а він тут
 * дає пояс браузера.
 *
 * ⚠ Значення, яке НЕ є голою датою (готова миттєвість ISO), проходить як є:
 * його вже привели до миттєвості, і другий зсув зіпсував би його.
 */
function instant(value: string, plusDays: number): string {
  const parts = DateOnly.exec(value);

  if (parts === null) return value;

  // ⚠ `new Date(рік, місяць, день + 1)` сам переносить через кінець місяця й
  // року, і саме він дає ПІВНІЧ У ПОЯСІ БРАУЗЕРА. `new Date('2026-09-23')`
  // дало б північ UTC — рівно та тиха помилка на добу, яку виправляє цей код.
  const at = new Date(Number(parts[1]), Number(parts[2]) - 1, Number(parts[3]) + plusDays);

  return Number.isNaN(at.getTime()) ? value : at.toISOString();
}

/** Початок вікна: північ названої дати в поясі браузера. */
export function windowStart(value: string): string {
  return instant(value, 0);
}

/**
 * Кінець вікна: північ НАСТУПНОЇ доби в поясі браузера.
 *
 * ⛔ Плюс доба — це і є виправлення. Сервер порівнює `ChangedAt < @to`, тож
 * «`to` = північ названого дня» виключала ввесь названий день. Наслідок був
 * тихий і найгірший з можливих: вікно за замовчуванням закінчується СЬОГОДНІ,
 * дивляться журнал теж сьогодні («хто щойно це змінив») — і він відповідав
 * «змін не було», тобто неправдою.
 */
export function windowEnd(value: string): string {
  return instant(value, 1);
}

/** Фільтр журналу змін комірок. */
export interface CellChangeFilter {
  /** Початок вікна (`YYYY-MM-DD` або ISO); **обов'язковий**. Дата — ВКЛЮЧНО. */
  readonly from: string;
  /** Кінець вікна; **обов'язковий**. Дата — ВКЛЮЧНО: названий день у вікні (див. `windowEnd`). */
  readonly to: string;
  readonly documentId?: number | null;
  readonly rowKey?: string | null;
  readonly columnDefId?: number | null;
  /** Автор зміни — `UserId`, не SID. */
  readonly author?: number | null;
  readonly origin?: string | null;
  readonly lateOnly?: boolean;
  readonly limit?: number;
  readonly cursor?: string | null;
}

/**
 * Фільтр адресує РІВНО ОДНУ комірку — документ, рядок і колонка разом.
 *
 * ⚠ Це не косметика: сервер на такий запит вимагає `Document.View` замість
 * `Security.ViewAudit` і дозволяє вікно в 13 місяців замість 92 днів (`D15-16`).
 * Клієнт мусить знати різницю, щоб не показувати «вікно завелике» там, де
 * сервер його прийме.
 */
export function isSingleCell(filter: CellChangeFilter): boolean {
  return (
    filter.documentId !== null &&
    filter.documentId !== undefined &&
    filter.rowKey !== null &&
    filter.rowKey !== undefined &&
    filter.rowKey.length > 0 &&
    filter.columnDefId !== null &&
    filter.columnDefId !== undefined
  );
}

/**
 * Рядок запиту журналу.
 *
 * ⛔ Через `URLSearchParams`, а не склеюванням: `rowKey` — довільний текст із
 * шаблону, і `&`, `#` чи пробіл у ньому інакше обірвали б адресу. Саме на
 * незакодованому `#` уже падав `smoke.ps1` (крок 17, CLAUDE.md).
 *
 * ⚠ Порожні значення НЕ потрапляють у запит зовсім. `?rowKey=` сервер трактує
 * як відсутній фільтр, але адреса з порожніми хвостами накопичує їх і стає
 * нечитабельною — а саме її людина надсилає колезі.
 */
export function cellChangesQuery(filter: CellChangeFilter): string {
  const params = new URLSearchParams();

  params.set('from', windowStart(filter.from));
  params.set('to', windowEnd(filter.to));
  params.set('limit', String(filter.limit ?? 100));

  if (filter.documentId !== null && filter.documentId !== undefined) {
    params.set('documentId', String(filter.documentId));
  }

  if (filter.rowKey !== null && filter.rowKey !== undefined && filter.rowKey.length > 0) {
    params.set('rowKey', filter.rowKey);
  }

  if (filter.columnDefId !== null && filter.columnDefId !== undefined) {
    params.set('columnDefId', String(filter.columnDefId));
  }

  if (filter.author !== null && filter.author !== undefined) {
    params.set('author', String(filter.author));
  }

  if (filter.origin !== null && filter.origin !== undefined && filter.origin.length > 0) {
    params.set('origin', filter.origin);
  }

  // ⚠ `lateOnly=false` не надсилається: булеве за замовчуванням і так `false`,
  // а параметр у адресі виглядав би як свідомо обраний фільтр.
  if (filter.lateOnly === true) {
    params.set('lateOnly', 'true');
  }

  if (filter.cursor !== null && filter.cursor !== undefined && filter.cursor.length > 0) {
    params.set('cursor', filter.cursor);
  }

  return params.toString();
}

/** Сторінка журналу структурних змін (`BE-16`). */
export type StructureChangePage = components['schemas']['PagedResultOfStructureChangeView'];

/** Фільтр журналу структурних змін; вікно **обов'язкове**, як у журналі комірок. */
export interface StructureChangeFilter {
  /** Початок вікна; дата — ВКЛЮЧНО. */
  readonly from: string;
  /** Кінець вікна; дата — ВКЛЮЧНО, як у `CellChangeFilter`. */
  readonly to: string;
  readonly entityType?: string | null;
  /** Автор зміни — `UserId`, не SID. */
  readonly changedByUserId?: number | null;
  readonly limit?: number;
  readonly cursor?: string | null;
}

/** Рядок запиту журналу структурних змін — ті самі правила, що в `cellChangesQuery`. */
export function structureChangesQuery(filter: StructureChangeFilter): string {
  const params = new URLSearchParams();

  // ⛔ Та сама межа, що в `cellChangesQuery`, і того самого помічника. Дві
  // вкладки одного екрана ділять ОДНУ пару дат; полагодити лише одну означало
  // б, що одна вкладка каже правду, а друга поруч — ні.
  params.set('from', windowStart(filter.from));
  params.set('to', windowEnd(filter.to));
  params.set('limit', String(filter.limit ?? 100));

  if (filter.entityType !== null && filter.entityType !== undefined && filter.entityType.length > 0) {
    params.set('entityType', filter.entityType);
  }

  if (filter.changedByUserId !== null && filter.changedByUserId !== undefined) {
    params.set('changedByUserId', String(filter.changedByUserId));
  }

  if (filter.cursor !== null && filter.cursor !== undefined && filter.cursor.length > 0) {
    params.set('cursor', filter.cursor);
  }

  return params.toString();
}

/**
 * Рядок запиту CSV-експорту: ті самі фільтри, що в переліку, але БЕЗ `limit` і
 * `cursor` — експорт віддає всю видачу фільтра, а не сторінку.
 */
export function structureExportQuery(filter: StructureChangeFilter): string {
  const params = new URLSearchParams(structureChangesQuery({ ...filter, cursor: null }));
  params.delete('limit');

  return params.toString();
}

/** Завантажений CSV: тіло й ім'я, яке запропонував сервер (або `null`). */
export interface StructureExportFile {
  readonly blob: Blob;
  readonly fileName: string | null;
}

/**
 * CSV журналу структурних змін (`BE-16`).
 *
 * ⛔ `fetch` → blob, а не посилання: за посиланням браузер показав би відмову
 * (`422`, `403`) сирим JSON на порожній сторінці. Тут вона стає `EcrApiError`
 * і показується `ErrorAlert`-ом із текстом сервера.
 *
 * ⚠ `method: 'GET'` названо явно: сторож `EndpointCoverageTests` виводить
 * метод із назви функції, а `apiFetchResponse` у його переліку немає.
 */
export async function fetchStructureExport(filter: StructureChangeFilter): Promise<StructureExportFile> {
  const query = structureExportQuery(filter);
  const response = await apiFetchResponse(`/api/v1/audit/structure/export.csv?${query}`, {
    method: 'GET',
  });

  return {
    blob: await response.blob(),
    fileName: fileNameOf(response.headers.get('Content-Disposition')),
  };
}

/**
 * Ім'я файлу з `Content-Disposition`; `filename*` (RFC 5987) має перевагу.
 *
 * ⚠ Шлях і керівні символи відкидаються: ім'я прийшло ззовні, а `download`
 * зі слешем браузери трактують по-різному.
 */
export function fileNameOf(disposition: string | null): string | null {
  if (disposition === null) return null;

  const extended = /filename\*\s*=\s*[^']*'[^']*'([^;]+)/i.exec(disposition);
  let name: string | null = null;

  if (extended?.[1] !== undefined) {
    try {
      name = decodeURIComponent(extended[1].trim());
    } catch {
      name = null;
    }
  }

  if (name === null) {
    const plain = /filename\s*=\s*("([^"]*)"|[^;]+)/i.exec(disposition);
    name = (plain?.[2] ?? plain?.[1])?.trim() ?? null;
  }

  if (name === null) return null;

  const safe = name.replace(/[\\/\p{Cc}]/gu, '_').trim();

  return safe.length > 0 ? safe : null;
}

/** Сторінка загального журналу структурних змін (`BE-16`). */
export function useStructureChanges(
  filter: StructureChangeFilter,
): UseQueryResult<StructureChangePage> {
  const query = structureChangesQuery(filter);

  return useQuery({
    queryKey: ['audit-structure', query],
    queryFn: () => apiFetch<StructureChangePage>(`/api/v1/audit/structure?${query}`),
  });
}

/**
 * Сторінка журналу змін комірок.
 *
 * ⛔ Вікно `from`/`to` — обов'язкове, і хук не вміє його не передати:
 * `aud.CellChange` партиційована за `ChangedAt`, і запит без меж пішов би по
 * ВСІХ партиціях, включно з архівними. Це не оптимізація — це різниця між
 * «повільно» і «сервер зайнятий».
 *
 * ⚠ Той самий хук обслуговує і вкладку History інспектора комірки: історія
 * однієї комірки — це фільтр `documentId + rowKey + columnDefId`, а не окремий
 * маршрут (див. `isSingleCell`).
 */
export function useCellChanges(
  filter: CellChangeFilter,
  enabled = true,
): UseQueryResult<CellChangePage> {
  const query = cellChangesQuery(filter);

  return useQuery({
    enabled,
    queryKey: ['audit-cells', query],
    queryFn: () => apiFetch<CellChangePage>(`/api/v1/audit/cells?${query}`),
  });
}
