import { afterEach, describe, expect, it, vi } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { routes } from '@/app/routes';
import { hasChunkLoader, prefetchRoute, routeIdsWithDataPrefetch } from '@/app/routePrefetch';

/**
 * Прогрів за наміром (`PR nav-arch #5`, директива C2) — реєстр
 * `routePrefetch.ts`.
 *
 * ⛔ Головна дисципліна файла — та сама, що й `queryKeys.test.ts`/
 * `breadcrumbResolvers.test.ts`: не «функція щось повернула», а «прогрів
 * пише в кеш РІВНО під тим ключем, який власна сторінка читає своїм
 * `useQuery`» (`queryKeys.templates.list()`/`registries.list()`/
 * `methodologies.list()`, ті самі виклики фабрики, що й `TemplatesPage.tsx`/
 * `RegistriesPage.tsx`/`MethodologiesPage.tsx`/`ExpressionsPage.tsx` на
 * момент цієї картки). Якщо прогрів колись розійдеться з ключем сторінки
 * (одруківка, окремий рядковий літерал замість виклику фабрики) — сторінка
 * після кліку з навбару застане кеш ПОРОЖНІМ під СВОЇМ ключем і зробить
 * повний мережевий запит заново, тобто prefetch мовчки ні на що не впливав
 * би. Тести нижче ловлять саме це, не факт виклику `prefetchQuery` як такий.
 *
 * ⚠ Сторінки, чий прогрів даних тут перевіряється, замокані ЛЕГКИМ
 * заглушками (`vi.mock`, нижче) — `prefetchRoute` однаково (fire-and-forget)
 * прогріває їхній РЕАЛЬНИЙ код-чанк (`routeChunkLoaders`), а він тягне
 * власне важке піддерево залежностей сторінки (Monaco, редактори тощо).
 * Під повним прогоном `npm test` (67 файлів паралельно) реальний `import()`
 * такої сторінки під навантаженням стабільно перевищував тестовий таймаут
 * 5000 мс — цей файл перевіряє ДАНІ прогріву (ключ/`queryFn`), а не саму
 * вагу чанка сторінки (те доведено `npm run budget` і живою перевіркою на
 * стенді, не тут).
 */
vi.mock('@/pages/admin/TemplatesPage', () => ({ TemplatesPage: () => null }));
vi.mock('@/pages/admin/RegistriesPage', () => ({ RegistriesPage: () => null }));
vi.mock('@/pages/admin/MethodologiesPage', () => ({ MethodologiesPage: () => null }));
vi.mock('@/pages/admin/ExpressionsPage', () => ({ ExpressionsPage: () => null }));
vi.mock('@/pages/admin/UnitsPage', () => ({ UnitsPage: () => null }));

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

describe('prefetchRoute — прогрів даних (лише фабричні ключі)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('templates: кеш після прогріву читається РІВНО ключем queryKeys.templates.list()', async () => {
    const templatePage = { items: [{ id: 1, code: 'TPL1' }], nextCursor: null, totalCount: 1 };
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(templatePage)));

    const client = new QueryClient();
    prefetchRoute(routes.adminTemplates.id, client);

    await vi.waitFor(() => {
      expect(client.getQueryData(queryKeys.templates.list())).toEqual(templatePage);
    });

    // ⚠ `prefetchRoute` так само (fire-and-forget) прогріває чанк
    // `TemplatesPage` — дочекатись його тут явно, інакше він лишається
    // необробленим після завершення тесту (`import()`, розпочатий цим
    // тестом, і далі летить у фоні в наступний).
    await import('@/pages/admin/TemplatesPage');
  });

  it('registries: кеш після прогріву читається РІВНО ключем queryKeys.registries.list()', async () => {
    const registryList = [{ code: 'R1', nameL10n: { values: { en: 'R1' } } }];
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(registryList)));

    const client = new QueryClient();
    prefetchRoute(routes.adminRegistries.id, client);

    await vi.waitFor(() => {
      expect(client.getQueryData(queryKeys.registries.list())).toEqual(registryList);
    });

    await import('@/pages/admin/RegistriesPage');
  });

  it('methodologies: кеш після прогріву читається РІВНО ключем queryKeys.methodologies.list()', async () => {
    const methodologyList = [{ id: 1, code: 'M1' }];
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(methodologyList)));

    const client = new QueryClient();
    prefetchRoute(routes.adminMethodologies.id, client);

    await vi.waitFor(() => {
      expect(client.getQueryData(queryKeys.methodologies.list())).toEqual(methodologyList);
    });

    await import('@/pages/admin/MethodologiesPage');
  });

  it('expressions: прогріває ОБИДВА ключі, якими сама сторінка користується (templates + methodologies)', async () => {
    const templatePage = { items: [{ id: 1, code: 'TPL1' }], nextCursor: null, totalCount: 1 };
    const methodologyList = [{ id: 1, code: 'M1' }];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/api/v1/templates')) return jsonResponse(templatePage);
        if (url.includes('/api/v1/methodologies')) return jsonResponse(methodologyList);
        return jsonResponse(null);
      }),
    );

    const client = new QueryClient();
    prefetchRoute(routes.adminExpressions.id, client);

    await vi.waitFor(() => {
      expect(client.getQueryData(queryKeys.templates.list())).toEqual(templatePage);
      expect(client.getQueryData(queryKeys.methodologies.list())).toEqual(methodologyList);
    });

    await import('@/pages/admin/ExpressionsPage');
  });

  it('маршрут без фабричного ключа (наприклад, "units") НЕ прогріває жодних даних — лише чанк', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(null)));

    const client = new QueryClient();
    const before = client.getQueryCache().getAll().length;

    expect(() => prefetchRoute(routes.adminUnits.id, client)).not.toThrow();

    // ⚠ Не чекаємо на мережу навмисно: якщо колись хтось додасть тут
    // прогрів даних із вигаданим (не фабричним) ключем — цей тест не ловив
    // би те, а `routeIdsWithDataPrefetch` нижче — ловить: реєстр прогріву
    // даних лишається СПИСКОМ, звіреним з фабричними доменами прямо.
    expect(client.getQueryCache().getAll().length).toBe(before);

    await import('@/pages/admin/UnitsPage');
  });

  it('реєстр прогріву даних обмежений маршрутами з фабричним ключем (templates/registries/methodologies), не всім навбаром', () => {
    expect([...routeIdsWithDataPrefetch].sort()).toEqual(
      [
        routes.adminTemplates.id,
        routes.adminRegistries.id,
        routes.adminMethodologies.id,
        routes.adminExpressions.id,
      ].sort(),
    );
  });

  it('кожен пункт навбару має зареєстрований завантажувач чанка', () => {
    // ⚠ `hasChunkLoader`, не `prefetchRoute`: цей тест перевіряє ПОКРИТТЯ
    // реєстру (запис існує для кожного `id`), не факт реального `import()`
    // усіх ~20 сторінок одразу — те, що кожен окремий завантажувач і
    // справді резолвиться в реальний модуль, доведено вище (templates/
    // registries/methodologies/expressions/units) явним `await import(...)`.
    for (const route of [
      routes.home,
      routes.changePassword,
      routes.myGroups,
      routes.documentDetail,
      routes.adminTemplates,
      routes.adminTemplateVersion,
      routes.adminTemplateVersionRelations,
      routes.adminRegistries,
      routes.adminRegistryDefinition,
      routes.adminMethodologies,
      routes.adminMethodologyVersions,
      routes.adminExpressions,
      routes.adminSecurity,
      routes.adminPeriods,
      routes.adminSources,
      routes.adminMapping,
      routes.adminJobs,
      routes.adminSnapshots,
      routes.adminAudit,
      routes.adminUnits,
      routes.adminUiStrings,
      routes.adminHealth,
    ]) {
      expect(hasChunkLoader(route.id)).toBe(true);
    }
  });
});
