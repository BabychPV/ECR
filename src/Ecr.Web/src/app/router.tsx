import { Suspense, createElement, lazy, type JSX } from 'react';
import { Loader, Center } from '@mantine/core';
import { createBrowserRouter, type RouteObject } from 'react-router-dom';
import { AdminLayout } from './AdminLayout';
import { AppLayout } from './AppLayout';
import { ForbiddenPage } from './ForbiddenPage';
import { NotFoundPage } from './NotFoundPage';
import { RouteErrorPage } from './RouteErrorPage';
import { RouteGuard } from './RouteGuard';
import {
  AuditPage,
  CampaignOverviewPage,
  ChangePasswordPage,
  ConsistencyIssuesPage,
  DocumentPage,
  DocumentsPage,
  ExpressionsPage,
  HealthPage,
  JobsPage,
  MappingPreviewPage,
  MethodologiesPage,
  MethodologyVersionsPage,
  MyGroupsPage,
  NotificationsPage,
  PeriodsPage,
  RegistriesPage,
  RegistryConstructorPage,
  SecurityPage,
  SnapshotsPage,
  SourcesPage,
  TableRelationsPage,
  TemplateVersionPage,
  TemplatesPage,
  UiStringsPage,
  UnitsPage,
} from './routePrefetch';
import { TemplateVersionLayout } from './TemplateVersionLayout';
import { childPath, relativePath, routes, type RouteHandle } from './routes';

/**
 * Маршрути застосунку.
 *
 * ⚠ Сторінки завантажуються **ліниво**. Конструктор шаблонів і конфігуратор
 * методологій разом важать більше, ніж уся решта: класти їх у початковий
 * бандл означало б, що оператор, який лише заповнює таблицю, щоранку
 * завантажує редактори, яких не відкриє.
 *
 * ⚠ Самі лінивi компоненти (`lazy(...)`) тепер визначені в `./routePrefetch`
 * (`PR nav-arch #5`), не тут: той самий завантажувач, яким тут монтується
 * `<TemplatesPage/>` і т. д., там-таки прогріває чанк на hover/focus навбару
 * (`useRoutePrefetch.ts`) — одна фабрика `import()` на сторінку, а не дві
 * незалежні (по одній на "монтування" і на "прогрів"), які могли б розійтися.
 *
 * ⛔ Маршрутів `/reports/*` НЕМАЄ: звітність лишається в SSRS (D-52).
 * `/admin/snapshots` — не виняток із цього правила, а його межа: там
 * будують і бачать ЗРІЗ (`rpt.*`), який SSRS читає, а не сам звіт.
 */
const LoginPage = lazy(async () => ({ default: (await import('@/pages/LoginPage')).LoginPage }));

/**
 * Картка шаблону (`UI-09`) — лінива, як і решта сторінок.
 *
 * ⚠ Оголошена ТУТ, а не в `routePrefetch.ts`, свідомо: той реєстр існує для
 * прогріву за наміром із НАВБАРУ (`useRoutePrefetch`), а на цю сторінку
 * заходять із рядка переліку шаблонів. Запис у реєстрі прогріву, якого ніхто
 * не кличе, — це третій спосіб адресувати той самий маршрут без жодного
 * споживача.
 */
const TemplateCardPage = lazy(async () => ({
  default: (await import('@/pages/admin/TemplateCardPage')).TemplateCardPage,
}));

/**
 * Межа очікування для маршрутів поза каркасом.
 *
 * ⚠ Сторінка входу рендериться поза `AppLayout`, тобто поза його `<Suspense>`.
 * Власна межа тут не рятує від аварії — `RouterProvider` має свою, — вона
 * задає, ЩО видно, поки вантажиться чанк. Без неї це порожній білий екран, а
 * перше, що бачить кожен користувач системи, — саме ця сторінка.
 *
 * ⚠ Тут спінер, а не скелет: форма входу коротка й з'являється миттєво, а
 * скелет із трьох смуг на весь екран виглядав би як зламана сторінка.
 */
function Chunk({ children }: { children: JSX.Element }): JSX.Element {
  return (
    <Suspense
      fallback={
        <Center h="100vh">
          <Loader size="sm" />
        </Center>
      }
    >
      {children}
    </Suspense>
  );
}

/**
 * Обгортає елемент маршруту рольовим гардом (`PR nav-arch #4`, `Q-279`).
 *
 * ⚠ Викликається ДЛЯ КОЖНОГО дочірнього маршруту `AppLayout` нижче, не лише
 * для записів із `handle.permission` — `RouteGuard` сам пропускає рендер
 * наскрізь, коли права в записі немає (`RouteGuard.tsx`). Один виклик на
 * кожен `element:` — це навмисно ОДНА форма для всього дерева: розрізняти
 * «цей маршрут обгорнутий, а той ні» на око за 20 записами реєстру — це і є
 * той клас неоднорідності, якому реєстр (`Q-276`) заважає для шляхів і
 * лейблів, а `guarded()` — для гарда.
 */
function guarded(handle: RouteHandle, element: JSX.Element): JSX.Element {
  return <RouteGuard handle={handle}>{element}</RouteGuard>;
}

/**
 * Межа помилки рендера для ВСІХ дочірніх маршрутів `AppLayout` (`D14-11`).
 *
 * ⚠ Це ОДИН безшляховий маршрут-обгортка, а не `errorElement` на кожному з
 * 22 листків. Різниця не косметична: обгортка покриває дітей ЗА ПОБУДОВОЮ —
 * новий маршрут, доданий у масив нижче, захищений, навіть якщо автор про цю
 * межу не знав. Перелік із 22 повторень цієї властивості не має: один
 * пропущений рядок — і саме той маршрут падає так само, як падав увесь
 * застосунок до цієї зміни, і помітити це можна лише живим переходом.
 *
 * ⚠ Обгортка стоїть ПІД `AppLayout`, а не на ньому: React Router підставляє
 * `errorElement` замість вмісту ТОГО маршруту, що його оголосив. На корені
 * `/` це знесло б разом із помилкою й навігацію, і шапку — тобто дало б ту
 * саму втрату застосунку, від якої `D14-11` і рятує. Тут же
 * `RouteErrorPage` малюється в `<Outlet/>` каркаса: меню й шапка живі, падає
 * лише вміст.
 *
 * ⚠ У безшляхового маршруту немає власного `element` — React Router рендерить
 * `<Outlet/>` за замовчуванням, тож зайвого рівня в дереві не з'являється.
 */
export function withRenderErrorBoundary(children: RouteObject[]): RouteObject[] {
  return [{ errorElement: <RouteErrorPage />, children }];
}

/**
 * Каталог компонентів — лише в розробці (модуль 7.8).
 *
 * ⛔ У збірці для розгортання маршруту НЕМАЄ взагалі, і це не про безпеку, а
 * про розмір і чесність: сторінка тягне всі спільні компоненти одразу, тобто
 * зводить нанівець сенс лінивих маршрутів, а в переліку адрес системи
 * з'явилася б сторінка, якої в системі немає.
 *
 * ⚠ Поза AppLayout: інакше вона потребувала б входу і була б недоступна саме
 * тоді, коли потрібна, — при налаштуванні вигляду на чистій машині.
 */
const devRoutes = import.meta.env.DEV
  ? [
      {
        path: '/_kitchen-sink',
        element: (
          <Chunk>
            {createElement(
              lazy(async () => ({
                default: (await import('@/pages/KitchenSinkPage')).KitchenSinkPage,
              })),
            )}
          </Chunk>
        ),
      },
    ]
  : [];

export const router = createBrowserRouter([
  ...devRoutes,
  { path: '/login', element: <Chunk><LoginPage /></Chunk> },
  {
    path: '/',
    element: <AppLayout />,
    children: withRenderErrorBoundary([
      // ⚠ Шлях і `handle` кожного маршруту нижче беруться з реєстру
      // (`./routes`) — єдиного місця, де ці рядки набираються руками.
      // `index: true` — виняток: домашній маршрут не має власного сегмента,
      // тож `routes.home.path` ('/') тут не застосовний як `path`.
      { index: true, element: guarded(routes.home.handle, <DocumentsPage />), handle: routes.home.handle },
      {
        path: childPath(routes.changePassword),
        element: guarded(routes.changePassword.handle, <ChangePasswordPage />),
        handle: routes.changePassword.handle,
      },
      {
        path: childPath(routes.myGroups),
        element: guarded(routes.myGroups.handle, <MyGroupsPage />),
        handle: routes.myGroups.handle,
      },
      {
        path: childPath(routes.documentDetail),
        element: guarded(routes.documentDetail.handle, <DocumentPage />),
        handle: routes.documentDetail.handle,
      },

      /**
       * Секція `/admin/*` — проміжний layout-маршрут (`PR nav-arch #2`,
       * директива: «мінімум `/admin/*`»). Усе, що раніше було 17 прямими
       * дітьми `AppLayout` під префіксом `admin/`, тепер — діти ЦЬОГО
       * `<Route>`: `AdminLayout` монтує `<Outlet/>` між `AppLayout` і кожною
       * адмінською сторінкою.
       *
       * ⚠ Рольовий гард (`PR nav-arch #4`, `Q-279`) НЕ стоїть на самому
       * `AdminLayout` — 17 сторінок секції вимагають 17 РІЗНИХ прав
       * (`Template.Edit`, `Registry.View`, `Security.ManageRoles`…), тож
       * один гард на рівні секції або пропускав би зайве, або забороняв би
       * забагато. Гард (`guarded()`, `RouteGuard.tsx`) обгортає кожен ЛИСТ
       * окремо, читаючи його ВЛАСНИЙ `handle.permission` з реєстру —
       * `AdminLayout` лишається тим самим голим `Outlet`, яким і був.
       *
       * ⚠ `path: 'admin'` тут — ЄДИНЕ місце, де сегмент `admin` набирається
       * руками: усі шляхи нижче — `relativePath(routes.X, 'admin')`, тобто
       * похідні від АБСОЛЮТНИХ шляхів реєстру (`routes.ts`), а не другий
       * незалежний рядковий літерал.
       */
      {
        path: 'admin',
        element: <AdminLayout />,
        children: [
          {
            path: relativePath(routes.adminTemplates, 'admin'),
            element: guarded(routes.adminTemplates.handle, <TemplatesPage />),
            handle: routes.adminTemplates.handle,
          },

          /**
           * Секція `admin/templates/:id/*` — другий кандидат на власний
           * layout, названий директивою прямо («`templates/:id/*` як секція
           * з власними вкладками версій/зв'язків»): `TemplateVersionPage` і
           * `TableRelationsPage` належать одному шаблону/версії
           * (спільні `:id`/`:versionId`).
           *
           * ⚠ `path: 'templates/:id'` — синтетичний вузол вкладеності, не
           * запис реєстру: у `routes.ts` немає сторінки на самому
           * `/admin/templates/:id` (без `/versions/:versionId`) — цей
           * рівень існує лише для того, щоб два дочірні маршрути ділили
           * один `<Outlet/>` і один параметр `:id`.
           */
          {
            path: 'templates/:id',
            element: guarded(routes.adminTemplateSection.handle, <TemplateVersionLayout />),
            // ⚠ `handle` цього синтетичного вузла живе в `routes.ts`
            // (`routes.adminTemplateSection`), не тут — той самий інваріант,
            // що й для листових маршрутів нижче (`PR nav-arch #3`,
            // breadcrumbs читають назву шаблону саме з цього `handle`).
            handle: routes.adminTemplateSection.handle,
            children: [
              /**
               * Лист самої секції (`UI-09`): картка шаблону.
               *
               * ⛔ `index: true`, а не окремий запис реєстру: `routes.ts`
               * стереже УНІКАЛЬНІСТЬ шляхів (`routeConfig.test.ts`), а шлях
               * цього листа — рівно `adminTemplateSection.path`. Другий запис
               * із тією самою адресою завалив би сторожа, і це правильно: дві
               * назви однієї адреси — це початок розходження.
               *
               * ⛔ `handle` тут НЕ ставиться — і це не пропуск. `useMatches()`
               * віддав би індексний матч із тим самим `pathname`, що й
               * layout-вузол вище, тож `Breadcrumbs` (`key:
               * `match:${pathname}``) отримав би ДВІ однакові крихти поспіль і
               * два однакові ключі React. Назву шаблону в ланцюжок уже кладе
               * сам `adminTemplateSection`.
               *
               * ⚠ `lazy()` стоїть тут, а не в `routePrefetch.ts`, з тієї самої
               * причини, що й `LoginPage` вище: прогрів за наміром гріє пункти
               * НАВБАРУ, а на цю сторінку заходять із переліку шаблонів, тобто
               * з рядка таблиці — реєстру прогріву вона не потребує.
               */
              {
                index: true,
                element: guarded(routes.adminTemplateSection.handle, <TemplateCardPage />),
              },
              {
                path: relativePath(routes.adminTemplateVersion, 'admin/templates/:id'),
                element: guarded(routes.adminTemplateVersion.handle, <TemplateVersionPage />),
                handle: routes.adminTemplateVersion.handle,
              },
              {
                path: relativePath(routes.adminTemplateVersionRelations, 'admin/templates/:id'),
                element: guarded(routes.adminTemplateVersionRelations.handle, <TableRelationsPage />),
                handle: routes.adminTemplateVersionRelations.handle,
              },
            ],
          },

          {
            path: relativePath(routes.adminRegistries, 'admin'),
            element: guarded(routes.adminRegistries.handle, <RegistriesPage />),
            handle: routes.adminRegistries.handle,
          },
          {
            path: relativePath(routes.adminRegistryDefinition, 'admin'),
            element: guarded(routes.adminRegistryDefinition.handle, <RegistryConstructorPage />),
            handle: routes.adminRegistryDefinition.handle,
          },
          {
            path: relativePath(routes.adminMethodologies, 'admin'),
            element: guarded(routes.adminMethodologies.handle, <MethodologiesPage />),
            handle: routes.adminMethodologies.handle,
          },
          {
            path: relativePath(routes.adminMethodologyVersions, 'admin'),
            element: guarded(routes.adminMethodologyVersions.handle, <MethodologyVersionsPage />),
            handle: routes.adminMethodologyVersions.handle,
          },
          {
            path: relativePath(routes.adminExpressions, 'admin'),
            element: guarded(routes.adminExpressions.handle, <ExpressionsPage />),
            handle: routes.adminExpressions.handle,
          },
          {
            path: relativePath(routes.adminSecurity, 'admin'),
            element: guarded(routes.adminSecurity.handle, <SecurityPage />),
            handle: routes.adminSecurity.handle,
          },
          {
            path: relativePath(routes.adminPeriods, 'admin'),
            element: guarded(routes.adminPeriods.handle, <PeriodsPage />),
            handle: routes.adminPeriods.handle,
          },
          {
            path: relativePath(routes.adminSources, 'admin'),
            element: guarded(routes.adminSources.handle, <SourcesPage />),
            handle: routes.adminSources.handle,
          },
          {
            path: relativePath(routes.adminMapping, 'admin'),
            element: guarded(routes.adminMapping.handle, <MappingPreviewPage />),
            handle: routes.adminMapping.handle,
          },
          {
            path: relativePath(routes.adminJobs, 'admin'),
            element: guarded(routes.adminJobs.handle, <JobsPage />),
            handle: routes.adminJobs.handle,
          },
          {
            path: relativePath(routes.adminSnapshots, 'admin'),
            element: guarded(routes.adminSnapshots.handle, <SnapshotsPage />),
            handle: routes.adminSnapshots.handle,
          },
          {
            path: relativePath(routes.adminCampaign, 'admin'),
            element: guarded(routes.adminCampaign.handle, <CampaignOverviewPage />),
            handle: routes.adminCampaign.handle,
          },
          {
            path: relativePath(routes.adminAudit, 'admin'),
            element: guarded(routes.adminAudit.handle, <AuditPage />),
            handle: routes.adminAudit.handle,
          },
          {
            path: relativePath(routes.adminUnits, 'admin'),
            element: guarded(routes.adminUnits.handle, <UnitsPage />),
            handle: routes.adminUnits.handle,
          },
          {
            path: relativePath(routes.adminConsistency, 'admin'),
            element: guarded(routes.adminConsistency.handle, <ConsistencyIssuesPage />),
            handle: routes.adminConsistency.handle,
          },
          {
            path: relativePath(routes.adminUiStrings, 'admin'),
            element: guarded(routes.adminUiStrings.handle, <UiStringsPage />),
            handle: routes.adminUiStrings.handle,
          },
          {
            path: relativePath(routes.adminNotifications, 'admin'),
            element: guarded(routes.adminNotifications.handle, <NotificationsPage />),
            handle: routes.adminNotifications.handle,
          },
          {
            path: relativePath(routes.adminHealth, 'admin'),
            element: guarded(routes.adminHealth.handle, <HealthPage />),
            handle: routes.adminHealth.handle,
          },
        ],
      },

      /**
       * `/403` (`UI-09`, L-правило про доступ) — маршрут-ціль
       * `<Navigate to="/403" .../>` із `RouteGuard.tsx`.
       *
       * ⚠ НЕ запис `routes.ts` — той самий інваріант, що й `path: '*'`
       * нижче: реєстр стереже показ у навбарі/breadcrumbs/гарди для сторінок
       * ЗАСТОСУНКУ, а `/403`, як і `/404`, — стан ПОМИЛКИ, не пункт меню
       * (`routes.test.ts` читає лише `path:` літерали `routes.ts`, тому цей
       * рядок і не мусить туди потрапляти — навмисно, не пропуск).
       *
       * ⛔ Без власного `guarded()`: сторінка відмови в доступі сама не
       * вимагає права — інакше відмова в доступі до маршруту, куди
       * перенаправляє відмова в доступі, дала б нескінченний цикл
       * редиректів.
       */
      { path: '403', element: <ForbiddenPage /> },

      // ⛔ ОБОВ'ЯЗКОВО останній: `react-router` сортує дітей за специфічністю
      // незалежно від порядку оголошення, тож місце в масиві тут не впливає
      // на пріоритет зіставлення — коментар лише про порядок читання файлу.
      // Ловить будь-яку адресу під `/`, якої немає в жодному записі вище
      // (застаріле посилання, помилка в URL): без цього дочірнього маршруту
      // React Router не знаходив ЖОДНОГО збігу і показував власний,
      // розробницький дефолтний екран («Unexpected Application Error!») —
      // знайдено живим переходом на неіснуючу адресу, не тестом.
      { path: '*', element: <NotFoundPage /> },
    ]),
  },
]);
