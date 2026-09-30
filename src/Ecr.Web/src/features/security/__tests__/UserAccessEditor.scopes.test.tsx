import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { GrantableProject, RoleView, UserRoleAssignmentView, UserView } from '@/api/types';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';
import { testTheme } from '@/test/render';

/**
 * ФВ-6.14: область дії ролі у формі доступу користувача.
 *
 * ⛔ `PUT …/roles` зі словником `scopes` — повна відповідь: роль без запису
 * діє скрізь. Тести тримають три речі: (1) змінену область форма шле повним
 * словником, разом з областями ІНШИХ ролей; (2) незмінену — не шле зовсім
 * (сервер зберігає як є); (3) порожня область показана як «усі проєкти».
 *
 * ⚠ МУТАЦІЇ: `scopesToSend` → завжди `undefined` — червоніє (1); прибрати
 * порівняння з базою (слати завжди) — червоніє (2); у `useUserRoleScopes`
 * не класти збережені області в базу — червоніють (1) і показ області.
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

const roles = [role(1, 'DataEntry'), role(2, 'Approver')];

const projects: GrantableProject[] = [
  { id: 5, code: 'P5', nameL10n: { values: { en: 'Flare north' } } },
  { id: 6, code: 'P6', nameL10n: { values: { en: 'Flare south' } } },
];

const assignments: UserRoleAssignmentView[] = [
  { roleCode: 'DataEntry', validFrom: null, validTo: null, scope: { projects: [5] } },
  { roleCode: 'Approver', validFrom: null, validTo: null, scope: null },
  // Строкова підміна — форма її не редагує, її область не входить у словник.
  { roleCode: 'Approver', validFrom: '2026-01-01', validTo: '2026-01-31', scope: { projects: [6] } },
];

let puts: { url: string; body: Record<string, unknown> }[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(options: { assignmentsFail?: boolean; putRoles?: () => Response } = {}): void {
  puts = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (init?.method === 'PUT') {
        puts.push({ url, body: JSON.parse(String(init.body)) as Record<string, unknown> });
        if (url.includes('/roles')) return options.putRoles?.() ?? json({ roles: 2 });
        return new Response(null, { status: 204 });
      }

      if (url.includes('/role-assignments')) {
        return options.assignmentsFail === true
          ? json({ title: 'Boom', status: 500, errorCode: 'ECR-SYS-0500', correlationId: 'c' }, 500)
          : json(assignments);
      }

      if (url.includes('/security/projects')) return json(projects);

      return json(['Approver', 'DataEntry']);
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

const projectsField = (code: string): Promise<HTMLElement> =>
  screen.findByRole('textbox', { name: `⟦security.scopeProjects⟧ · ${code}` });

function rolesPut(): Record<string, unknown> {
  const put = puts.find((p) => p.url.endsWith('/users/7/roles'));
  expect(put).toBeDefined();

  return put?.body ?? {};
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UserAccessEditor: область дії ролі (ФВ-6.14)', () => {
  it('збережена область показана назвою проєкту; роль без області — «усі проєкти»', async () => {
    mockServer();
    show();

    await projectsField('DataEntry');
    await waitFor(() => expect(screen.getByText('Flare north (P5)')).toBeTruthy());

    // Одна роль без області — одна позначка «усі проєкти».
    expect(document.querySelectorAll('[data-scope="all"]')).toHaveLength(1);
    expect(screen.getByText('⟦security.scopeAllProjects⟧')).toBeTruthy();

    // Роль з областю — попередження про глобальні права.
    expect(screen.getByText('⟦security.scopeGlobalWarning⟧')).toBeTruthy();
  });

  it('додана область іде в PUT повним словником — разом з областю іншої ролі', async () => {
    mockServer();
    show();

    fireEvent.click(await projectsField('Approver'));
    fireEvent.click(await screen.findByRole('option', { name: 'Flare south (P6)' }));

    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));
    await waitFor(() => expect(puts.length).toBeGreaterThan(0));

    expect(rolesPut()).toEqual({
      roleCodes: ['Approver', 'DataEntry'],
      scopes: { Approver: { projects: [6] }, DataEntry: { projects: [5] } },
    });
  });

  it('знята область (порожнє поле) — роль без запису в словнику, тобто всі проєкти', async () => {
    mockServer();
    show();

    await waitFor(() => expect(screen.getByText('Flare north (P5)')).toBeTruthy());

    // Кнопка очищення поля DataEntry — єдина в поля з вибраним значенням.
    const field = await projectsField('DataEntry');
    const clear = field.closest('.mantine-InputWrapper-root')?.querySelector('button');
    expect(clear).not.toBeNull();
    fireEvent.click(clear as Element);

    await waitFor(() => expect(document.querySelectorAll('[data-scope="all"]')).toHaveLength(2));

    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));
    await waitFor(() => expect(puts.length).toBeGreaterThan(0));

    expect(rolesPut()).toEqual({ roleCodes: ['Approver', 'DataEntry'], scopes: {} });
  });

  it('область не змінено — словника в PUT немає, сервер зберігає області як є', async () => {
    mockServer();
    show();

    await waitFor(() => expect(screen.getByText('Flare north (P5)')).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));
    await waitFor(() => expect(puts.length).toBeGreaterThan(0));

    expect(rolesPut()).toEqual({ roleCodes: ['Approver', 'DataEntry'] });
  });

  it('області не прочиталися — поля немає, пояснення є, словник не шлеться', async () => {
    mockServer({ assignmentsFail: true });
    show();

    await waitFor(() => expect(screen.getByText('⟦security.scopeUnavailable⟧')).toBeTruthy());
    expect(screen.queryByRole('textbox', { name: '⟦security.scopeProjects⟧ · DataEntry' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));
    await waitFor(() => expect(puts.length).toBeGreaterThan(0));

    expect(rolesPut()).not.toHaveProperty('scopes');
  });

  it('403 noProjectManageGrant — відмова біля поля тієї ролі, чию область відхилено', async () => {
    mockServer({
      putRoles: () =>
        json(
          {
            title: 'Forbidden',
            status: 403,
            detail: 'You have no Manage grant on project 6.',
            errorCode: 'ECR-AUTH-0403',
            correlationId: 'c',
            messageKey: 'err.ECR-AUTH-0403.noProjectManageGrant',
            projectId: '6',
          },
          403,
        ),
    });
    show();

    fireEvent.click(await projectsField('Approver'));
    fireEvent.click(await screen.findByRole('option', { name: 'Flare south (P6)' }));
    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));

    const field = await projectsField('Approver');
    await waitFor(() => expect(field.getAttribute('aria-invalid')).toBe('true'));
    expect((await projectsField('DataEntry')).getAttribute('aria-invalid')).not.toBe('true');
  });
});
