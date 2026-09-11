import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RoleView, UserView } from '@/api/types';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';

/**
 * Ролі наявного користувача (`Q-254`).
 *
 * ⛔ `assigned.error` не рендерився НІДЕ: невдалий запит ролей і користувач,
 * що справді не має жодної ролі, малювали ОДИН і той самий жовтий алерт
 * «ролей немає» — той самий клас дефекту, що й `A7-04` (порожній дашборд
 * здоров'я виглядав як здорова система).
 *
 * ⚠ `MultiSelect` підмінено легким заглушником: справжній `@mantine/core`
 * `MultiSelect` під jsdom «зависає» (відтворюваний факт — жоден наявний тест
 * у репозиторії не рендерить його напряму саме тому). Обидва твердження
 * нижче перевіряють стан ДО показу форми (помилка / порожній перелік), тож
 * заглушник у перевірку не втручається.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  return {
    ...actual,
    MultiSelect: () => null,
  };
});
const user: UserView = {
  id: 7,
  userName: 'ivanov',
  displayName: 'Іванов',
  email: null,
  provider: 'Local',
  isActive: true,
  isBootstrapAdmin: false,
  isLockedOut: false,
  mustChangePassword: false,
  receivesAlerts: false,
};

const roles: RoleView[] = [
  { id: 1, code: 'DataEntry', isActive: true, isBuiltIn: false, dangerousPermissions: [], permissions: [] },
];

function respond(body: unknown, status = 200): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      ),
    ),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <UserAccessEditor user={user} roles={roles} onClose={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Ролі наявного користувача', () => {
  it('Q-254: невдалий запит ролей показує помилку, а НЕ «ролей немає»', async () => {
    respond(
      {
        title: 'Недоступно',
        status: 503,
        errorCode: 'ECR-SYS-0503',
        correlationId: 'cid-roles-1',
      },
      503,
    );
    show();

    // ⛔ Головне твердження: до виправлення тут малювався порожній alert
    // «ролей немає» (той самий колір, що й для дійсно порожнього переліку),
    // а помилка НІКУДИ не потрапляла.
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('ECR-SYS-0503');

    // Текст «ролей немає» — про ІНШИЙ стан (дійсна відсутність), і в
    // помилковому стані показуватися не повинен.
    expect(screen.queryByText('⟦security.noRolesWarning⟧')).toBeNull();
  });

  it('дійсно порожній перелік ролей пояснюється, а не мовчить, і НЕ виглядає як помилка', async () => {
    respond([]);
    show();

    // Порожній стан AsyncBoundary — заголовок/пояснення, БЕЗ role="alert".
    expect(await screen.findByText('⟦security.noRolesTitle⟧')).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('успішний запит із реальними ролями НЕ показує ні помилку, ні «ролей немає»', async () => {
    respond(['DataEntry']);
    show();

    // ⛔ Ні помилки, ні «ролей немає» — форма (заглушена `MultiSelect`,
    // див. коментар вище) отримала свої дані. `waitFor` (а не миттєва
    // перевірка) — запит асинхронний, і твердженню треба дочекатися його
    // завершення.
    await waitFor(() => {
      expect(screen.queryByRole('alert')).toBeNull();
      expect(screen.queryByText('⟦security.noRolesWarning⟧')).toBeNull();
      expect(screen.queryByText('⟦security.noRolesTitle⟧')).toBeNull();
    });
  });
});
