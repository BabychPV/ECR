import { Suspense, createElement, lazy, type JSX } from 'react';
import { Loader, Center } from '@mantine/core';
import { createBrowserRouter } from 'react-router-dom';
import { AdminLayout } from './AdminLayout';
import { AppLayout } from './AppLayout';
import { TemplateVersionLayout } from './TemplateVersionLayout';
import { childPath, relativePath, routes } from './routes';

/**
 * Маршрути застосунку.
 *
 * ⚠ Сторінки завантажуються **ліниво**. Конструктор шаблонів і конфігуратор
 * методологій разом важать більше, ніж уся решта: класти їх у початковий
 * бандл означало б, що оператор, який лише заповнює таблицю, щоранку
 * завантажує редактори, яких не відкриє.
 *
 * ⛔ Маршрутів `/reports/*` НЕМАЄ: звітність лишається в SSRS (D-52).
 * `/admin/snapshots` — не виняток із цього правила, а його межа: там
 * будують і бачать ЗРІЗ (`rpt.*`), який SSRS читає, а не сам звіт.
 */
const LoginPage = lazy(async () => ({ default: (await import('@/pages/LoginPage')).LoginPage }));
const ChangePasswordPage = lazy(async () => ({
  default: (await import('@/pages/ChangePasswordPage')).ChangePasswordPage,
}));
const DocumentsPage = lazy(async () => ({
  default: (await import('@/pages/DocumentsPage')).DocumentsPage,
}));
const DocumentPage = lazy(async () => ({
  default: (await import('@/pages/DocumentPage')).DocumentPage,
}));
const TemplatesPage = lazy(async () => ({
  default: (await import('@/pages/admin/TemplatesPage')).TemplatesPage,
}));
const TemplateVersionPage = lazy(async () => ({
  default: (await import('@/pages/admin/TemplateVersionPage')).TemplateVersionPage,
}));
/**
 * ⚠ Окремий маршрут, а не вкладка в редакторі версії. Зв'язки таблиць
 * налаштовують заходом «а звідки в цій таблиці числа» (`ФВ-2.13`), і на таку
 * відповідь треба вміти дати посилання; версія при цьому лишається в адресі,
 * бо зв'язок належить саме їй.
 */
const TableRelationsPage = lazy(async () => ({
  default: (await import('@/pages/admin/TableRelationsPage')).TableRelationsPage,
}));
const RegistriesPage = lazy(async () => ({
  default: (await import('@/pages/admin/RegistriesPage')).RegistriesPage,
}));

/**
 * ⚠ Окремий маршрут, а не вкладка в переліку довідників (`ФВ-8.12`). Перелік
 * відповідає на «які значення можна обрати», конструктор — на «як цей довідник
 * улаштований»: різні питання, різні права і різна аудиторія. На друге треба
 * вміти дати посилання (`ФВ-14.29`).
 */
const RegistryConstructorPage = lazy(async () => ({
  default: (await import('@/pages/admin/RegistryConstructorPage')).RegistryConstructorPage,
}));
const MethodologiesPage = lazy(async () => ({
  default: (await import('@/pages/admin/MethodologiesPage')).MethodologiesPage,
}));
const MethodologyVersionsPage = lazy(async () => ({
  default: (await import('@/pages/admin/MethodologyVersionsPage')).MethodologyVersionsPage,
}));
const ExpressionsPage = lazy(async () => ({
  default: (await import('@/pages/admin/ExpressionsPage')).ExpressionsPage,
}));
const SecurityPage = lazy(async () => ({
  default: (await import('@/pages/admin/SecurityPage')).SecurityPage,
}));
const PeriodsPage = lazy(async () => ({
  default: (await import('@/pages/admin/PeriodsPage')).PeriodsPage,
}));
const SourcesPage = lazy(async () => ({
  default: (await import('@/pages/admin/SourcesPage')).SourcesPage,
}));

/**
 * ⚠ Окремий маршрут, а не вкладка в `/admin/sources`. Перелік джерел
 * відповідає на «чи збирається», перегляд мапінгу — на «куди лягає»; це різні
 * питання, і на друге треба вміти дати посилання (`ФВ-14.29`).
 */
const MappingPreviewPage = lazy(async () => ({
  default: (await import('@/pages/admin/MappingPreviewPage')).MappingPreviewPage,
}));
const JobsPage = lazy(async () => ({ default: (await import('@/pages/admin/JobsPage')).JobsPage }));
const SnapshotsPage = lazy(async () => ({
  default: (await import('@/pages/admin/SnapshotsPage')).SnapshotsPage,
}));
const AuditPage = lazy(async () => ({
  default: (await import('@/pages/admin/AuditPage')).AuditPage,
}));
const UnitsPage = lazy(async () => ({
  default: (await import('@/pages/admin/UnitsPage')).UnitsPage,
}));
const UiStringsPage = lazy(async () => ({
  default: (await import('@/pages/admin/UiStringsPage')).UiStringsPage,
}));
const HealthPage = lazy(async () => ({
  default: (await import('@/pages/admin/HealthPage')).HealthPage,
}));

/**
 * ⛔ Маршрут НЕ під `/admin`: він для кожного, а не для адміністратора. Ролі
 * доменних користувачів призначаються на AD-групу (`ФВ-6.15`), і людина без
 * жодного збігу бачить порожні екрани, які не відрізняються від справної
 * системи без даних (`H-21`). Сховати цю відповідь в адміністрування означало
 * б лишити її тим, кому вона й не потрібна.
 */
const MyGroupsPage = lazy(async () => ({
  default: (await import('@/pages/MyGroupsPage')).MyGroupsPage,
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
    children: [
      // ⚠ Шлях і `handle` кожного маршруту нижче беруться з реєстру
      // (`./routes`) — єдиного місця, де ці рядки набираються руками.
      // `index: true` — виняток: домашній маршрут не має власного сегмента,
      // тож `routes.home.path` ('/') тут не застосовний як `path`.
      { index: true, element: <DocumentsPage />, handle: routes.home.handle },
      {
        path: childPath(routes.changePassword),
        element: <ChangePasswordPage />,
        handle: routes.changePassword.handle,
      },
      { path: childPath(routes.myGroups), element: <MyGroupsPage />, handle: routes.myGroups.handle },
      {
        path: childPath(routes.documentDetail),
        element: <DocumentPage />,
        handle: routes.documentDetail.handle,
      },

      /**
       * Секція `/admin/*` — проміжний layout-маршрут (`PR nav-arch #2`,
       * директива: «мінімум `/admin/*`»). Усе, що раніше було 17 прямими
       * дітьми `AppLayout` під префіксом `admin/`, тепер — діти ЦЬОГО
       * `<Route>`: `AdminLayout` монтує `<Outlet/>` між `AppLayout` і кожною
       * адмінською сторінкою, і саме тут природно стане рольовий гард
       * секції (`PR #4`) чи заголовок/breadcrumbs секції (`PR #3`), не
       * розкидані по кожній сторінці окремо.
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
            element: <TemplatesPage />,
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
            element: <TemplateVersionLayout />,
            children: [
              {
                path: relativePath(routes.adminTemplateVersion, 'admin/templates/:id'),
                element: <TemplateVersionPage />,
                handle: routes.adminTemplateVersion.handle,
              },
              {
                path: relativePath(routes.adminTemplateVersionRelations, 'admin/templates/:id'),
                element: <TableRelationsPage />,
                handle: routes.adminTemplateVersionRelations.handle,
              },
            ],
          },

          {
            path: relativePath(routes.adminRegistries, 'admin'),
            element: <RegistriesPage />,
            handle: routes.adminRegistries.handle,
          },
          {
            path: relativePath(routes.adminRegistryDefinition, 'admin'),
            element: <RegistryConstructorPage />,
            handle: routes.adminRegistryDefinition.handle,
          },
          {
            path: relativePath(routes.adminMethodologies, 'admin'),
            element: <MethodologiesPage />,
            handle: routes.adminMethodologies.handle,
          },
          {
            path: relativePath(routes.adminMethodologyVersions, 'admin'),
            element: <MethodologyVersionsPage />,
            handle: routes.adminMethodologyVersions.handle,
          },
          {
            path: relativePath(routes.adminExpressions, 'admin'),
            element: <ExpressionsPage />,
            handle: routes.adminExpressions.handle,
          },
          {
            path: relativePath(routes.adminSecurity, 'admin'),
            element: <SecurityPage />,
            handle: routes.adminSecurity.handle,
          },
          {
            path: relativePath(routes.adminPeriods, 'admin'),
            element: <PeriodsPage />,
            handle: routes.adminPeriods.handle,
          },
          {
            path: relativePath(routes.adminSources, 'admin'),
            element: <SourcesPage />,
            handle: routes.adminSources.handle,
          },
          {
            path: relativePath(routes.adminMapping, 'admin'),
            element: <MappingPreviewPage />,
            handle: routes.adminMapping.handle,
          },
          {
            path: relativePath(routes.adminJobs, 'admin'),
            element: <JobsPage />,
            handle: routes.adminJobs.handle,
          },
          {
            path: relativePath(routes.adminSnapshots, 'admin'),
            element: <SnapshotsPage />,
            handle: routes.adminSnapshots.handle,
          },
          {
            path: relativePath(routes.adminAudit, 'admin'),
            element: <AuditPage />,
            handle: routes.adminAudit.handle,
          },
          {
            path: relativePath(routes.adminUnits, 'admin'),
            element: <UnitsPage />,
            handle: routes.adminUnits.handle,
          },
          {
            path: relativePath(routes.adminUiStrings, 'admin'),
            element: <UiStringsPage />,
            handle: routes.adminUiStrings.handle,
          },
          {
            path: relativePath(routes.adminHealth, 'admin'),
            element: <HealthPage />,
            handle: routes.adminHealth.handle,
          },
        ],
      },
    ],
  },
]);
