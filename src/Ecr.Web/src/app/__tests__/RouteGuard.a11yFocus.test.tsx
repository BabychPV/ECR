import { describe, expect, it } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { AccessDeniedPage } from '@/app/RouteGuard';
import { RouteAnnouncer } from '@/shared/ui/RouteAnnouncer';
import { theme } from '@/shared/theme/theme';

/**
 * Фокус і оголошення на `AccessDeniedPage` (`PR nav-arch #7`) — єдина
 * знайдена прогалина аудиту («усі 24 листові маршрути `routes.ts` уже
 * використовують `PageHeader`, і лише ЦЯ сторінка, змонтована всередині
 * `AppLayout` замість гарантованого маршруту, досі обходила його»): перехід
 * на заборонений маршрут — теж зміна екрана, і користувач клавіатури не
 * повинен лишатися на старому пункті навбару без жодного оголошення, чому
 * екран щойно змінився.
 *
 * ⛔ ОКРЕМИЙ файл, а не додаток до `RouteGuard.test.tsx` — навмисно.
 * `RouteGuard.test.tsx` уже монтує `<AccessDeniedPage>` (тест «права немає —
 * показує явну сторінку відмови») БЕЗ `<RouteAnnouncer/>` у дереві; той
 * рендер усе одно викликає `announceRoute(title)`, який записує текст у
 * модульну змінну `last` (`RouteAnnouncer.tsx`) — і `announceRoute`
 * дедуплікує ЗА ТЕКСТОМ: другий виклик із ТИМ САМИМ заголовком (текст
 * відмови не залежить від `permission`) у ЦЬОМУ файлі мовчки не оновив би
 * `live.textContent` свіжого `<RouteAnnouncer/>`, і тест впав би на
 * дедуплікації чужого, непов'язаного рендера, а не на реальному регресі.
 * Vitest ізолює модульний реєстр НА ФАЙЛ (той самий факт, що вже
 * задокументував `ChangePasswordPage.pageHeader.test.tsx`) — окремий файл
 * гарантує, що `last` тут починається порожнім.
 *
 * Мутаційна перевірка (RED → GREEN, вручну, перед комітом):
 * 1. `heading.current?.focus()` у `RouteGuard.tsx` тимчасово прибрано —
 *    RED: «фокус і оголошення» впав на першому твердженні
 *    (`document.activeElement` лишався на `document.body`).
 * 2. Виклик `announceRoute(title)` тимчасово прибрано — RED: той самий тест
 *    впав на `waitFor` (порожня область оголошень).
 * Обидва рази відновлено оригінальний код — GREEN.
 */
function renderAccessDenied(): ReturnType<typeof render> {
  return render(
    <MantineProvider theme={theme}>
      <MemoryRouter>
        <RouteAnnouncer />
        <AccessDeniedPage permission="Security.ManageRoles" />
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('AccessDeniedPage: фокус і оголошення на зміні маршруту (PR nav-arch #7)', () => {
  // ⚠ ОДИН тест на весь файл (не кілька окремих) — навмисно. Заголовок
  // відмови не залежить від `permission`, тож ДРУГИЙ рендер У ЦЬОМУ Ж
  // файлі з тим самим текстом одразу впав би на власній дедуплікації
  // `announceRoute` (`last` уже дорівнює цьому тексту після першого
  // рендера), а не на реальному регресі — той самий пастка, задокументована
  // вище і в `ChangePasswordPage.pageHeader.test.tsx`.
  it('заголовок відмови отримує фокус ПРОГРАМНО (не зупинка табу) і оголошується через aria-live (polite) — той самий ідіом, що й PageHeader', async () => {
    renderAccessDenied();

    const heading = screen.getByRole('heading');
    expect(document.activeElement).toBe(heading);
    expect(heading.getAttribute('tabindex')).toBe('-1');

    const live = screen.getByRole('status');
    expect(live.getAttribute('aria-live')).toBe('polite');

    await waitFor(() => {
      expect(live.textContent).toBe(heading.textContent);
    });
  });
});
