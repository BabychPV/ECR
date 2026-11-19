import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { RoleView } from '@/api/types';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { theme } from '@/shared/theme/theme';
import { withTestDefaults } from '@/test/render';

/**
 * ✎ B155 (варіант C): типовий маршрут погодження майданчика — на вкладці «Ролі» і ЛИШЕ з `Security.ManageRoles`
 * (сервер стереже `GET/PUT /approval-route/default` тим самим правом). Без права — ні розділу, ні запиту.
 */
configure({ asyncUtilTimeout: 10_000 });

const Roles: RoleView[] = [
  { id: 10, code: 'Auditor', isActive: true, isBuiltIn: false, permissions: [], dangerousPermissions: [] },
];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function serve(permissions: string[]): string[] {
  const urls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      urls.push(url);

      if (url.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }
      if (url.endsWith('/api/v1/approval-route/default')) {
        return json({
          projectId: null,
          templateId: null,
          hasRoute: true,
          steps: [{ ordinal: 1, roleId: 10, isOptional: false }],
          effectiveSource: 'own',
          effectiveSteps: [{ ordinal: 1, roleId: 10, isOptional: false }],
          generalApproverAlsoApproves: false,
          generalApproverRoleId: null,
          canEdit: true,
          workflowModeNone: false,
        });
      }
      if (url.includes('/roles/approvers')) {
        return json([{ id: 10, code: 'Auditor', nameL10n: null, isBuiltIn: false, isGeneralApprover: false }]);
      }
      if (url.includes('/roles')) return json(Roles);

      return json([]);
    }),
  );

  return urls;
}

function show(): void {
  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/security']}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SecurityPage: типовий маршрут погодження (B155)', () => {
  it('з Security.ManageRoles — розділ із заголовком і маршрутом (контроль)', async () => {
    serve(['Security.ManageRoles', 'Security.ManageUsers']);
    show();

    const section = await screen.findByTestId('approval-route-section-default');
    expect(screen.getByRole('region', { name: '⟦approvalRoutes.defaultTitle⟧' })).toBe(section);
    expect((await screen.findByTestId('approval-route-source')).textContent).toBe('⟦approvalRoutes.source.own⟧');
  });

  it('без Security.ManageRoles — ні розділу, ні запиту типового маршруту', async () => {
    const urls = serve(['Security.ManageUsers']);
    show();

    // Сторінка намальована: вкладки є, профіль приїхав (вкладка «Session» — з ManageUsers).
    await screen.findByRole('tab', { name: '⟦security.session⟧' });
    await screen.findAllByText('Auditor');
    expect(screen.queryByTestId('approval-route-section-default')).toBeNull();
    expect(urls.some((url) => url.includes('/approval-route/default'))).toBe(false);
  });
});
