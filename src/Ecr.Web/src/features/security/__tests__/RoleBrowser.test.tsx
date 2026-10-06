import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { JSX } from 'react';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { RoleBrowser } from '@/features/security/RoleBrowser';
import type { PermissionCatalogItem, RoleView } from '@/api/types';
import { withTestDefaults } from '@/test/render';

/**
 * UI-37: ролі master-detail і «Compare roles» (макет `screens-ops.js`
 * `secRoles`).
 *
 * ⛔ Мутаційні докази:
 *  - `roleHas` лише за `permissions` → право, яке сервер назвав тільки в
 *    `dangerousPermissions`, показується «не надано» (червоний 2-й);
 *  - обрана роль не з `?role=` → у шапці перша роль (червоний 1-й);
 *  - «Only differences» не фільтрує → лишаються однакові рядки (червоний 4-й).
 */

const Strings: Record<string, string> = {
  'security.roles': 'Roles',
  'security.builtIn': 'built-in',
  'security.custom': 'custom',
  'security.inactive': 'inactive',
  'security.noPermissions': 'no permissions',
  'security.dangerous': '{count} dangerous permission(s)',
  'security.dangerousMark': 'Dangerous',
  'security.granted': 'granted',
  'security.notGranted': 'not granted',
  'security.groupSummary': '{granted} of {total} granted',
  'security.groupDangerous': '{count} dangerous',
  'security.domainOther': 'Other',
  'security.domain.Document': 'Documents',
  'security.viewOne': 'One role',
  'security.viewCompare': 'Compare roles',
  'security.builtInReadOnlyTitle': 'Built-in role — read-only',
  'security.builtInReadOnlyText': 'Built-in roles ship with the product.',
  'security.customReadOnly': 'Permissions are set when the role is created.',
  'security.cloneAsCustom': 'Clone as custom role',
  'security.roleMore': 'More',
  'security.onlyDiff': 'Only differences',
  'security.identicalRoles': 'These roles are identical.',
  'security.compareHint': 'Showing {shown} of {total}',
  'security.permissionColumn': 'Permission',
  'security.matrixLabel': 'Role comparison matrix',
  'permission.Document.View': 'View documents',
  'permission.Document.Reopen': 'Return a submitted document to draft',
};

const Catalog: PermissionCatalogItem[] = [
  { code: 'Document.Reopen', group: 'Document', isDangerous: true },
  { code: 'Document.View', group: 'Document', isDangerous: false },
  { code: 'Security.ManageRoles', group: 'Security', isDangerous: true },
];

const Roles: RoleView[] = [
  {
    id: 1,
    code: 'Approver',
    nameL10n: { values: { en: 'Approver role' } },
    isActive: true,
    isBuiltIn: true,
    permissions: ['Document.View'],
    // ⚠ Лише тут, не в `permissions`: має читатися як «надано».
    dangerousPermissions: ['Document.Reopen'],
  },
  {
    id: 2,
    code: 'NightShift',
    isActive: true,
    isBuiltIn: false,
    permissions: ['Document.View', 'Legacy.Thing'],
    dangerousPermissions: [],
  },
  {
    id: 3,
    code: 'Twin',
    isActive: false,
    isBuiltIn: false,
    permissions: ['Document.View'],
    dangerousPermissions: ['Document.Reopen'],
  },
];

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) =>
      new Response(
        JSON.stringify(
          String(input).includes('/ui-strings/') ? { languageCode: 'en', revision: 1, strings: Strings } : [],
        ),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    ),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

let search = '';
function Where(): JSX.Element {
  search = useLocation().search;
  return <></>;
}

async function show(url: string, roles: RoleView[] = Roles, canManage = true): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter initialEntries={[url]}>
          <RoleBrowser roles={roles} permissions={Catalog} canManage={canManage} />
          <Where />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function detail(): HTMLElement {
  return screen.getByTestId('role-detail');
}

describe('RoleBrowser: одна роль (UI-37)', () => {
  it('обрана роль — з адреси; клік у списку міняє її й адресу', async () => {
    await show('/admin/security?role=NightShift');

    expect(within(detail()).getByRole('heading', { level: 2 }).textContent).toBe('NightShift');
    // Власна роль: пояснення «лише читання» і меню «More», без банера вбудованої.
    expect(within(detail()).getByText('Permissions are set when the role is created.')).not.toBeNull();
    expect(within(detail()).getByRole('button', { name: 'More' })).not.toBeNull();
    expect(screen.queryByTestId('role-builtin-banner')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /Approver role/ }));

    await waitFor(() => expect(search).toContain('role=Approver'));
    expect(within(detail()).getByRole('heading', { level: 2 }).textContent).toBe('Approver role');
    expect(screen.getByTestId('role-builtin-banner').textContent).toContain('Built-in role — read-only');
    expect(within(detail()).getByRole('button', { name: 'Clone as custom role' })).not.toBeNull();
  });

  it('групи прав: підсумок, небезпечне позначене, право лише з dangerousPermissions — надано', async () => {
    await show('/admin/security?role=Approver');

    const group = detail().querySelector<HTMLElement>('[data-permission-group="Document"]') as HTMLElement;
    expect(within(group).getByRole('button', { expanded: true }).textContent).toContain('Documents');
    expect(group.textContent).toContain('2 of 2 granted · 1 dangerous');

    const reopen = group.querySelector<HTMLElement>('[data-permission="Document.Reopen"]') as HTMLElement;
    expect(reopen.getAttribute('data-granted')).toBe('true');
    expect(within(reopen).getByText('Dangerous')).not.toBeNull();

    // Група без наданого згорнута, але підсумок видно.
    const security = detail().querySelector<HTMLElement>('[data-permission-group="Security"]') as HTMLElement;
    const toggle = within(security).getByRole('button');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.textContent).toContain('0 of 1 granted');
    fireEvent.click(toggle);
    expect(security.querySelector('[data-permission="Security.ManageRoles"]')?.getAttribute('data-granted')).toBe('false');
  });

  it('право ролі, якого немає в каталозі, не губиться — група «Other»', async () => {
    await show('/admin/security?role=NightShift');

    const other = detail().querySelector<HTMLElement>('[data-permission-group=""]') as HTMLElement;
    expect(other.textContent).toContain('Other');
    expect(other.querySelector('[data-permission="Legacy.Thing"]')?.getAttribute('data-granted')).toBe('true');
  });

  it('без права керування дій над роллю немає', async () => {
    await show('/admin/security?role=Approver', Roles, false);

    expect(within(detail()).queryByRole('button', { name: 'Clone as custom role' })).toBeNull();
  });
});

describe('RoleBrowser: порівняння ролей (UI-37)', () => {
  it('права — рядки, ролі — колонки; «Only differences» лишає лише відмінні', async () => {
    await show('/admin/security?view=compare', [Roles[0] as RoleView, Roles[2] as RoleView]);

    const table = await screen.findByRole('table');
    const head = table.querySelector('thead') as HTMLElement;
    expect(within(head).getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      'Permission',
      'Approver role',
      'Twininactivecustom',
    ]);
    expect(table.querySelectorAll('tbody th[scope="row"]')).toHaveLength(3);

    fireEvent.click(screen.getByRole('checkbox', { name: 'Only differences' }));

    // Approver і Twin мають однакові права — відмінностей немає.
    expect(await screen.findByTestId('role-matrix-identical')).not.toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('відмінне право лишається під «Only differences»', async () => {
    await show('/admin/security?view=compare', [Roles[0] as RoleView, Roles[1] as RoleView]);

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Only differences' }));

    const table = screen.getByRole('table');
    const rows = [...table.querySelectorAll('tbody th[scope="row"]')].map((th) => th.textContent);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toContain('Document.Reopen');
  });
});
