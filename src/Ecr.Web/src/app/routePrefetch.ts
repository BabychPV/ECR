import { lazy } from 'react';
import type { QueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { MethodologyDto, RegistryDefDto, TemplatePage as TemplatePageDto } from '@/api/types';
import { routes } from './routes';

/**
 * Реєстр лінивих сторінок і прогріву за наміром (`PR nav-arch #5`, директива C1/C2).
 *
 * ⚠ Кожна сторінка застосунку (крім `/login` і dev-маршруту "каталог
 * компонентів", які лишились у `router.tsx` — вони поза навбаром і поза
 * прогрівом за наміром) визначена РІВНО ОДИН РАЗ тут: `lazy(loader)` для
 * `router.tsx` і ТОЙ САМИЙ `loader` у {@link routeChunkLoaders} для
 * `useRoutePrefetch` — не дві незалежні функції з однаковим `import()`
 * усередині, а одна, звідки й `router.tsx`, і навбар беруть своє.
 *
 * ⛔ Файл НЕ імпортує `router.tsx`/`AppLayout`/навбар навмисно — звідси й
 * винесено ОКРЕМИМ модулем, а не додано просто як експорт у `router.tsx`.
 * `AppLayout` (через `NavRouteLink`/`useRoutePrefetch`) читає прогрів
 * маршруту, а `router.tsx` монтує `AppLayout` як елемент кореневого
 * маршруту — якби прогрів жив у `router.tsx`, вийшло б циклічне
 * `router.tsx → AppLayout → NavRouteLink → useRoutePrefetch → router.tsx`,
 * яке працює в ESM не завжди передбачувано (залежить від порядку
 * ініціалізації чанків бандлера). Цей модуль — лист залежностей
 * (`@/api/*`, `./routes`), не вузол дерева маршрутів.
 */
const DocumentsPageLoader = async () => ({ default: (await import('@/pages/DocumentsPage')).DocumentsPage });
export const DocumentsPage = lazy(DocumentsPageLoader);

const ChangePasswordPageLoader = async () => ({
  default: (await import('@/pages/ChangePasswordPage')).ChangePasswordPage,
});
export const ChangePasswordPage = lazy(ChangePasswordPageLoader);

const DocumentPageLoader = async () => ({ default: (await import('@/pages/DocumentPage')).DocumentPage });
export const DocumentPage = lazy(DocumentPageLoader);

/**
 * ⛔ Окремий маршрут, а не вкладка в навбарі (`ФВ-6.15`, перенесено з
 * `router.tsx`): роль доменних користувачів призначається на AD-групу, і
 * людина без жодного збігу бачить порожні екрани, які не відрізняються від
 * справної системи без даних (`H-21`).
 */
const MyGroupsPageLoader = async () => ({ default: (await import('@/pages/MyGroupsPage')).MyGroupsPage });
export const MyGroupsPage = lazy(MyGroupsPageLoader);

const TemplatesPageLoader = async () => ({
  default: (await import('@/pages/admin/TemplatesPage')).TemplatesPage,
});
export const TemplatesPage = lazy(TemplatesPageLoader);

const TemplateVersionPageLoader = async () => ({
  default: (await import('@/pages/admin/TemplateVersionPage')).TemplateVersionPage,
});
export const TemplateVersionPage = lazy(TemplateVersionPageLoader);

const TableRelationsPageLoader = async () => ({
  default: (await import('@/pages/admin/TableRelationsPage')).TableRelationsPage,
});
export const TableRelationsPage = lazy(TableRelationsPageLoader);

const RegistriesPageLoader = async () => ({
  default: (await import('@/pages/admin/RegistriesPage')).RegistriesPage,
});
export const RegistriesPage = lazy(RegistriesPageLoader);

const RegistryConstructorPageLoader = async () => ({
  default: (await import('@/pages/admin/RegistryConstructorPage')).RegistryConstructorPage,
});
export const RegistryConstructorPage = lazy(RegistryConstructorPageLoader);

const MethodologiesPageLoader = async () => ({
  default: (await import('@/pages/admin/MethodologiesPage')).MethodologiesPage,
});
export const MethodologiesPage = lazy(MethodologiesPageLoader);

const MethodologyVersionsPageLoader = async () => ({
  default: (await import('@/pages/admin/MethodologyVersionsPage')).MethodologyVersionsPage,
});
export const MethodologyVersionsPage = lazy(MethodologyVersionsPageLoader);

const ExpressionsPageLoader = async () => ({
  default: (await import('@/pages/admin/ExpressionsPage')).ExpressionsPage,
});
export const ExpressionsPage = lazy(ExpressionsPageLoader);

const SecurityPageLoader = async () => ({ default: (await import('@/pages/admin/SecurityPage')).SecurityPage });
export const SecurityPage = lazy(SecurityPageLoader);

const PeriodsPageLoader = async () => ({ default: (await import('@/pages/admin/PeriodsPage')).PeriodsPage });
export const PeriodsPage = lazy(PeriodsPageLoader);

const SourcesPageLoader = async () => ({ default: (await import('@/pages/admin/SourcesPage')).SourcesPage });
export const SourcesPage = lazy(SourcesPageLoader);

const MappingPreviewPageLoader = async () => ({
  default: (await import('@/pages/admin/MappingPreviewPage')).MappingPreviewPage,
});
export const MappingPreviewPage = lazy(MappingPreviewPageLoader);

const JobsPageLoader = async () => ({ default: (await import('@/pages/admin/JobsPage')).JobsPage });
export const JobsPage = lazy(JobsPageLoader);

const SnapshotsPageLoader = async () => ({
  default: (await import('@/pages/admin/SnapshotsPage')).SnapshotsPage,
});
export const SnapshotsPage = lazy(SnapshotsPageLoader);

const AuditPageLoader = async () => ({ default: (await import('@/pages/admin/AuditPage')).AuditPage });
export const AuditPage = lazy(AuditPageLoader);

const UnitsPageLoader = async () => ({ default: (await import('@/pages/admin/UnitsPage')).UnitsPage });
export const UnitsPage = lazy(UnitsPageLoader);

const UiStringsPageLoader = async () => ({
  default: (await import('@/pages/admin/UiStringsPage')).UiStringsPage,
});
export const UiStringsPage = lazy(UiStringsPageLoader);

const HealthPageLoader = async () => ({ default: (await import('@/pages/admin/HealthPage')).HealthPage });
export const HealthPage = lazy(HealthPageLoader);

/**
 * Прогрів ЧАНКА маршруту за наміром — виклик того самого завантажувача, що
 * й аргумент `lazy()` вище. Виклик поза рендером НЕ рендерить компонент —
 * лише запускає `import()`, який браузер/Vite кешує за специфікатором
 * модуля: коли `React.lazy` реально монтує компонент після кліку, той самий
 * `import()` резолвиться з уже теплого кешу, а не з мережі.
 *
 * ⚠ Ключ реєстру — `RouteEntry.id` (`routes.ts`), той самий ідентифікатор,
 * яким оперують гарди (`RouteGuard`) і breadcrumbs (`ancestorIds`) — не шлях
 * і не назва компонента: третій незалежний спосіб адресувати той самий
 * маршрут тут був би зайвим.
 */
const routeChunkLoaders: Partial<Record<string, () => Promise<unknown>>> = {
  [routes.home.id]: DocumentsPageLoader,
  [routes.changePassword.id]: ChangePasswordPageLoader,
  [routes.myGroups.id]: MyGroupsPageLoader,
  [routes.documentDetail.id]: DocumentPageLoader,
  [routes.adminTemplates.id]: TemplatesPageLoader,
  [routes.adminTemplateVersion.id]: TemplateVersionPageLoader,
  [routes.adminTemplateVersionRelations.id]: TableRelationsPageLoader,
  [routes.adminRegistries.id]: RegistriesPageLoader,
  [routes.adminRegistryDefinition.id]: RegistryConstructorPageLoader,
  [routes.adminMethodologies.id]: MethodologiesPageLoader,
  [routes.adminMethodologyVersions.id]: MethodologyVersionsPageLoader,
  [routes.adminExpressions.id]: ExpressionsPageLoader,
  [routes.adminSecurity.id]: SecurityPageLoader,
  [routes.adminPeriods.id]: PeriodsPageLoader,
  [routes.adminSources.id]: SourcesPageLoader,
  [routes.adminMapping.id]: MappingPreviewPageLoader,
  [routes.adminJobs.id]: JobsPageLoader,
  [routes.adminSnapshots.id]: SnapshotsPageLoader,
  [routes.adminAudit.id]: AuditPageLoader,
  [routes.adminUnits.id]: UnitsPageLoader,
  [routes.adminUiStrings.id]: UiStringsPageLoader,
  [routes.adminHealth.id]: HealthPageLoader,
};

/**
 * Прогрів ДАНИХ за наміром — `queryClient.prefetchQuery` РІВНО з тим самим
 * ключем і `queryFn`, які власна сторінка передає у свій `useQuery` (звірено
 * рядок у рядок із `TemplatesPage.tsx`/`RegistriesPage.tsx`/
 * `MethodologiesPage.tsx`/`ExpressionsPage.tsx` на момент цієї картки).
 *
 * ⛔ Не КОЖЕН пункт навбару тут — лише ті чотири, чия ПЕРША сторінкова
 * `useQuery` уже читає ключ із фабрики (`@/api/queryKeys`, `PR #1`).
 * Директива (C2) явно вимагає той самий ключ, що й цільова сторінка, і явно
 * забороняє вигадувати новий — сторінки з РЯДКОВИМ літералом замість
 * фабричного ключа (`['units']`, `['jobs']`, `['sources']`, `['roles']`,
 * `['projects']`, `['my-groups']`, `['documents', periodKey, cursor]`…)
 * лишаються з прогрівом ЛИШЕ чанка (`routeChunkLoaders` вище їх усе одно
 * покриває): додати їм ключ фабрики "по-мінімуму" заради самого прогріву —
 * це вже редагування семи-восьми сторінкових файлів, яких немає в межах
 * цієї картки (`CLAUDE.md` цієї директиви), а вгадати рядковий літерал
 * СТАРОГО ключа окремо від сторінки — це і є та розбіжність написання,
 * заради якої існує сама фабрика (`queryKeys.ts`, коментар угорі файла).
 * Названо явно в Q-281, не замовчано.
 */
const routeDataPrefetchers: Partial<Record<string, (queryClient: QueryClient) => void>> = {
  [routes.adminTemplates.id]: (queryClient) => {
    void queryClient.prefetchQuery({
      queryKey: queryKeys.templates.list(),
      queryFn: () => apiFetch<TemplatePageDto>('/api/v1/templates?limit=100'),
    });
  },
  [routes.adminRegistries.id]: (queryClient) => {
    void queryClient.prefetchQuery({
      queryKey: queryKeys.registries.list(),
      queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
    });
  },
  [routes.adminMethodologies.id]: (queryClient) => {
    void queryClient.prefetchQuery({
      queryKey: queryKeys.methodologies.list(),
      queryFn: () => apiFetch<MethodologyDto[]>('/api/v1/methodologies'),
    });
  },
  // ⚠ `ExpressionsPage` сама вмикає лише ОДИН із двох запитів одразу
  // (`enabled: dialect === 'Template' | 'Methodology'`) — але обидва ключі
  // вже фабричні (`templates.list()`/`methodologies.list()`, ті самі, що й
  // прогріті для `adminTemplates`/`adminMethodologies` вище), і прогріти
  // обидва тут дешевше й безпечніше, ніж читати внутрішній стан сторінки
  // (`dialect`) ззовні, аби вгадати, який саме буде активний.
  [routes.adminExpressions.id]: (queryClient) => {
    void queryClient.prefetchQuery({
      queryKey: queryKeys.templates.list(),
      queryFn: () => apiFetch<TemplatePageDto>('/api/v1/templates?limit=100'),
    });
    void queryClient.prefetchQuery({
      queryKey: queryKeys.methodologies.list(),
      queryFn: () => apiFetch<MethodologyDto[]>('/api/v1/methodologies'),
    });
  },
};

/**
 * Прогріває чанк і (де є фабричний ключ) дані ОДНОГО маршруту за наміром.
 * Викликається обробниками наведення/фокусу (`useRoutePrefetch.ts`), не
 * рендером — сам виклик нічого не малює.
 */
export function prefetchRoute(routeId: string, queryClient: QueryClient): void {
  void routeChunkLoaders[routeId]?.();
  routeDataPrefetchers[routeId]?.(queryClient);
}

/** Лише для тестів (`routePrefetch.test.ts`) — які маршрути мають фабричний прогрів даних. */
export const routeIdsWithDataPrefetch: readonly string[] = Object.keys(routeDataPrefetchers);

/**
 * Чи зареєстрований завантажувач чанка для маршруту — БЕЗ його виклику.
 *
 * ⚠ Лише для тестів (`routePrefetch.test.ts`): перевірка «реєстр покриває
 * КОЖЕН пункт навбару» не повинна реально виконувати `import()` усіх ~20
 * сторінок (кожна тягне власне піддерево залежностей, деякі — важкі:
 * Monaco, RevoGrid) лише заради факту «запис є». Сам виклик СПРАВЖНЬОГО
 * завантажувача (`prefetchRoute` вище) перевіряється окремо, на меншій
 * вибірці, де тест свідомо чекає (`await import(...)`) на завершення.
 */
export function hasChunkLoader(routeId: string): boolean {
  return routeChunkLoaders[routeId] !== undefined;
}
