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
const RegistriesPage = lazy(async () => ({
  default: (await import('@/pages/admin/RegistriesPage')).RegistriesPage,
}));
const MethodologiesPage = lazy(async () => ({
  default: (await import('@/pages/admin/MethodologiesPage')).MethodologiesPage,
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
const JobsPage = lazy(async () => ({ default: (await import('@/pages/admin/JobsPage')).JobsPage }));
const HealthPage = lazy(async () => ({
  default: (await import('@/pages/admin/HealthPage')).HealthPage,
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
      { path: 'documents/:id', element: <DocumentPage /> },
      { path: 'admin/templates', element: <TemplatesPage /> },
      { path: 'admin/templates/:id/versions/:versionId', element: <TemplateVersionPage /> },
      { path: 'admin/registries', element: <RegistriesPage /> },
      { path: 'admin/methodologies', element: <MethodologiesPage /> },
      { path: 'admin/security', element: <SecurityPage /> },
      { path: 'admin/periods', element: <PeriodsPage /> },
      { path: 'admin/sources', element: <SourcesPage /> },
      { path: 'admin/jobs', element: <JobsPage /> },
      { path: 'admin/health', element: <HealthPage /> },
    ],
  },
]);
