import { EcrApiError, apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * Позиція каталогу джерела: елемент ієрархії AF або його атрибут (`ФВ-13.13`).
 *
 * ⚠ Псевдонім згенерованого типу, а не власне оголошення (`D-137`): поле, яке
 * сервер додасть завтра, має з'явитися тут само, а не розійтися мовчки.
 */
export type SourceCatalogItem = components['schemas']['SourceCatalogItem'];

/** Сторінка каталогу; `nextCursor === null` — сторінка остання. */
export type SourceCatalogPage = components['schemas']['SourceCatalogPage'];

/**
 * Розмір сторінки, який просить клієнт.
 *
 * ⚠ Те саме число, що й серверний `DefaultLimit`, і просимо ми його ЯВНО.
 * Сервер підставить своє, якщо межі немає, — але тоді зміна його дефолту
 * мовчки змінила б і крок кнопки «показати ще», тобто те, що бачить людина.
 */
export const CatalogPageLimit = 50;

/**
 * Один рівень каталогу.
 *
 * ⛔ Усі три поля ОБОВ'ЯЗКОВІ й приймають «порожньо» явним `null`. Під
 * `exactOptionalPropertyTypes` необов'язкове поле і поле зі значенням
 * `undefined` — різні речі, і саме на цій різниці загубився б `path`: виклик
 * без нього віддає КОРЕНІ, а не дочірні вузли, і помилка виглядала б як
 * «джерело не віддає дітей», а не як дефект клієнта.
 */
export interface CatalogQuery {
  /** Шлях батьківського елемента; `null` — кореневий рівень. */
  readonly path: string | null;

  /** Рядок пошуку; порожній — без фільтра. */
  readonly search: string;

  /** Курсор із попередньої сторінки; `null` — перша сторінка. */
  readonly cursor: string | null;
}

/**
 * Читає сторінку каталогу імен джерела.
 *
 * ⛔ Адреса записана ПОВНІСТЮ одним літералом — той самий прийом, що в
 * `dataSourceApi.ts`: сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі`
 * шукає літерал `/api/v1/…`, і зібрана з частин адреса для нього не існує.
 */
export function fetchSourceCatalog(
  dataSourceId: number,
  query: CatalogQuery,
): Promise<SourceCatalogPage> {
  const params = new URLSearchParams({ limit: String(CatalogPageLimit) });

  if (query.path !== null && query.path.length > 0) params.set('path', query.path);
  if (query.search.length > 0) params.set('search', query.search);
  if (query.cursor !== null && query.cursor.length > 0) params.set('cursor', query.cursor);

  return apiFetch<SourceCatalogPage>(
    `/api/v1/data-sources/${dataSourceId}/catalog?${params.toString()}`,
  );
}

/** Право, без якого каталог не читається (те саме, що вимагає сервер). */
export const CatalogPermission = 'Integration.Manage';

/** «Джерело недоступне або не відповіло вчасно» — той самий код, що в `ErrorCodes.cs`. */
export const SourceUnavailableCode = 'ECR-INT-0503';

/** «Джерело відмовило в автентифікації» — той самий код, що в `ErrorCodes.cs`. */
export const SourceAuthRefusedCode = 'ECR-INT-0502';

/**
 * Чи це відмова, за яку відповідає ДЖЕРЕЛО, а не форма.
 *
 * ⛔ Різниця не косметична. `503`/`502` означають «чужа система зараз мовчить»:
 * виправити це в полі неможливо, і показ такої відмови під полем шляху сказав
 * би людині «ти ввела не те». Правильна відповідь — окремий стан із причиною і
 * кнопкою «повторити», а решта форми лишається робочою: шлях можна вписати
 * руками, і мапінг завести теж.
 *
 * ⚠ Гілка за КОДОМ, а не за статусом: `503` від проксі чи шлюзу не несе нашого
 * коду і є звичайним збоєм запиту (`ErrorAlert` із кореляцією).
 */
export function isSourceOutage(error: unknown): boolean {
  if (!(error instanceof EcrApiError)) return false;

  const code = error.problem.errorCode;

  return code === SourceUnavailableCode || code === SourceAuthRefusedCode;
}

/** Чи позиція каталогу — атрибут, тобто кінцевий вузол, який і мапиться. */
export function isAttribute(item: SourceCatalogItem): boolean {
  return item.kind === 'Attribute';
}

/** Підпис позиції: опис із джерела, а якщо його немає — ім'я. */
export function catalogLabel(item: SourceCatalogItem): string {
  return item.displayName !== null && item.displayName.length > 0 ? item.displayName : item.code;
}

/**
 * Що саме підставляється в поле джерельного шляху мапінгу.
 *
 * ⚠ Припущення (названо в описі PR): у форму йде ПОВНИЙ шлях, а не саме ім'я.
 * `MappingPreview.unmappedSourceFields` порівнює `sourcePath`, тобто розриви
 * рахуються за шляхом; підставлене коротке ім'я збіглося б із полем джерела
 * лише випадково. Ім'я лишається запасним варіантом на випадок, коли джерело
 * шляху не назвало.
 */
export function catalogValueOf(item: SourceCatalogItem): string {
  return item.path !== null && item.path.length > 0 ? item.path : item.code;
}
