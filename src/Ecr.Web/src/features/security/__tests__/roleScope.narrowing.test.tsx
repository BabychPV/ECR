import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { GrantableProject, GrantableSheet, RoleView, UserRoleAssignmentView, UserView } from '@/api/types';
import { GroupAssignmentsPanel } from '@/features/security/GroupAssignmentsPanel';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';
import { formatPeriod, parsePeriod, scopeToDto } from '@/features/security/roleScope';
import { testTheme } from '@/test/render';

/**
 * D-214 (ФВ-6.14): область ролі звужується аркушами й проміжком періодів —
 * три прості поля «Проєкти», «Аркуші», «Періоди з … по …».
 *
 * ⚠ МУТАЦІЇ: `scopeToDto` без `sheets`/`periods` — червоніють тести PUT і POST;
 * `parsePeriod` без року-номера (`2026-01`) — червоніє PUT; кнопка «Зберегти»
 * без `scopesValid` — червоніє тест некоректної межі.
 */

const projects: GrantableProject[] = [
  { id: 5, code: 'P5', nameL10n: { values: { en: 'Flare north' } } },
  { id: 6, code: 'P6', nameL10n: { values: { en: 'Flare south' } } },
];

const sheets: GrantableSheet[] = [
  { projectId: 5, code: 'F1', nameL10n: { values: { en: 'Emissions' } } },
  { projectId: 6, code: 'F1', nameL10n: { values: { en: 'Emissions' } } },
  { projectId: 6, code: 'F2', nameL10n: { values: { en: 'Waste' } } },
];

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

let sent: { url: string; method: string; body: Record<string, unknown> }[] = [];

function stubFetch(fallback: unknown, assignments: UserRoleAssignmentView[] = []): void {
  sent = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      if (method !== 'GET') {
        sent.push({ url, method, body: JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown> });
        if (method === 'POST') {
          return json({ id: 9, effectiveAfterNextSignIn: false, principalName: null, principalSid: 'S-1-5-21-9' }, 201);
        }
        return url.includes('/roles') ? json({ roles: 1 }) : new Response(null, { status: 204 });
      }

      if (url.includes('/security/projects')) return json(projects);
      if (url.includes('/security/project-sheets')) return json(sheets);
      if (url.includes('/role-assignments')) return json(assignments);

      return json(fallback);
    }),
  );
}

function renderWith(node: JSX.Element): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        {node}
      </QueryClientProvider>
    </MantineProvider>,
  );
}

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

const roles: RoleView[] = [
  { id: 3, code: 'Operators', isActive: true, isBuiltIn: false, permissions: ['Document.View'], dangerousPermissions: [] },
];

const field = (name: string): Promise<HTMLElement> => screen.findByRole('textbox', { name });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('межі періоду й форма області (D-214)', () => {
  it('ключ періоду з тексту людини і назад', () => {
    expect(parsePeriod('2026-01')).toBe(202601);
    expect(parsePeriod(' 2026/1 ')).toBe(202601);
    expect(parsePeriod('202612')).toBe(202612);
    expect(parsePeriod('')).toBeNull();
    expect(parsePeriod('26-1')).toBeUndefined();
    expect(parsePeriod('2026-00')).toBeUndefined();
    expect(formatPeriod(202603)).toBe('2026-03');
    expect(formatPeriod(null)).toBe('');
  });

  it('область лише з проєктами — дослівно та сама, що до D-214', () => {
    expect(scopeToDto({ projects: [6, 5], sheets: [], from: '', to: '' })).toEqual({ projects: [5, 6] });
    expect(scopeToDto({ projects: [], sheets: ['F1'], from: '2026-01', to: '' })).toBeNull();
  });
});

describe('UserAccessEditor: аркуші й періоди (D-214)', () => {
  it('аркуш і проміжок періодів ідуть у PUT полями sheets і periods', async () => {
    stubFetch(['Operators']);
    renderWith(<UserAccessEditor user={user} roles={roles} onClose={() => {}} />);

    const sheetsField = await field('⟦security.scopeSheets⟧ · Operators');
    expect(sheetsField.hasAttribute('disabled')).toBe(true);

    fireEvent.click(await field('⟦security.scopeProjects⟧ · Operators'));
    fireEvent.click(await screen.findByRole('option', { name: 'Flare south (P6)' }));

    await waitFor(() => expect(sheetsField.hasAttribute('disabled')).toBe(false));
    fireEvent.click(sheetsField);
    fireEvent.click(await screen.findByRole('option', { name: 'Waste (F2)' }));

    fireEvent.change(await field('⟦security.scopePeriodFrom⟧ · Operators'), { target: { value: '2026-01' } });
    fireEvent.change(await field('⟦security.scopePeriodTo⟧ · Operators'), { target: { value: '2026-06' } });

    expect(screen.getByText('⟦security.scopeNarrowedWarning⟧')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));
    await waitFor(() => expect(sent.some((s) => s.url.endsWith('/users/7/roles'))).toBe(true));

    expect(sent.find((s) => s.url.endsWith('/users/7/roles'))?.body).toEqual({
      roleCodes: ['Operators'],
      scopes: { Operators: { projects: [6], sheets: ['F2'], periods: { from: 202601, to: 202606 } } },
    });
  });

  it('збережена область показана у полях; незмінена — словника в PUT немає', async () => {
    stubFetch(['Operators'], [
      {
        roleCode: 'Operators',
        validFrom: null,
        validTo: null,
        scope: { projects: [6], sheets: ['F2'], periods: { from: 202601, to: null } },
      },
    ]);
    renderWith(<UserAccessEditor user={user} roles={roles} onClose={() => {}} />);

    await waitFor(async () =>
      expect((await field('⟦security.scopePeriodFrom⟧ · Operators')).getAttribute('value')).toBe('2026-01'),
    );
    expect(screen.getByText('Waste (F2)')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));
    await waitFor(() => expect(sent.some((s) => s.url.endsWith('/users/7/roles'))).toBe(true));

    expect(sent.find((s) => s.url.endsWith('/users/7/roles'))?.body).toEqual({ roleCodes: ['Operators'] });
  });

  it('межа, що не є періодом, — помилка біля поля, зберегти не можна', async () => {
    stubFetch(['Operators']);
    renderWith(<UserAccessEditor user={user} roles={roles} onClose={() => {}} />);

    fireEvent.click(await field('⟦security.scopeProjects⟧ · Operators'));
    fireEvent.click(await screen.findByRole('option', { name: 'Flare south (P6)' }));

    const from = await field('⟦security.scopePeriodFrom⟧ · Operators');
    await waitFor(() => expect(from.hasAttribute('disabled')).toBe(false));
    fireEvent.change(from, { target: { value: '26-1' } });

    await waitFor(() => expect(from.getAttribute('aria-invalid')).toBe('true'));
    expect(screen.getByRole('button', { name: '⟦common.save⟧' }).hasAttribute('disabled')).toBe(true);
  });
});

describe('GroupAssignmentsPanel: аркуші й періоди (D-214)', () => {
  const rows = [
    {
      id: 1,
      roleId: 3,
      roleCode: 'Operators',
      principalSid: 'S-1-5-21-1',
      principalName: 'CORP\\North',
      validFrom: null,
      validTo: null,
      scope: { projects: [5], sheets: ['F1'], periods: { from: 202601, to: null } },
    },
  ];

  it('колонка «Область» називає аркуші й періоди', async () => {
    stubFetch(rows);
    renderWith(<GroupAssignmentsPanel roles={roles} />);

    const row = (await screen.findByText('CORP\\North')).closest('tr');
    await waitFor(() => expect(row?.querySelector('[data-scope="sheets"]')).toBeTruthy());
    expect(row?.querySelector('[data-scope="periods"]')).toBeTruthy();
  });

  it('аркуш і лише «по» ідуть у POST; «з» — відкрита межа', async () => {
    stubFetch(rows);
    renderWith(<GroupAssignmentsPanel roles={roles} />);

    await screen.findByText('CORP\\North');
    fireEvent.click(screen.getByRole('textbox', { name: '⟦security.role⟧' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Operators' }));
    fireEvent.change(screen.getByRole('textbox', { name: /groupRoles\.principal/ }), {
      target: { value: 'CORP\\South' },
    });

    fireEvent.click(screen.getByRole('textbox', { name: '⟦security.scopeProjects⟧' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Flare south (P6)' }));
    fireEvent.click(screen.getByRole('textbox', { name: '⟦security.scopeSheets⟧' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Waste (F2)' }));
    fireEvent.change(screen.getByRole('textbox', { name: '⟦security.scopePeriodTo⟧' }), {
      target: { value: '2026-12' },
    });

    fireEvent.click(screen.getByRole('button', { name: '⟦groupRoles.assign⟧' }));
    await waitFor(() => expect(sent.filter((s) => s.method === 'POST')).toHaveLength(1));

    expect(sent.find((s) => s.method === 'POST')?.body).toEqual({
      roleId: 3,
      principal: 'CORP\\South',
      confirmDangerous: false,
      scope: { projects: [6], sheets: ['F2'], periods: { from: null, to: 202612 } },
    });
  });
});
