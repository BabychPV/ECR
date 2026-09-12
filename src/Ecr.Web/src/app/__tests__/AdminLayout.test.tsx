import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { AdminLayout } from '@/app/AdminLayout';

/**
 * Layout-маршрут секції `/admin/*` (`PR nav-arch #2`).
 *
 * ⚠ Ізольований роутер (`createMemoryRouter`), не повне дерево
 * `router.tsx`: `AdminLayout` — простий компонент без власного стану чи
 * залежності від сесії/каталогу (на відміну від `AppLayout`), тож і тест
 * не тягне `MantineProvider`/`QueryClientProvider`/мок `fetch` — вони лише
 * замастили б, що саме перевіряється.
 *
 * Мутаційна перевірка (RED → GREEN, вручну, процес картки): тимчасово
 * замінено тіло `AdminLayout` на `return null;` (без `<Outlet/>`) — обидва
 * тести нижче впали (`getByTestId('child')` не знаходить елемент, бо
 * дочірній маршрут ніколи не монтується). Відновлено `<Outlet/>` — GREEN.
 */
describe('AdminLayout — layout-маршрут секції /admin/* (PR nav-arch #2)', () => {
  it('рендериться і монтує дочірній маршрут через Outlet', () => {
    const router = createMemoryRouter(
      [
        {
          path: '/admin',
          element: <AdminLayout />,
          children: [
            { path: 'templates', element: <div data-testid="child">Сторінка шаблонів</div> },
          ],
        },
      ],
      { initialEntries: ['/admin/templates'] },
    );

    render(<RouterProvider router={router} />);

    expect(screen.getByTestId('child').textContent).toBe('Сторінка шаблонів');
  });

  it('не ламає ІНШІ дочірні маршрути секції — кожен рендериться на своєму шляху', () => {
    const router = createMemoryRouter(
      [
        {
          path: '/admin',
          element: <AdminLayout />,
          children: [
            { path: 'templates', element: <div data-testid="child">Шаблони</div> },
            { path: 'registries', element: <div data-testid="child">Довідники</div> },
            { path: 'health', element: <div data-testid="child">Стан здоров'я</div> },
          ],
        },
      ],
      { initialEntries: ['/admin/registries'] },
    );

    render(<RouterProvider router={router} />);

    // Саме "Довідники" — не "Шаблони" й не "Стан здоров'я": AdminLayout не
    // рендерить перший-ліпший чи всі дочірні маршрути одразу, а той, що
    // відповідає поточній адресі.
    expect(screen.getByTestId('child').textContent).toBe('Довідники');
  });
});
