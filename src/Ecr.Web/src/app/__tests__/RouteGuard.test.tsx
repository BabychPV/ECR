import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
import { RouteGuard } from '@/app/RouteGuard';
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
 * ✎ **`UI-09`, L-правило про доступ.** Гард більше не рендерить відмову
 * inline — він робить `<Navigate to="/403" .../>` (`RouteGuard.tsx`). Тому
 * дерево тестів тут несе МІНІМАЛЬНУ пару маршрутів (`/guarded` і `/403`), а
 * не голий `<MemoryRouter>` навколо самого гарда: без цільового маршруту
 * `<Navigate>` не мала б на що навігувати, і тест не міг би відрізнити
 * «гард пропустив» від «гард спробував редиректнути на адресу, якої в
 * дереві немає». Сам вміст сторінки `/403` (`ForbiddenPage`) перевіряється
 * окремо, `ForbiddenPage.test.tsx` — тут важливо лише, ЩО гард туди
 * НАВІГУЄ, а не як виглядає сторінка призначення.
 *
 * Мутаційна перевірка (RED → GREEN, вручну, двічі, перед комітом):
 * 1. Тіло `RouteGuard` тимчасово зведено на `if (false && ...)` (гард
 *    завжди пропускає, «завжди дозволено») — тести впали: захищений
 *    `data-testid` знайшовся в дереві там, де мав бути відсутній.
 * 2. Умову інвертовано (`can(...)` без заперечення — гард забороняє САМЕ
 *    тому, у кого право Є, і пропускає того, у кого немає) — тести впали,
 *    у ПРОТИЛЕЖНИЙ бік: користувач із правом потрапляв на `/403`,
 *    користувач без права бачив захищений вміст.
 * Обидва рази відновлено `if (handle.permission !== undefined &&
 * !can(session.data, handle.permission))` — GREEN.
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

/**
 * `/guarded` монтує `RouteGuard` над захищеним вмістом; `/403` — маркер
 * призначення редиректу (НЕ справжня `ForbiddenPage` — та має власний файл
 * тестів). Обидва — прямі діти `MemoryRouter`, той самий мінімальний
 * підхід, що й у `routeGuards.router.test.tsx`.
 */
function renderGuard(me: CurrentUserDto, permission: string | undefined): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(MeQueryKey, me);

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/guarded']}>
          <Routes>
            <Route
              path="/guarded"
              element={
                <RouteGuard handle={handleWith(permission)}>
                  <div data-testid="protected">Захищений вміст</div>
                </RouteGuard>
              }
            />
            <Route path="/403" element={<div data-testid="forbidden-route">Forbidden</div>} />
          </Routes>
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

  it('право є — рендерить дочірній вміст, без редиректу на /403', () => {
    renderGuard(meWith(['Template.Edit']), 'Template.Edit');

    expect(screen.getByTestId('protected').textContent).toBe('Захищений вміст');
    expect(screen.queryByTestId('forbidden-route')).toBeNull();
  });

  it('права немає — навігує на /403 (не рендерить захищений вміст, не лишається на місці)', () => {
    renderGuard(meWith([]), 'Template.Edit');

    expect(screen.queryByTestId('protected')).toBeNull();
    expect(screen.getByTestId('forbidden-route')).toBeTruthy();
  });

  it('запис без permission у handle пропускає вміст наскрізь, незалежно від прав користувача', () => {
    renderGuard(meWith([]), undefined);

    expect(screen.getByTestId('protected').textContent).toBe('Захищений вміст');
    expect(screen.queryByTestId('forbidden-route')).toBeNull();
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

// ⛔ Сторінка призначення (`ForbiddenPage`, вміст `/403`) переїхала в
// окремий файл, `ForbiddenPage.test.tsx` (`UI-09`) — той самий поділ, що
// вже описаний у коментарі на початку файлу: цей файл перевіряє лише, ЩО
// `RouteGuard` навігує на `/403`, не як виглядає сама сторінка призначення.
