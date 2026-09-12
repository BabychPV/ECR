import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
import { AccessDeniedPage, RouteGuard } from '@/app/RouteGuard';
import type { RouteHandle } from '@/app/routes';
import { MeQueryKey } from '@/shared/session/useSession';
import { theme } from '@/shared/theme/theme';

/**
 * `RouteGuard` — рольовий гард маршруту (`PR nav-arch #4`, `Q-279`).
 *
 * ⚠ Тут перевіряється САМ гард, ізольовано від `router.tsx` (той самий
 * поділ, що й `AdminLayout.test.tsx`/`TemplateVersionLayout.test.tsx` для
 * `PR #2` — простий компонент отримує простий тест, без зайвого дерева
 * маршрутів). Інтеграція з РЕАЛЬНИМ деревом маршрутів (`createMemoryRouter`,
 * кілька захищених сторінок) — окремий файл,
 * `routeGuards.router.test.tsx`.
 *
 * Мутаційна перевірка (RED → GREEN, вручну, двічі, перед комітом):
 * 1. Тіло `RouteGuard` тимчасово зведено на `if (false && ...)` (гард
 *    завжди пропускає, «завжди дозволено») — 8 тестів упали: захищений
 *    `data-testid` знайшовся в дереві там, де мав бути відсутній.
 * 2. Умову інвертовано (`can(...)` без заперечення — гард забороняє САМЕ
 *    тому, у кого право Є, і пропускає того, у кого немає) — 12 із 14
 *    тестів упали, у ПРОТИЛЕЖНИЙ бік: користувач із правом бачив відмову,
 *    користувач без права — захищений вміст.
 * Обидва рази відновлено `if (handle.permission !== undefined &&
 * !can(session.data, handle.permission))` — GREEN, 14/14.
 */

function meWith(permissions: string[]): CurrentUserDto {
  return {
    userId: 1,
    userName: 'tester',
    language: 'en',
    permissions,
    denies: [],
    grants: {},
    isSimulation: false,
    simulatedForUserId: null,
    mustChangePassword: false,
  };
}

/** Будує `RouteHandle`, не додаючи `permission`, коли його немає
 *  (`exactOptionalPropertyTypes`: `{ permission: undefined }` — не те
 *  саме, що поле відсутнє взагалі). */
function handleWith(permission: string | undefined): RouteHandle {
  return permission === undefined ? { labelKey: 'nav.templates' } : { labelKey: 'nav.templates', permission };
}

function renderGuard(me: CurrentUserDto, permission: string | undefined): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(MeQueryKey, me);

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <RouteGuard handle={handleWith(permission)}>
            <div data-testid="protected">Захищений вміст</div>
          </RouteGuard>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('RouteGuard — рольовий гард маршруту (PR nav-arch #4)', () => {
  beforeEach(() => {
    // ⚠ `staleTime: 0` (`useSession.ts`) означає фоновий рефетч одразу після
    // монтування — навіть із засіяним кешем. Заглушка `fetch` повертає той
    // самий профіль, щоб рефетч не впав на реальну мережу в jsdom і не
    // перезаписав дані іншим об'єктом посеред тесту.
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 200, headers: { 'Content-Type': 'application/json' } })),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('право є — рендерить дочірній вміст, без сторінки відмови', () => {
    renderGuard(meWith(['Template.Edit']), 'Template.Edit');

    expect(screen.getByTestId('protected').textContent).toBe('Захищений вміст');
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('права немає — показує явну сторінку відмови, а не вміст і не порожній екран', () => {
    renderGuard(meWith([]), 'Template.Edit');

    expect(screen.queryByTestId('protected')).toBeNull();

    const denial = screen.getByRole('alert');
    expect(denial.textContent).not.toBe('');
    // ⚠ Код права видно в самій відмові (`ФВ-14.24` — не тупиковий екран без
    // пояснення, ЯКЕ саме право потрібне).
    expect(denial.textContent).toContain('Template.Edit');
  });

  it('запис без permission у handle пропускає вміст наскрізь, незалежно від прав користувача', () => {
    renderGuard(meWith([]), undefined);

    expect(screen.getByTestId('protected').textContent).toBe('Захищений вміст');
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('me ще не завантажено (кеш порожній) — гард зачинений за замовчуванням, не відкритий', () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    // ⛔ Кеш НЕ засіяний навмисно: перевіряється, що `can(undefined, ...)`
    // (гард без даних сесії) не пропускає захищений вміст «про всяк
    // випадок» — `RouteGuard` сам по собі зачинений, не лише завдяки тому,
    // що `AppLayout` блокує рендер `Outlet` до резолву сесії.
    render(
      <MantineProvider theme={theme}>
        <QueryClientProvider client={client}>
          <MemoryRouter>
            <RouteGuard handle={{ labelKey: 'nav.templates', permission: 'Template.Edit' }}>
              <div data-testid="protected">Захищений вміст</div>
            </RouteGuard>
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    expect(screen.queryByTestId('protected')).toBeNull();
  });
});

describe('AccessDeniedPage — сторінка відмови (PR nav-arch #4)', () => {
  it('показує код права і посилання на домашній маршрут, не порожній стан', () => {
    render(
      <MantineProvider theme={theme}>
        <MemoryRouter>
          <AccessDeniedPage permission="Security.ManageRoles" />
        </MemoryRouter>
      </MantineProvider>,
    );

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Security.ManageRoles');
    expect(screen.getByRole('link')).toBeTruthy();
  });
});
