import type { QueryClient, QueryKey } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefinitionDto, TemplatePage, TemplateVersionPage } from '@/api/types';
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
export type CrumbResolverId = NonNullable<RouteHandle['crumb']>['resolveWith'];

/** Значення параметрів поточного матчу (`useParams()`), як рядки чи `undefined`. */
export type CrumbParams = Readonly<Record<string, string | undefined>>;

interface ResolverLookup {
  /** Ключ кешу, який треба прочитати; `undefined` — параметр невалідний, резолв неможливий. */
  key: QueryKey | undefined;
  /** Дістає людиночитний рядок із даних кешу; порожній/`undefined` — вважається "нема значення". */
  read: (data: unknown) => string | undefined;
}

function templateNameLookup(params: CrumbParams): ResolverLookup {
  const templateId = Number(params['id']);
  if (!Number.isFinite(templateId)) return { key: undefined, read: () => undefined };

  return {
    key: queryKeys.templates.list(),
    read: (data) => (data as TemplatePage).items.find((item) => item.id === templateId)?.code,
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

const lookups: Record<NonNullable<CrumbResolverId>, (params: CrumbParams) => ResolverLookup> = {
  templateName: templateNameLookup,
  templateVersionLabel: templateVersionLabelLookup,
  registryName: registryNameLookup,
};

/** Підсумок спроби резолву однієї динамічної крихти. */
export type CrumbResolution =
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

  const { key, read } = lookups[resolveWith](params);
  if (key === undefined) return { status: 'unavailable' };

  const data = queryClient.getQueryData(key);
  const value = data === undefined ? undefined : read(data);
  if (value !== undefined && value.length > 0) return { status: 'resolved', text: value };

  const state = queryClient.getQueryState(key);
  return state?.fetchStatus === 'fetching' ? { status: 'loading' } : { status: 'unavailable' };
}
