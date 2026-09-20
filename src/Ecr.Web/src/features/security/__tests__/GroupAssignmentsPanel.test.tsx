import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { GroupAssignmentsPanel, toMachineDate } from '@/features/security/GroupAssignmentsPanel';
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
  'groupRoles.validFrom': 'Valid from',
  'groupRoles.validTo': 'Valid to',
  'groupRoles.validityOrder': 'End before start',
};

const roles: RoleView[] = [
  { id: 2, code: 'Publishers', isActive: true, isBuiltIn: false, permissions: [], dangerousPermissions: ['Calculation.Publish'] },
];

const json = (body: unknown, status = 200, type = 'application/json'): Response =>
  new Response(body === null ? null : JSON.stringify(body), { status, headers: { 'Content-Type': type } });

function stubFetch(
  onPost: (body: Record<string, unknown>) => Response,
  validity: { validFrom: string | null; validTo: string | null } = { validFrom: null, validTo: null },
): ReturnType<typeof vi.fn> {
  const spy = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
    if (init?.method === 'POST') return onPost(JSON.parse(String(init.body)) as Record<string, unknown>);
    if (init?.method === 'DELETE') return json(null, 204);
    return json([
      { id: 11, roleId: 2, roleCode: 'Publishers', principalSid: 'S-1-5-21-1-2-3-1105', principalName: 'CORP\\EcrOps', ...validity },
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

  it('вікно чинності: дата без години, а безстрокова межа — «…», не «—»', async () => {
    stubFetch(() => json({}, 201), { validFrom: '2026-03-01', validTo: null });
    await renderPanel();

    const row = (await screen.findByText('CORP\\EcrOps')).closest('tr');
    const from = row?.querySelector('time');

    expect(from?.getAttribute('datetime')).toBe('2026-03-01');
    expect(from?.textContent).toContain('2026');
    // Години в даних немає: `DateOnly` на сервері, тож і на екрані її бути не може.
    expect(from?.textContent).not.toMatch(/\d:\d\d/);

    const open = row?.querySelector('[data-timestamp="none"]');
    expect(open?.textContent).toBe('…');
    expect(row?.textContent).not.toContain('—');
  });

  describe('дата в запиті — та сама доба, яку обрано', () => {
    /*
     * ⚠ Пояс у тесті задати не можна: пул `vmThreads` — це потоки, а присвоєння
     * `process.env.TZ` у потоці до рушія не доходить (перевірено: мутація
     * `toISOString()` лишала «New_York 23:30» зеленим). Тому два виміри:
     *   • північ і пізній вечір ЛОКАЛЬНОЇ доби — у будь-якому поясі з ненульовим
     *     зсувом `toISOString()` ламає рівно один із них;
     *   • дата, чия локальна доба свідомо не збігається з UTC, — ловить те саме
     *     і на машині в UTC (CI).
     */
    it.each([0, 23])('toMachineDate бере локальну добу, година %i', (hour) => {
      expect(toMachineDate(new Date(2026, 2, 1, hour, 30))).toBe('2026-03-01');
      expect(toMachineDate(null)).toBeNull();
    });

    it('toMachineDate не читає UTC: локальна доба 2 березня за UTC-доби 1 березня', () => {
      const picked = new Date(Date.UTC(2026, 2, 1, 12));
      picked.getFullYear = () => 2026;
      picked.getMonth = () => 2;
      picked.getDate = () => 2;

      expect(toMachineDate(picked)).toBe('2026-03-02');
    });

    it('форма шле validFrom/validTo як YYYY-MM-DD, а «to раніше from» не відправляється', async () => {
      const posted: Record<string, unknown>[] = [];
      stubFetch((body) => {
        posted.push(body);
        return json({ id: 13, principalSid: 'S-1-5-32-544', principalName: null, effectiveAfterNextSignIn: false }, 201);
      });
      await renderPanel();

      await userEvent.click(screen.getByRole('textbox', { name: 'Role' }));
      await userEvent.click(await screen.findByRole('option', { name: 'Publishers' }));
      await userEvent.type(screen.getByLabelText(/Group name or SID/), 'S-1-5-32-544');
      await userEvent.type(screen.getByRole('textbox', { name: 'Valid from' }), '2026-03-10');
      await userEvent.type(screen.getByRole('textbox', { name: 'Valid to' }), '2026-03-01');
      await userEvent.tab();

      expect(await screen.findByText('End before start')).not.toBeNull();
      expect(screen.getByRole('button', { name: 'Assign role to group' })).toHaveProperty('disabled', true);

      await userEvent.clear(screen.getByRole('textbox', { name: 'Valid to' }));
      await userEvent.type(screen.getByRole('textbox', { name: 'Valid to' }), '2026-03-31');
      await userEvent.tab();
      await userEvent.click(screen.getByRole('button', { name: 'Assign role to group' }));

      await waitFor(() =>
        expect(posted).toEqual([
          { roleId: 2, principal: 'S-1-5-32-544', confirmDangerous: false, validFrom: '2026-03-10', validTo: '2026-03-31' },
        ]),
      );
    });
  });
});
