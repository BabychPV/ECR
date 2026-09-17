import type { JSX } from 'react';
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
 * ⚠ `MultiSelect` (`@mantine/core`) під jsdom «зависає» — той самий
 * відтворюваний факт, що й `Select` (`Q-299`, `GrantsPanel.resourceName.
 * test.tsx`, `ColumnEditor.registryLookup.test.tsx`). Стаб тут — кнопка
 * «очистити ролі», що напряму викликає `onChange([])`: цього досить, щоб
 * відтворити ТОЧНО той самий виклик, що й зняття останнього чіпа
 * мишкою/клавіатурою в реальному компоненті, без порталу й floating-ui.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  function StubMultiSelect(props: {
    label?: string;
    value?: string[];
    onChange?: (value: string[]) => void;
  }): JSX.Element {
    return (
      <div>
        <div aria-label={props.label} data-testid="roles-value">
          {(props.value ?? []).join(',')}
        </div>
        <button type="button" onClick={() => props.onChange?.([])}>
          {`${props.label ?? 'roles'} — clear all`}
        </button>
      </div>
    );
  }

  return { ...actual, MultiSelect: StubMultiSelect };
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

describe('UserAccessEditor: очищення всіх ролей не ховає контроль вибору (lane1)', () => {
  it('зняття останньої ролі лишає MultiSelect на місці, поряд із попередженням', async () => {
    respond(['DataEntry']);
    show();

    // Дочекатися завантаження призначених ролей.
    await waitFor(() => {
      expect(screen.getByTestId('roles-value').textContent).toBe('DataEntry');
    });

    // Адмін знімає останню роль — саме та дія з репро аудиту.
    fireEvent.click(screen.getByText(/clear all/));

    // ⛔ Мутаційний доказ (RED на невиправленому коді): до фіксу цей клік
    // прибирав `MultiSelect` із DOM ЦІЛКОМ (замінений на нередаговуваний
    // текст) — цей запит кинув би виняток «not found».
    expect(screen.getByTestId('roles-value')).toBeDefined();
    expect(screen.getByTestId('roles-value').textContent).toBe('');

    // Попередження показується ПОРЯД, не ЗАМІСТЬ контролю.
    expect(screen.getByText('⟦security.noRolesTitle⟧')).toBeDefined();

    // Контроль лишається інтерактивним — можна клікнути ще раз, нічого не
    // впало.
    fireEvent.click(screen.getByText(/clear all/));
    expect(screen.getByTestId('roles-value')).toBeDefined();
  });
});
