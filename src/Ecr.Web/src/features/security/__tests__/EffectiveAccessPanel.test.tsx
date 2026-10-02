import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { EffectiveAccessView } from '@/api/types';
import { EffectiveAccessPanel } from '@/features/security/EffectiveAccessPanel';
import { testTheme } from '@/test/render';

/**
 * Розріз «ресурс → рівень → грант ролі» (`ФВ-6.16`, `D-220`).
 *
 * ⛔ Перевіряється те, чим розріз відрізняється від переліку ролей: внесок, що НЕ порахувався, стоїть
 * на екрані зі словом «ні», а заборона названа окремо. Розріз, який показує лише те, що дало доступ,
 * не пояснює, чому доступу немає.
 */
const Denied: EffectiveAccessView = {
  userId: 7,
  userName: 'ivanov',
  resource: 'Registry:5',
  level: 'None',
  isDenied: true,
  denyReason: 'ExplicitDeny',
  groupsFromTicket: false,
  levelMayExceedActual: false,
  contributions: [
    { source: 'Permission', roleCode: 'Glb', principalSid: null, permissionCode: 'Registry.View', level: 'Read', isDeny: false, scope: 'Unscoped', counted: true },
    { source: 'Grant', roleCode: 'Dny', principalSid: null, permissionCode: null, level: 'Read', isDeny: true, scope: 'Unscoped', counted: true },
    { source: 'Grant', roleCode: 'Scp', principalSid: 'S-1-5-21-9', permissionCode: null, level: 'Write', isDeny: false, scope: 'OutOfScope', counted: false },
  ],
};

function stub(body: unknown, status = 200): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async () =>
    Promise.resolve(
      new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }),
    ),
  );
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <EffectiveAccessPanel userId={7} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('EffectiveAccessPanel', () => {
  it('нічого не питає в сервера, доки не натиснуто «Пояснити», і не дозволяє порожній ресурс', () => {
    const fetchMock = stub(Denied);
    show();

    const button = screen.getByRole('button', { name: /effectiveAccess\.explain/ });
    expect(button).toHaveProperty('disabled', true);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('питає ресурс у формі тип:ідентифікатор і показує заборону та внесок, що не порахувався', async () => {
    const fetchMock = stub(Denied);
    show();

    fireEvent.change(screen.getByLabelText(/effectiveAccess\.resourceId/), { target: { value: '5' } });
    fireEvent.click(screen.getByRole('button', { name: /effectiveAccess\.explain/ }));

    await waitFor(() => expect(screen.getByText(/effectiveAccess\.level/)).toBeDefined());

    const url = String((fetchMock.mock.calls[0] as unknown[])[0]);
    expect(url).toContain('/api/v1/security/users/7/effective-access?resource=Registry%3A5');

    // Заборона названа окремо (role=alert) — виграє над усім.
    expect(screen.getByText(/effectiveAccess\.denied/)).toBeDefined();

    // Групи чужого запису невідомі — перелік не мовчить.
    expect(screen.getByText(/effectiveAccess\.groupsUnknown/)).toBeDefined();

    // Таблиця внесків має доступну назву (WCAG 1.3.1): без неї читач екрана каже лише «таблиця».
    expect(screen.getByRole('table', { name: '⟦effectiveAccess.title⟧' })).toBeDefined();

    // Усі три внески видно, у тому числі той, що не порахувався.
    expect(screen.getByText('Glb')).toBeDefined();
    expect(screen.getByText('Dny')).toBeDefined();
    const scopedRow = screen.getByText('Scp').closest('tr');
    expect(scopedRow?.textContent).toContain('effectiveAccess.scopeOutOfScope');
    expect(scopedRow?.textContent).toContain('effectiveAccess.notCounted');
    expect(scopedRow?.textContent).toContain('effectiveAccess.viaGroup');

    const denyRow = screen.getByText('Dny').closest('tr');
    expect(denyRow?.textContent).toContain('effectiveAccess.deny');
    expect(denyRow?.textContent).toContain('effectiveAccess.counted');
  });

  it('порожній перелік внесків і рівень «нічого не дає» пояснюються словами', async () => {
    stub({ ...Denied, isDenied: false, denyReason: 'NoGrant', groupsFromTicket: true, contributions: [] });
    show();

    fireEvent.change(screen.getByLabelText(/effectiveAccess\.resourceId/), { target: { value: '9' } });
    fireEvent.click(screen.getByRole('button', { name: /effectiveAccess\.explain/ }));

    await waitFor(() => expect(screen.getByText(/effectiveAccess\.noGrant/)).toBeDefined());
    expect(screen.getByText(/effectiveAccess\.noContributions/)).toBeDefined();
    expect(screen.queryByText(/effectiveAccess\.groupsUnknown/)).toBeNull();
  });
});
