import { Suspense, createElement, lazy, type JSX } from 'react';
import { Loader, Center } from '@mantine/core';
import { createBrowserRouter } from 'react-router-dom';
import { AppLayout } from './AppLayout';

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
      { index: true, element: <DocumentsPage /> },
      { path: 'change-password', element: <ChangePasswordPage /> },
      { path: 'my-groups', element: <MyGroupsPage /> },
      { path: 'documents/:id', element: <DocumentPage /> },
      { path: 'admin/templates', element: <TemplatesPage /> },
      { path: 'admin/templates/:id/versions/:versionId', element: <TemplateVersionPage /> },
      {
        path: 'admin/templates/:id/versions/:versionId/relations',
        element: <TableRelationsPage />,
      },
      { path: 'admin/registries', element: <RegistriesPage /> },
      { path: 'admin/methodologies', element: <MethodologiesPage /> },
      { path: 'admin/methodologies/:id/versions', element: <MethodologyVersionsPage /> },
      { path: 'admin/expressions', element: <ExpressionsPage /> },
      { path: 'admin/security', element: <SecurityPage /> },
      { path: 'admin/periods', element: <PeriodsPage /> },
      { path: 'admin/sources', element: <SourcesPage /> },
      { path: 'admin/mapping', element: <MappingPreviewPage /> },
      { path: 'admin/jobs', element: <JobsPage /> },
      { path: 'admin/snapshots', element: <SnapshotsPage /> },
      { path: 'admin/audit', element: <AuditPage /> },
      { path: 'admin/units', element: <UnitsPage /> },
      { path: 'admin/ui-strings', element: <UiStringsPage /> },
      { path: 'admin/health', element: <HealthPage /> },
    ],
  },
]);
