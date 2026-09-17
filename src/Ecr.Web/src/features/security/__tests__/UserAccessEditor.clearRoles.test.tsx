import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RoleView, UserView } from '@/api/types';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';
import { testTheme } from '@/test/render';

/**
 * UI-аудит, lane 1: «Clearing all roles in the user "Access" modal removes
 * the only way to add a role back»
 * (`docs/build/audit-drafts/lane1-access-modal-roles-vanish.md`, severity
 * High).
 *
 * ⛔ Корінь — `AsyncBoundary`'s `isEmpty={() => selected.length === 0}`
 * перевіряв ЖИВИЙ стан форми (React-стан `selected`), а не відповідь
 * сервера. Щойно адмін знімав останню роль-чіп у `MultiSelect`,
 * `AsyncBoundary` миттю ХОВАВ сам `MultiSelect` і малював натомість
 * нередаговуваний попереджувальний текст — без жодного контролю, щоб
 * додати роль назад. Єдиний вихід був «Cancel», що відкидав УСІ зміни
 * сеансу (включно з правкою email).
 *
 * ✎ Тут `MultiSelect` підмінявся кнопкою «очистити ролі», що напряму
 * викликала `onChange([])` — нібито тому, що справжній «зависає під jsdom»
 * (`Q-299`). Причина зависання знайдена й усунена: взаємна рекурсія
 * jsdom ↔ nwsapi на станових псевдокласах (коментар у `src/test/setup.ts`).
 * Роль тепер знімається тим самим хрестиком на «пігулці», що й мишкою.
 */

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

function respond(body: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <UserAccessEditor user={user} roles={roles} onClose={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const RolesLabel = '⟦security.roles⟧';

/** Обрані ролі так, як їх показує `MultiSelect` — «пігулками» над полем. */
function selectedRoles(): string[] {
  return Array.from(document.querySelectorAll('.mantine-Pill-label')).map(
    (pill) => pill.textContent ?? '',
  );
}

/** Хрестики зняття ролі на самих «пігулках». */
function roleRemoveButtons(): HTMLElement[] {
  return Array.from(document.querySelectorAll<HTMLElement>('.mantine-Pill-remove'));
}

describe('UserAccessEditor: очищення всіх ролей не ховає контроль вибору (lane1)', () => {
  it('зняття останньої ролі лишає MultiSelect на місці, поряд із попередженням', async () => {
    respond(['DataEntry']);
    show();

    // Дочекатися завантаження призначених ролей.
    await waitFor(() => {
      expect(selectedRoles()).toEqual(['DataEntry']);
    });

    // Адмін знімає останню роль — саме та дія з репро аудиту, зроблена тим
    // самим хрестиком на «пігулці», що й мишкою в браузері.
    fireEvent.click(roleRemoveButtons()[0] as HTMLElement);

    // ⛔ Мутаційний доказ (RED на невиправленому коді): до фіксу цей клік
    // прибирав `MultiSelect` із DOM ЦІЛКОМ (замінений на нередаговуваний
    // текст) — цей запит кинув би виняток «not found».
    const rolesField = screen.getByLabelText(RolesLabel);
    expect(rolesField).toBeDefined();
    expect(selectedRoles()).toEqual([]);

    // Попередження показується ПОРЯД, не ЗАМІСТЬ контролю.
    expect(screen.getByText('⟦security.noRolesTitle⟧')).toBeDefined();

    // Контроль лишається інтерактивним — клік розкриває список ролей, тобто
    // роль СПРАВДІ можна додати назад, а не лише «поле ще в DOM».
    fireEvent.click(rolesField);
    expect(screen.getByRole('option', { name: 'DataEntry' })).toBeDefined();
  });
});
