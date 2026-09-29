import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { GrantableProject, RoleView } from '@/api/types';
import { GroupAssignmentsPanel } from '@/features/security/GroupAssignmentsPanel';
import { testTheme } from '@/test/render';

/**
 * ФВ-6.14: групові призначення — колонка «Область» і поле проєктів у формі.
 *
 * ⚠ МУТАЦІЇ: не додавати `scope` у тіло `POST` — червоніє тест форми;
 * колонка без `ScopeSummary` (порожня клітинка) — червоніє тест колонки;
 * `[]` від сервера показати «усіма проєктами» — червоніє тест «не діє ніде».
 */

const roles: RoleView[] = [
  { id: 3, code: 'Operators', isActive: true, isBuiltIn: false, permissions: ['Document.View'], dangerousPermissions: [] },
];

const projects: GrantableProject[] = [
  { id: 5, code: 'P5', nameL10n: { values: { en: 'Flare north' } } },
  { id: 6, code: 'P6', nameL10n: { values: { en: 'Flare south' } } },
];

const rows = [
  { id: 1, roleId: 3, roleCode: 'Operators', principalSid: 'S-1-5-21-1', principalName: 'CORP\\North', validFrom: null, validTo: null, scope: { projects: [5] } },
  { id: 2, roleId: 3, roleCode: 'Operators', principalSid: 'S-1-5-21-2', principalName: 'CORP\\All', validFrom: null, validTo: null, scope: null },
  { id: 3, roleId: 3, roleCode: 'Operators', principalSid: 'S-1-5-21-3', principalName: 'CORP\\Broken', validFrom: null, validTo: null, scope: { projects: [] } },
];

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

let posted: Record<string, unknown>[] = [];

function stubFetch(): void {
  posted = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (init?.method === 'POST') {
        posted.push(JSON.parse(String(init.body)) as Record<string, unknown>);
        return json({ id: 9, effectiveAfterNextSignIn: false, principalName: null, principalSid: 'S-1-5-21-9' }, 201);
      }
      if (url.includes('/security/projects')) return json(projects);
      return json(rows);
    }),
  );
}

function renderPanel(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <GroupAssignmentsPanel roles={roles} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function scopeCell(group: string): HTMLElement {
  const row = screen.getByText(group).closest('tr');
  const cell = row?.querySelector<HTMLElement>('[data-column="scope"]');
  expect(cell).toBeTruthy();

  return cell as HTMLElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('GroupAssignmentsPanel: область дії (ФВ-6.14)', () => {
  it('колонка «Область»: назва проєкту, «усі проєкти», «не діє ніде»', async () => {
    stubFetch();
    renderPanel();

    expect(await screen.findByText('⟦groupRoles.scope⟧')).toBeTruthy();
    await waitFor(() => expect(scopeCell('CORP\\North').textContent).toBe('Flare north (P5)'));
    expect(scopeCell('CORP\\All').textContent).toBe('⟦security.scopeAllProjects⟧');
    expect(scopeCell('CORP\\Broken').textContent).toBe('⟦security.scopeNowhere⟧');
  });

  it('проєкт у формі йде в POST полем scope; попередження про глобальні права', async () => {
    stubFetch();
    renderPanel();

    await screen.findByText('CORP\\North');

    fireEvent.click(screen.getByRole('textbox', { name: '⟦security.role⟧' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Operators' }));
    fireEvent.change(screen.getByRole('textbox', { name: /groupRoles\.principal/ }), {
      target: { value: 'CORP\\South' },
    });

    expect(screen.queryByText('⟦security.scopeGlobalWarning⟧')).toBeNull();
    fireEvent.click(screen.getByRole('textbox', { name: /security\.scopeProjects/ }));
    fireEvent.click(await screen.findByRole('option', { name: 'Flare south (P6)' }));
    expect(screen.getByText('⟦security.scopeGlobalWarning⟧')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '⟦groupRoles.assign⟧' }));
    await waitFor(() => expect(posted).toHaveLength(1));

    expect(posted[0]).toEqual({
      roleId: 3,
      principal: 'CORP\\South',
      confirmDangerous: false,
      scope: { projects: [6] },
    });
  });

  it('без проєктів поле scope не шлеться — роль діє в усіх проєктах', async () => {
    stubFetch();
    renderPanel();

    await screen.findByText('CORP\\North');

    fireEvent.click(screen.getByRole('textbox', { name: '⟦security.role⟧' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Operators' }));
    fireEvent.change(screen.getByRole('textbox', { name: /groupRoles\.principal/ }), {
      target: { value: 'CORP\\South' },
    });
    fireEvent.click(screen.getByRole('button', { name: '⟦groupRoles.assign⟧' }));
    await waitFor(() => expect(posted).toHaveLength(1));

    expect(posted[0]).not.toHaveProperty('scope');
  });
});
