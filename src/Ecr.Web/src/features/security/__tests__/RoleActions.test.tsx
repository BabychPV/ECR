import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { RoleActions } from '@/features/security/RoleActions';
import { withTestDefaults } from '@/test/render';
import type { RoleView } from '@/api/types';

/**
 * Директива №15, `BE-14`: дії над роллю.
 *
 * ⛔ Головне тут — відмова видалення. «Роль використовується» — відповідь по
 * суті, а не аварія: діалог має показати, СКІЛЬКИ на ній тримається, і
 * прибрати кнопку, повтор якої дав би ту саму відмову.
 */

const Strings: Record<string, string> = {
  'common.delete': 'Remove',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
  'security.cloneRole': 'Clone',
  'security.renameRole': 'Rename',
  'security.newRoleCode': 'New code',
  'security.deleteRoleConfirm': 'Delete role "{code}"?',
  'security.roleDeleteRefused': 'The role cannot be deleted',
  'security.roleAssignments': 'Assignments',
  'security.grants': 'Grants',
  'security.roleApprovalSteps': 'Approval route steps',
  'security.rolePeriodAccessRules': 'Period access rules',
  'security.roleCloned': 'Cloned',
};

const role = (overrides: Partial<RoleView> = {}): RoleView => ({
  id: 7,
  code: 'Reviewers',
  isActive: true,
  isBuiltIn: false,
  permissions: ['Document.View'],
  dangerousPermissions: [],
  ...overrides,
});

const json = (body: unknown, status = 200, type = 'application/json'): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } });

function stubFetch(onRole: (url: string, init?: RequestInit) => Response): ReturnType<typeof vi.fn> {
  const spy = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
    if (url.includes('/roles/')) return onRole(url, init);
    return json([]);
  });
  vi.stubGlobal('fetch', spy);
  return spy;
}

async function renderActions(target: RoleView): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RoleActions role={target} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RoleActions (BE-14)', () => {
  it('на 409 показує кількість залежних і прибирає кнопку видалення замість повтору', async () => {
    const fetchSpy = stubFetch(() =>
      json(
        {
          title: 'Role conflict',
          status: 409,
          detail: 'Role "Reviewers" is in use.',
          errorCode: 'ECR-SEC-0409',
          correlationId: 'c-1',
          assignments: '2',
          grants: '1',
        },
        409,
        'application/problem+json',
      ),
    );
    await renderActions(role());

    await userEvent.click(screen.getByRole('button', { name: 'Remove' }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('Delete role "Reviewers"?');

    const confirm = screen.getAllByRole('button', { name: 'Remove' }).at(-1) as HTMLElement;
    await userEvent.click(confirm);

    await screen.findByText('The role cannot be deleted');
    expect(screen.getByText('Assignments: 2')).not.toBeNull();
    expect(screen.getByText('Grants: 1')).not.toBeNull();

    const deleteCall = fetchSpy.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE');
    expect(String(deleteCall?.[0])).toContain('/api/v1/roles/7');

    // ⛔ У діалозі лишився тільки Cancel: кнопка-тригер у рядку — поза діалогом.
    await waitFor(() =>
      expect(
        Array.from(screen.getByRole('dialog').querySelectorAll('button')).map((b) => b.textContent),
      ).not.toContain('Remove'),
    );
  });

  it('V-09: роль лише в маршруті погодження — діалог називає кроки маршруту', async () => {
    stubFetch(() =>
      json(
        {
          title: 'Role conflict',
          status: 409,
          detail: 'Role "Reviewers" is in use.',
          errorCode: 'ECR-SEC-0409',
          correlationId: 'c-2',
          assignments: '0',
          grants: '0',
          approvalSteps: '2',
          periodAccessRules: '0',
        },
        409,
        'application/problem+json',
      ),
    );
    await renderActions(role());

    await userEvent.click(screen.getByRole('button', { name: 'Remove' }));
    await screen.findByRole('dialog');
    await userEvent.click(screen.getAllByRole('button', { name: 'Remove' }).at(-1) as HTMLElement);

    await screen.findByText('The role cannot be deleted');
    expect(screen.getByText('Approval route steps: 2')).not.toBeNull();
    // ⚠ Нульовий лічильник нового виду не показується: «0 правил» — шум.
    expect(screen.queryByText(/Period access rules/)).toBeNull();
  });

  it.each([
    ['без messageKey — сире речення сервера не показується', false],
    ['з messageKey — локалізована подробиця показується', true],
  ])('X-08: відмова видалення, %s', async (_name, localized) => {
    stubFetch(() =>
      json(
        {
          title: 'Role conflict',
          status: 409,
          detail: 'Роль «Reviewers» використовується: 2 призначення.',
          errorCode: 'ECR-SEC-0409',
          correlationId: 'c-3',
          assignments: '2',
          grants: '0',
          ...(localized ? { messageKey: 'err.ECR-SEC-0409.roleInUse' } : {}),
        },
        409,
        'application/problem+json',
      ),
    );
    await renderActions(role());

    await userEvent.click(screen.getByRole('button', { name: 'Remove' }));
    await screen.findByRole('dialog');
    await userEvent.click(screen.getAllByRole('button', { name: 'Remove' }).at(-1) as HTMLElement);

    await screen.findByText('The role cannot be deleted');

    // ⛔ Мутація «повернути `remove.error.message`» показує сире речення й
    // без `messageKey` — перший випадок червоніє.
    const raw = screen.queryByText('Роль «Reviewers» використовується: 2 призначення.');
    expect(raw === null).toBe(!localized);
  });

  it('клон надсилає новий код на /roles/{id}/clone', async () => {
    const fetchSpy = stubFetch(() => json({ roleId: 8 }, 201));
    await renderActions(role());

    await userEvent.click(screen.getByRole('button', { name: 'Clone' }));
    await userEvent.type(await screen.findByLabelText('New code'), 'ReviewersCopy');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      const call = fetchSpy.mock.calls.find(([url]) => String(url).includes('/roles/7/clone'));
      expect(call).toBeDefined();
      expect((call?.[1] as RequestInit).method).toBe('POST');
      expect(JSON.parse(String((call?.[1] as RequestInit).body))).toEqual({ code: 'ReviewersCopy' });
    });
  });

  it('вбудована роль має лише «клонувати»', async () => {
    stubFetch(() => json([]));
    await renderActions(role({ isBuiltIn: true, code: 'SystemAdministrator' }));

    expect(screen.getByRole('button', { name: 'Clone' })).not.toBeNull();
    expect(screen.queryByRole('button', { name: 'Rename' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Remove' })).toBeNull();
  });
});
