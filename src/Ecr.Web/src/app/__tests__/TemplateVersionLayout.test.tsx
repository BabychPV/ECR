import type { JSX } from 'react';
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider, useParams } from 'react-router-dom';
import { TemplateVersionLayout } from '@/app/TemplateVersionLayout';

/**
 * Layout-маршрут секції `admin/templates/:id/*` (`PR nav-arch #2`).
 *
 * ⚠ Ізольований роутер, як і `AdminLayout.test.tsx` — той самий підхід,
 * той самий аргумент (простий компонент без залежності від сесії).
 *
 * ⚠ Перевіряється НЕ лише «дочірній маршрут з'являється», а й що спільний
 * параметр `:id` (спільний для `TemplateVersionPage` і
 * `TableRelationsPage` за задумом цього layout'а — обидва належать одному
 * шаблону) справді доступний дочірньому маршруту через звичайний
 * `useParams()` React Router — це і є те, заради чого шаблон/версія
 * поділяють один layout, а не два незалежні плоскі маршрути.
 *
 * Мутаційна перевірка (RED → GREEN, вручну, процес картки): тимчасово
 * замінено тіло `TemplateVersionLayout` на `return null;` (без
 * `<Outlet/>`) — обидва тести нижче впали (дочірній вміст ніколи не
 * монтується, `:id` ніде читати). Відновлено `<Outlet/>` — GREEN.
 */
function ChildReadingId(): JSX.Element {
  const { id } = useParams();
  return <div data-testid="child">id={id}</div>;
}

describe('TemplateVersionLayout — layout-маршрут секції admin/templates/:id/* (PR nav-arch #2)', () => {
  it('рендериться, монтує дочірній маршрут через Outlet, і :id доступний дочірньому маршруту', () => {
    const router = createMemoryRouter(
      [
        {
          path: '/admin/templates/:id',
          element: <TemplateVersionLayout />,
          children: [{ path: 'versions/:versionId', element: <ChildReadingId /> }],
        },
      ],
      { initialEntries: ['/admin/templates/42/versions/7'] },
    );

    render(<RouterProvider router={router} />);

    expect(screen.getByTestId('child').textContent).toBe('id=42');
  });

  it('не ламає ІНШИЙ дочірній маршрут секції (relations) — рендериться зі спільним :id', () => {
    const router = createMemoryRouter(
      [
        {
          path: '/admin/templates/:id',
          element: <TemplateVersionLayout />,
          children: [
            { path: 'versions/:versionId', element: <div data-testid="child">версія</div> },
            {
              path: 'versions/:versionId/relations',
              element: <ChildReadingId />,
            },
          ],
        },
      ],
      { initialEntries: ['/admin/templates/42/versions/7/relations'] },
    );

    render(<RouterProvider router={router} />);

    // Саме сторінка зв'язків (з `:id`), не сторінка версії — той самий
    // layout обслуговує обидва маршрути, не плутаючи, який зараз активний.
    expect(screen.getByTestId('child').textContent).toBe('id=42');
  });
});
