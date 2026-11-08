import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RoleView, UserRoleAssignmentView, UserView } from '@/api/types';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';
import { isExpired } from '@/features/security/DatedRoleNotices';
import { testTheme } from '@/test/render';

/**
 * P1 (людина 08.11, кореляція e16184de): роль, чия строкова підміна СПЛИЛА, у формі доступу
 * виглядала ВІДСУТНЬОЮ (форма читала лише безстрокові `GET …/roles`). Адміністратор додавав її
 * знову — і отримував «Internal error».
 *
 * ⚠ МУТАЦІЇ: прибрати `<DatedRoleNotices>` з `UserAccessEditor` — червоніють «сплила роль
 * показана» і «re-assign → PUT»; `onReassign` не додає код у `selected` — червоніє «re-assign →
 * PUT»; `isExpired` з `<=` замість `<` — червоніє межа «останній день ще чинний».
 */

const user: UserView = {
  id: 3,
  userName: 'land-owner',
  displayName: 'Land owner',
  email: null,
  provider: 'Local',
  isActive: true,
  isBootstrapAdmin: false,
  isLockedOut: false,
  mustChangePassword: false,
  receivesAlerts: false,
  lastSignInAt: null,
};

const role = (id: number, code: string): RoleView => ({
  id,
  code,
  isActive: true,
  isBuiltIn: false,
  dangerousPermissions: [],
  permissions: ['Document.View'],
});

const roles = [role(1, 'DataEntry'), role(11, 'LandRestricted'), role(12, 'Approver')];

let assignments: UserRoleAssignmentView[] = [];
let puts: { url: string; body: Record<string, unknown> }[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(rows: UserRoleAssignmentView[]): void {
  assignments = rows;
  puts = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (init?.method === 'PUT') {
        puts.push({ url, body: JSON.parse(String(init.body)) as Record<string, unknown> });
        if (url.includes('/roles')) return json({ roles: 2 });
        return new Response(null, { status: 204 });
      }

      if (url.includes('/role-assignments')) return json(assignments);
      if (url.includes('/security/projects')) return json([]);

      // `GET …/roles` — лише безстрокові.
      return json(
        assignments.filter((a) => a.validFrom === null && a.validTo === null).map((a) => a.roleCode),
      );
    }),
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

const expiredRow = (code: string): Promise<HTMLElement> =>
  waitFor(() => {
    const row = document.querySelector<HTMLElement>(`[data-role-validity="expired"][data-role-code="${code}"]`);
    expect(row).not.toBeNull();

    return row as HTMLElement;
  });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UserAccessEditor: сплила строкова роль (P1 08.11)', () => {
  it('сплила роль показана як «expired <дата>, re-assign?», а не відсутня', async () => {
    mockServer([
      { roleCode: 'DataEntry', validFrom: null, validTo: null, scope: null },
      { roleCode: 'LandRestricted', validFrom: null, validTo: '2026-10-26', scope: null },
    ]);
    show();

    const row = await expiredRow('LandRestricted');
    expect(within(row).getByText(/^⟦security\.roleExpiredOn \(date=/)).toBeTruthy();
    expect(within(row).getByText(/^⟦security\.roleExpiredReassign \(role=LandRestricted, date=/)).toBeTruthy();
    expect(within(row).getByRole('button', { name: '⟦security.reassignRoleNamed (role=LandRestricted)⟧' })).toBeTruthy();
  });

  it('re-assign додає роль у набір, і Save шле її в PUT …/roles', async () => {
    mockServer([
      { roleCode: 'DataEntry', validFrom: null, validTo: null, scope: null },
      { roleCode: 'LandRestricted', validFrom: null, validTo: '2026-10-26', scope: null },
    ]);
    show();

    const row = await expiredRow('LandRestricted');
    fireEvent.click(within(row).getByRole('button', { name: '⟦security.reassignRoleNamed (role=LandRestricted)⟧' }));

    // Після натиску — «буде призначено знову при збереженні», кнопки вже немає.
    await waitFor(() => expect(within(row).getByText(/^⟦security\.roleReassignPending \(role=LandRestricted, date=/)).toBeTruthy());
    expect(within(row).queryByRole('button')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));
    await waitFor(() => expect(puts.some((p) => p.url.endsWith('/users/3/roles'))).toBe(true));

    const put = puts.find((p) => p.url.endsWith('/users/3/roles'));
    expect(put?.body['roleCodes']).toEqual(['DataEntry', 'LandRestricted']);
  });

  it('чинна строкова роль — «valid until», без re-assign', async () => {
    mockServer([{ roleCode: 'Approver', validFrom: null, validTo: '2099-12-31', scope: null }]);
    show();

    await waitFor(() => expect(screen.getByText(/^⟦security\.roleValidUntil \(role=Approver, date=/)).toBeTruthy());
    expect(document.querySelector('[data-role-validity="expired"]')).toBeNull();
    expect(screen.queryByRole('button', { name: /reassignRoleNamed/ })).toBeNull();
  });

  it('межа: останній день дії ще чинний, наступного — сплила', () => {
    expect(isExpired({ validTo: '2026-10-26' }, '2026-10-26')).toBe(false);
    expect(isExpired({ validTo: '2026-10-26' }, '2026-10-27')).toBe(true);
    expect(isExpired({ validTo: null }, '2026-10-27')).toBe(false);
  });
});
