import { describe, it, expect } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { ChangePasswordPage } from '@/pages/ChangePasswordPage';
import { RouteAnnouncer } from '@/shared/ui/RouteAnnouncer';

/**
 * `ChangePasswordPage` використовує спільний `PageHeader`, а не голий
 * `Title` (`Q-261`).
 *
 * ⛔ Ця сторінка була ЄДИНИМ винятком усередині `AppLayout`, що обходив
 * `PageHeader`: усі інші екрани переносять фокус на заголовок і оголошують
 * назву маршруту через `RouteAnnouncer` при монтуванні
 * (`shared/ui/PageHeader.tsx`, `ФВ-14.19`, перевірено тим самим прийомом,
 * що й `shared/theme/__tests__/motion.test.tsx`). Саме тут це найболючіше:
 * на цей екран потрапляють майже виключно ПРИМУСОВИМ редиректом
 * (`me.mustChangePassword`), тобто без `PageHeader` користувач читалки
 * опинявся на новому екрані без жодного оголошення, чому щойно зник той,
 * на якому він був.
 */
function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <RouteAnnouncer />
          <ChangePasswordPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('ChangePasswordPage: PageHeader (Q-261)', () => {
  // ⚠ Цей тест — ПЕРШИЙ навмисно: `announceRoute` (RouteAnnouncer.tsx)
  // дедуплікує оголошення за текстом у модульній змінній `last`, і кожен
  // тест цього файлу монтує ту саму сторінку з тим самим заголовком
  // (`password.title` — фіксований ключ, на відміну від `motion.test.tsx`,
  // де кожен тест передає РІЗНИЙ заголовок саме для того, щоб обійти цю
  // дедуплікацію). Модуль ізольований на файл (vitest), тож `last`
  // починається порожнім лише для ПЕРШОГО виклику в цьому файлі.
  it('назва екрана оголошується через aria-live (RouteAnnouncer)', async () => {
    show();

    const live = screen.getByRole('status');
    expect(live.getAttribute('aria-live')).toBe('polite');

    await waitFor(() => {
      expect(live.textContent).toBe('⟦password.title⟧');
    });
  });

  it('заголовок сторінки отримує фокус при відкритті', () => {
    show();

    const heading = screen.getByRole('heading', { name: '⟦password.title⟧' });

    expect(document.activeElement).toBe(heading);
  });

  it('заголовок приймає фокус програмно, але не стає зупинкою табу', () => {
    show();

    expect(
      screen.getByRole('heading', { name: '⟦password.title⟧' }).getAttribute('tabindex'),
    ).toBe('-1');
  });

  it('голого h4-заголовка без PageHeader більше немає', () => {
    show();

    // ⛔ До фіксу тут стояв `<Title order={4}>` — заголовок БЕЗ ролі
    // `heading` рівня 3, без фокуса і без оголошення. Перевіряємо, що
    // рівно ОДИН заголовок на екрані, і це саме `PageHeader` (order=3).
    const headings = screen.getAllByRole('heading');
    expect(headings).toHaveLength(1);
    expect(headings[0]?.tagName).toBe('H3');
  });
});
