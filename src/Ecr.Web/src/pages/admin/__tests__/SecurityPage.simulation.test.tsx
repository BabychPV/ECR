import type { JSX } from 'react';
import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import type { RoleView } from '@/api/types';
import { GrantsPanel } from '@/pages/admin/GrantsPanel';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { withTestDefaults } from '@/test/render';

/**
 * Аудит L9-18: під симуляцією «очима користувача» екрани безпеки — лише перегляд.
 *
 * ⛔ Сервер під симуляцією відхиляє КОЖЕН не-GET (`SimulationReadOnlyMiddleware`, `ECR-SIM-0403`),
 * а `can()` бачить права ЦІЛІ. Доти сторінка показувала «створити», перемикачі, «Доступ», гранти
 * з «Додати»/«Зберегти» — і кожна дія закінчувалась 403. Контрольний випадок без симуляції
 * доводить, що тест не зелений «бо нічого не намальовано».
 */
const Users = {
  items: [
    {
      id: 1,
      userName: 'first.user',
      displayName: 'First User',
      provider: 'Windows',
      email: 'first@example.com',
      isActive: true,
      isBootstrapAdmin: false,
      isLockedOut: false,
      mustChangePassword: false,
      receivesAlerts: false,
    },
  ],
  nextCursor: null,
  totalCount: 1,
};

const Roles: RoleView[] = [
  { id: 10, code: 'Auditor', isActive: true, isBuiltIn: false, permissions: [], dangerousPermissions: [] },
];

const Grants = [{ resourceKind: 'Project', resourceId: 5, level: 'Read', isDeny: false, resourceName: 'P5' }];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function stubFetch(isSimulation: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Security.ManageUsers', 'Security.ManageRoles', 'Security.Simulate'],
          simulatedForUserId: isSimulation ? 4 : null,
          userId: 9,
          userName: 'tester',
        });
      }
      if (url.includes('/grants')) return json(Grants);
      if (url.includes('/roles')) return json(Roles);
      if (url.includes('/users')) return json(Users);

      return json([]);
    }),
  );
}

function wrap(node: JSX.Element): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/security?tab=users']}>{node}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SecurityPage: L9-18 — симуляція лише для перегляду', () => {
  it('без симуляції — створення, «Доступ» і перемикач алертів доступні (контроль)', async () => {
    stubFetch(false);
    wrap(<SecurityPage />);

    await screen.findByText('first.user');
    await waitFor(() => expect(screen.getByRole('button', { name: /security\.createUser/ })).toBeDefined());
    expect(screen.queryByTestId('security-simulation-read-only')).toBeNull();
    expect((screen.getByLabelText(/security\.alerts.*first\.user/) as HTMLInputElement).disabled).toBe(false);
    expect(screen.getByRole('button', { name: '⟦security.simulate⟧' })).toBeDefined();
  });

  it('під симуляцією — банер, без «створити», «Доступ» і перемикач неактивні, без запуску симуляції', async () => {
    stubFetch(true);
    wrap(<SecurityPage />);

    await screen.findByText('first.user');
    await screen.findByTestId('security-simulation-read-only');
    expect(screen.queryByRole('button', { name: /security\.createUser/ })).toBeNull();
    expect((screen.getByLabelText(/security\.alerts.*first\.user/) as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: /security\.access/ }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole('button', { name: '⟦security.simulate⟧' })).toBeNull();
  });
});

describe('GrantsPanel: L9-18 — під симуляцією гранти лише для перегляду', () => {
  async function pickRole(): Promise<void> {
    fireEvent.click(await screen.findByLabelText(/security\.role/));
    fireEvent.click(await screen.findByRole('option', { name: /Auditor/ }));
  }

  it('без симуляції — «Додати», «Прибрати» є (контроль)', async () => {
    stubFetch(false);
    wrap(<GrantsPanel roles={Roles} />);

    await pickRole();
    await screen.findByText('P5');
    await waitFor(() => expect(screen.getByRole('button', { name: /grants\.add/ })).toBeDefined());
    expect(screen.getByRole('button', { name: /grants\.remove/ })).toBeDefined();
  });

  it('під симуляцією — без «Додати»/«Зберегти»/«Прибрати», поля readOnly, перемикач заборони неактивний', async () => {
    stubFetch(true);
    wrap(<GrantsPanel roles={Roles} />);

    await pickRole();
    await screen.findByText('P5');
    await waitFor(() =>
      expect((screen.getByRole('switch', { name: /grants\.deny/ }) as HTMLInputElement).disabled).toBe(true),
    );
    expect(screen.queryByRole('button', { name: /grants\.add/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /common\.save/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /grants\.remove/ })).toBeNull();
    expect((screen.getByRole('textbox', { name: /grants\.level/ }) as HTMLInputElement).readOnly).toBe(true);
    expect((screen.getByRole('textbox', { name: /grants\.pickerProject/ }) as HTMLInputElement).readOnly).toBe(true);
  });
});
