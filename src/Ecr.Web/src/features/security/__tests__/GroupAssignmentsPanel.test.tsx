import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { GroupAssignmentsPanel } from '@/features/security/GroupAssignmentsPanel';
import { withTestDefaults } from '@/test/render';
import type { RoleView } from '@/api/types';

/** Ролі груп каталогу: ім'я поруч із SID, відкликання, підтвердження небезпечної ролі. */

const Strings: Record<string, string> = {
  'security.role': 'Role',
  'groupRoles.title': 'Group roles',
  'groupRoles.group': 'Group',
  'groupRoles.revoke': 'Revoke',
  'groupRoles.revoked': 'Revoked',
  'groupRoles.principal': 'Group name or SID',
  'groupRoles.principalHint': 'DOMAIN\\Group or S-1-...',
  'groupRoles.assign': 'Assign role to group',
  'groupRoles.assigned': 'Assigned',
  'groupRoles.assignedNextSignIn': 'Assigned, next sign-in',
  'groupRoles.dangerousTitle': 'Dangerous permissions',
  'groupRoles.assignAnyway': 'Assign anyway',
};

const roles: RoleView[] = [
  { id: 2, code: 'Publishers', isActive: true, isBuiltIn: false, permissions: [], dangerousPermissions: ['Calculation.Publish'] },
];

const json = (body: unknown, status = 200, type = 'application/json'): Response =>
  new Response(body === null ? null : JSON.stringify(body), { status, headers: { 'Content-Type': type } });

function stubFetch(onPost: (body: Record<string, unknown>) => Response): ReturnType<typeof vi.fn> {
  const spy = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
    if (init?.method === 'POST') return onPost(JSON.parse(String(init.body)) as Record<string, unknown>);
    if (init?.method === 'DELETE') return json(null, 204);
    return json([
      { id: 11, roleId: 2, roleCode: 'Publishers', principalSid: 'S-1-5-21-1-2-3-1105', principalName: 'CORP\\EcrOps', validFrom: null, validTo: null },
    ]);
  });
  vi.stubGlobal('fetch', spy);
  return spy;
}

async function renderPanel(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <GroupAssignmentsPanel roles={roles} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('GroupAssignmentsPanel', () => {
  it('показує імʼя поруч із SID і відкликає призначення за його Id', async () => {
    const fetchSpy = stubFetch(() => json({}, 201));
    await renderPanel();

    expect(await screen.findByText('CORP\\EcrOps')).not.toBeNull();
    expect(screen.getByText('S-1-5-21-1-2-3-1105')).not.toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Revoke' }));

    await waitFor(() => {
      const call = fetchSpy.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE');
      expect(String(call?.[0])).toContain('/api/v1/security/group-assignments/11');
    });
  });

  it('небезпечна роль: 409 показує права, і лише «Assign anyway» шле confirmDangerous', async () => {
    const posted: Record<string, unknown>[] = [];
    stubFetch((body) => {
      posted.push(body);
      return body['confirmDangerous'] === true
        ? json({ id: 12, principalSid: 'S-1-5-32-544', principalName: null, effectiveAfterNextSignIn: true }, 201)
        : json(
            {
              title: 'Conflict',
              status: 409,
              detail: 'Dangerous',
              errorCode: 'ECR-SEC-0409',
              correlationId: 'c-1',
              dangerousPermissions: ['Calculation.Publish'],
            },
            409,
            'application/problem+json',
          );
    });
    await renderPanel();

    await userEvent.click(screen.getByRole('textbox', { name: 'Role' }));
    await userEvent.click(await screen.findByRole('option', { name: 'Publishers' }));
    await userEvent.type(screen.getByLabelText(/Group name or SID/), 'S-1-5-32-544');
    await userEvent.click(screen.getByRole('button', { name: 'Assign role to group' }));

    expect(await screen.findByText('Calculation.Publish')).not.toBeNull();
    expect(posted).toEqual([{ roleId: 2, principal: 'S-1-5-32-544', confirmDangerous: false }]);

    await userEvent.click(screen.getByRole('button', { name: 'Assign anyway' }));

    await waitFor(() => expect(posted.at(-1)).toEqual({ roleId: 2, principal: 'S-1-5-32-544', confirmDangerous: true }));
  });
});
