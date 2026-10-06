import type { QueryClient, QueryKey } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type {
  DocumentSummary,
  DocumentTableDto,
  RegistryDefinitionDto,
  TemplateCard,
  TemplatePage,
  TemplateVersionPage,
} from '@/api/types';
import { templateCardKey } from '@/features/templates/templateCardQuery';
import { localized } from '@/shared/i18n/localized';
import type { RouteHandle } from './routes';

/**
 * Резолвер динамічних крихт breadcrumbs (`PR nav-arch #3`).
 *
 * ⛔ **Найважливіша умова цього файла**: жодна функція тут НЕ викликає
 * `useQuery`/`apiFetch`/`fetch` — лише `queryClient.getQueryData` й
 * `queryClient.getQueryState`, тобто читання того, що вже лежить у кеші
 * TanStack Query, накопиченому чужими сторінками (переліком шаблонів,
 * переліком версій шаблону, конструктором довідника). Директива прямо
 * забороняє breadcrumbs власні мережеві запити чи водоспад запитів на
 * кожному рівні вкладеності — резолвер, що зробив би `useQuery` тут,
 * порушив би саме цю вимогу, і жоден `tsc`/лінт цього не впіймав би (сигнатура
 * функції нижче однаково повертає `string | undefined`).
 *
 * **Чому кеш може бути порожнім, і це нормально.** `templates.list()` й
 * `templates.versionsOf(templateId)` наповнюються `TemplatesPage` (перелік) —
 * НЕ сторінкою версії/зв'язків, куди веде ця крихта. Людина, що прийшла на
 * `/admin/templates/1/versions/1` через перелік (звичайний потік), застає
 * кеш теплим — ім'я резолвиться миттєво. Людина, що вставила посилання на
 * глибокий маршрут напряму (кеш холодний, жоден запит на цей ключ навіть не
 * летить), НІКОЛИ не побачить резолвлену назву — і це свідомий компроміс
 * «нуль нових запитів» ціною цього рідкісного випадку; резолвер відрізняє
 * «запит іде» (`fetchStatus: 'fetching'` → скелет, дані `Reason` іще
 * приїдуть) від «запиту взагалі нема і не буде» (→ статичний `labelKey`,
 * не вічний скелет і не сирий `:id`) саме через {@link resolveCrumbValue}.
 */

/** Домени, які вміє резолвити ця версія breadcrumbs. */
type CrumbResolverId = NonNullable<RouteHandle['crumb']>['resolveWith'];

/** Значення параметрів поточного матчу (`useParams()`), як рядки чи `undefined`. */
export type CrumbParams = Readonly<Record<string, string | undefined>>;

interface ResolverLookup {
  /** Ключ кешу, який треба прочитати; `undefined` — параметр невалідний, резолв неможливий. */
  key: QueryKey | undefined;
  /** Дістає людиночитний рядок із даних кешу; порожній/`undefined` — вважається "нема значення". */
  read: (data: unknown) => string | undefined;
  /**
   * Друге джерело того самого значення, якщо в першому його немає.
   *
   * ⛔ Назва шаблону жила лише в кеші ПЕРЕЛІКУ шаблонів: з переліку крихта
   * показувала `GEN99819007`, а пряме посилання чи оновлення сторінки версії —
   * «Templates / Templates / 1.0.0.0» (повторний прохід UI 2026-09-24).
   * Картку шаблону тепер вантажить `TemplateVersionLayout`, і крихта читає її.
   */
  fallback?: ResolverLookup;
}

function templateNameLookup(params: CrumbParams): ResolverLookup {
  const templateId = Number(params['id']);
  if (!Number.isFinite(templateId)) return { key: undefined, read: () => undefined };

  return {
    key: queryKeys.templates.list(),
    read: (data) => (data as TemplatePage).items.find((item) => item.id === templateId)?.code,
    fallback: {
      key: templateCardKey(templateId),
      read: (data) => (data as TemplateCard).code,
    },
  };
}

function templateVersionLabelLookup(params: CrumbParams): ResolverLookup {
  const templateId = Number(params['id']);
  const versionId = Number(params['versionId']);
  if (!Number.isFinite(templateId) || !Number.isFinite(versionId)) {
    return { key: undefined, read: () => undefined };
  }

  return {
    // ⚠ НЕ `queryKeys.templates.version(versionId)` — той ключ кешує
    // `TemplateStructureDto` (аркуші/презентація), у якому НЕМА людського
    // номера версії взагалі (`schema.d.ts`: лише `templateVersionId`,
    // `isEditable`, `presentationRevision`, `sheets`). Номер версії
    // (`TemplateVersionSummary.version`, напр. "1.0") живе в переліку версій
    // ШАБЛОНУ — тому й ключ саме `versionsOf`, а не `version`.
    key: queryKeys.templates.versionsOf(templateId),
    read: (data) => (data as TemplateVersionPage).items.find((item) => item.id === versionId)?.version,
  };
}

function registryNameLookup(params: CrumbParams): ResolverLookup {
  const code = params['code'];
  if (code === undefined || code.length === 0) return { key: undefined, read: () => undefined };

  return {
    key: queryKeys.registries.definition(code),
    read: (data) => {
      const name = localized((data as RegistryDefinitionDto).nameL10n);
      return name.length > 0 ? name : undefined;
    },
  };
}

const lookups: Record<Exclude<NonNullable<CrumbResolverId>, 'documentKey'>, (params: CrumbParams) => ResolverLookup> = {
  templateName: templateNameLookup,
  templateVersionLabel: templateVersionLabelLookup,
  registryName: registryNameLookup,
};

/**
 * Параметри адреси (`?sheet=`, `?periodKey=`) у {@link CrumbParams} — з цим
 * префіксом, щоб не зіткнутися з `:name` шляху (`fillParams` їх не бачить).
 */
export const SearchParamPrefix = '?';

/**
 * UI-32: дані сторінки документа з кешу — за ПРЕФІКСОМ ключа (`['document', id]`,
 * `['document-tables', id]`), бо третій елемент ключа — період, а період за
 * замовчуванням рахує сама сторінка. Перевага — запис саме для `?periodKey=`
 * з адреси; інакше — найсвіжіший. Лише читання кешу (`findAll`), без запиту.
 */
function documentQueryData(
  queryClient: QueryClient,
  domain: 'document' | 'document-tables',
  params: CrumbParams,
): { data: unknown; fetching: boolean } {
  const documentId = Number(params['id']);
  if (!Number.isFinite(documentId)) return { data: undefined, fetching: false };

  const periodKey = Number(params[`${SearchParamPrefix}periodKey`]);
  const queries = queryClient.getQueryCache().findAll({ queryKey: [domain, documentId] });
  // ⛔ Лише ключ сторінки `[домен, id, періодKey]`: під тим самим префіксом живуть і
  // `['document', id, period, 'workflow-history']`, `['document', id, 'migrate-version-targets']`
  // — свіжіші, але без бізнес-ключа (знайдено живим прогоном: крихта лишалась «Documents»).
  const withData = queries
    .filter((query) => query.queryKey.length === 3 && typeof query.queryKey[2] === 'number')
    .filter((query) => query.state.data !== undefined)
    .sort((a, b) => b.state.dataUpdatedAt - a.state.dataUpdatedAt);
  const chosen = withData.find((query) => query.queryKey[2] === periodKey) ?? withData[0];

  return {
    data: chosen?.state.data,
    fetching: queries.some((query) => query.state.fetchStatus === 'fetching'),
  };
}

function documentKeyResolution(queryClient: QueryClient, params: CrumbParams): CrumbResolution {
  const { data, fetching } = documentQueryData(queryClient, 'document', params);
  const businessKey = (data as DocumentSummary | undefined)?.businessKey;
  if (businessKey !== undefined && businessKey.length > 0) return { status: 'resolved', text: businessKey };

  return fetching ? { status: 'loading' } : { status: 'unavailable' };
}

/**
 * UI-32: назва аркуша документа, на якому стоїть сторінка, — той самий вибір,
 * що в `DocumentPage` (`?sheet=`, інакше перший за порядком).
 *
 * ⛔ P1 прихованих аркушів: назва береться ЛИШЕ з переліку таблиць, який
 * повернув сервер (`GET /documents/{id}/tables` прихованих аркушів не віддає).
 * Код з адреси, якого в переліку немає, назвою не стає — як і на сторінці.
 */
export function resolveDocumentSheet(queryClient: QueryClient, params: CrumbParams): CrumbResolution {
  const { data, fetching } = documentQueryData(queryClient, 'document-tables', params);
  const tables = Array.isArray(data) ? (data as DocumentTableDto[]) : [];
  if (tables.length === 0) return fetching ? { status: 'loading' } : { status: 'unavailable' };

  const wanted = params[`${SearchParamPrefix}sheet`];
  const sheet =
    tables.find((table) => table.sheetCode === wanted) ??
    [...tables].sort((a, b) => a.sheetOrdinal - b.sheetOrdinal)[0]!;
  const name = localized(sheet.sheetNameL10n);

  return { status: 'resolved', text: name.length > 0 ? name : sheet.sheetCode };
}

/** Підсумок спроби резолву однієї динамічної крихти. */
type CrumbResolution =
  | { status: 'resolved'; text: string }
  // ⚠ Запит на цей ключ ще виконується (`fetchStatus: 'fetching'`) — крихта
  // покаже вузький `Skeleton`, доки він не завершиться.
  | { status: 'loading' }
  // ⚠ Ключа кешу нема, і жоден запит на нього не йде (холодний кеш, прямий
  // перехід за посиланням) АБО параметр матчу невалідний — крихта покаже
  // статичний `labelKey`, а не вічний скелет і не сирий `:id`/GUID.
  | { status: 'unavailable' };

/**
 * Резолвить одну динамічну крихту з кешу TanStack Query — БЕЗ жодного
 * запиту. `queryClient.getQueryData`/`getQueryState` — єдине, що ця функція
 * робить із `queryClient`.
 */
export function resolveCrumbValue(
  queryClient: QueryClient,
  resolveWith: CrumbResolverId,
  params: CrumbParams,
): CrumbResolution {
  if (resolveWith === undefined) return { status: 'unavailable' };
  if (resolveWith === 'documentKey') return documentKeyResolution(queryClient, params);

  let loading = false;

  for (let lookup: ResolverLookup | undefined = lookups[resolveWith](params); lookup; lookup = lookup.fallback) {
    const { key, read } = lookup;
    if (key === undefined) continue;

    const data = queryClient.getQueryData(key);
    const value = data === undefined ? undefined : read(data);
    if (value !== undefined && value.length > 0) return { status: 'resolved', text: value };

    loading ||= queryClient.getQueryState(key)?.fetchStatus === 'fetching';
  }

  return loading ? { status: 'loading' } : { status: 'unavailable' };
}
