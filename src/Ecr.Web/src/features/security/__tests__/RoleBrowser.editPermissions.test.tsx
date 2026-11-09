import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { RoleBrowser } from '@/features/security/RoleBrowser';
import type { PermissionCatalogItem, RolePermissionsPreview, RoleView } from '@/api/types';
import { withTestDefaults } from '@/test/render';

/**
 * (v) рішення людини 08.11: права власної ролі правляться — «Edit permissions» → прапорці → перегляд впливу →
 * окреме підтвердження небезпечних → `PUT …/permissions` з `If-Match`. Вбудована — замок і причина текстом.
 *
 * ⛔ Мутаційні докази:
 *  - кнопка «Edit permissions» і для вбудованої → червоний 1-й;
 *  - «Save» не чекає прапорця небезпечних → червоний 2-й (кнопка ввімкнена до підтвердження);
 *  - `If-Match` не з перегляду / без `confirmDangerous` → червоний 2-й (тіло й заголовок PUT);
 *  - без звірки `current` перегляду з основою правки → червоний 3-й;
 *  - `409 permissionsStale` лише тостом → червоний 4-й.
 */

const Catalog: PermissionCatalogItem[] = [
  { code: 'Document.Reopen', group: 'Document', isDangerous: true },
  { code: 'Document.View', group: 'Document', isDangerous: false },
];

const Roles: RoleView[] = [
  {
    id: 1,
    code: 'Approver',
    isActive: true,
    isBuiltIn: true,
    permissions: ['Document.View'],
    dangerousPermissions: [],
  },
  {
    id: 11,
    code: 'LandRestricted',
    isActive: true,
    isBuiltIn: false,
    permissions: ['Document.View'],
    dangerousPermissions: [],
  },
];

interface Call {
  readonly url: string;
  readonly method: string;
  readonly body: unknown;
  readonly ifMatch: string | null;
}

let calls: Call[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function preview(overrides: Partial<RolePermissionsPreview> = {}): RolePermissionsPreview {
  return {
    roleId: 11,
    roleCode: 'LandRestricted',
    version: 'V1',
    current: ['Document.View'],
    added: ['Document.Reopen'],
    removed: ['Document.View'],
    addedDangerous: ['Document.Reopen'],
    users: [
      {
        userId: 3,
        userName: 'land-owner',
        displayName: 'Land owner',
        gained: ['Document.Reopen'],
        lost: [],
        effectiveBefore: 4,
        effectiveAfter: 5,
      },
    ],
    groups: [{ sid: 'S-1-5-21-1-2-3-500' }],
    ...overrides,
  };
}

function mockServer(options: { preview?: RolePermissionsPreview; put?: () => Response } = {}): void {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const headers = new Headers(init?.headers);
      calls.push({
        url,
        method,
        body: init?.body === undefined ? null : JSON.parse(String(init.body)),
        ifMatch: headers.get('If-Match'),
      });

      if (url.endsWith('/permissions/preview')) return json(options.preview ?? preview());
      if (method === 'PUT' && url.endsWith('/permissions')) return options.put?.() ?? new Response(null, { status: 204 });

      return json([]);
    }),
  );
}

function show(role: string): void {
  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={[`/admin/security?role=${role}`]}>
          <RoleBrowser roles={Roles} permissions={Catalog} canManage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function checkbox(code: string): HTMLInputElement {
  const row = screen.getByTestId('role-detail').querySelector(`[data-permission="${code}"]`) as HTMLElement;

  return within(row).getByRole('checkbox') as HTMLInputElement;
}

async function editAndReview(): Promise<HTMLElement> {
  fireEvent.click(screen.getByTestId('role-edit-permissions'));
  fireEvent.click(checkbox('Document.View'));
  fireEvent.click(checkbox('Document.Reopen'));

  const banner = screen.getByTestId('role-permissions-dirty');
  expect(banner.textContent).toContain('security.unsavedPermissionChanges (count=2)');
  fireEvent.click(within(banner).getByRole('button', { name: /security\.reviewAndSave/ }));

  return screen.findByTestId('role-impact');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RoleBrowser: правка прав власної ролі ((v) 08.11)', () => {
  it('власна роль — «Edit permissions»; вбудована — замок і причина, кнопки немає', () => {
    mockServer();
    show('LandRestricted');
    expect(screen.getByTestId('role-edit-permissions')).not.toBeNull();
    expect(screen.queryByTestId('role-builtin-banner')).toBeNull();
  });

  it('вбудована роль: банер із причиною, без «Edit permissions»', () => {
    mockServer();
    show('Approver');
    expect(screen.getByTestId('role-builtin-banner').textContent).toContain('security.builtInReadOnlyText');
    expect(screen.queryByTestId('role-edit-permissions')).toBeNull();
  });

  it('перегляд впливу → небезпечне окремо підтверджується → PUT з If-Match перегляду і confirmDangerous', async () => {
    mockServer();
    show('LandRestricted');

    const impact = await editAndReview();

    const sent = calls.find((c) => c.url.endsWith('/roles/11/permissions/preview'));
    expect(sent?.body).toEqual({ permissionCodes: ['Document.Reopen'] });

    // Носії й групи — до збереження.
    const user = within(impact).getByText('Land owner').closest('tr') as HTMLElement;
    expect(user.textContent).toContain('+ Document.Reopen');
    expect(within(impact).getByTestId('role-impact-groups').textContent).toContain('S-1-5-21-1-2-3-500');

    const save = within(impact).getByRole('button', { name: /security\.savePermissions/ }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);

    fireEvent.click(within(screen.getByTestId('role-dangerous-confirm')).getByRole('checkbox'));
    await waitFor(() => expect(save.disabled).toBe(false));
    fireEvent.click(save);

    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true));
    const put = calls.find((c) => c.method === 'PUT');
    expect(put?.url).toMatch(/\/api\/v1\/roles\/11\/permissions$/);
    expect(put?.ifMatch).toBe('"V1"');
    expect(put?.body).toEqual({ permissionCodes: ['Document.Reopen'], confirmDangerous: true });
  });

  it('роль змінили з іншого місця після початку правки — пояснення, «Save» вимкнено', async () => {
    mockServer({ preview: preview({ current: ['Document.View', 'Document.Reopen'] }) });
    show('LandRestricted');

    const impact = await editAndReview();
    expect(within(impact).getByTestId('role-permissions-conflict').textContent).toContain(
      'security.rolePermissionsChangedMeanwhile',
    );
    fireEvent.click(within(screen.getByTestId('role-dangerous-confirm')).getByRole('checkbox'));
    expect((within(impact).getByRole('button', { name: /security\.savePermissions/ }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('409 permissionsStale — зрозуміле пояснення в діалозі й «Reload role», без 500', async () => {
    mockServer({
      put: () =>
        json(
          {
            title: 'Conflict',
            status: 409,
            detail: 'Someone else changed the permissions of role "LandRestricted".',
            errorCode: 'ECR-SEC-0409',
            correlationId: 'c',
            messageKey: 'err.ECR-SEC-0409.permissionsStale',
          },
          409,
        ),
    });
    show('LandRestricted');

    const impact = await editAndReview();
    fireEvent.click(within(screen.getByTestId('role-dangerous-confirm')).getByRole('checkbox'));
    fireEvent.click(within(impact).getByRole('button', { name: /security\.savePermissions/ }));

    const conflict = await within(impact).findByTestId('role-permissions-conflict');
    expect(within(conflict).getByRole('button', { name: /security\.reloadRole/ })).not.toBeNull();
  });
});
