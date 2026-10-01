import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { EffectiveAccessView } from '@/api/types';
import { EffectiveAccessPanel } from '@/features/security/EffectiveAccessPanel';
import { testTheme } from '@/test/render';

/**
 * Ð Ð¾Ð·Ñ€Ñ–Ð· Â«Ñ€ÐµÑÑƒÑ€Ñ â†’ Ñ€Ñ–Ð²ÐµÐ½ÑŒ â†’ Ð³Ñ€Ð°Ð½Ñ‚ Ñ€Ð¾Ð»Ñ–Â» (`Ð¤Ð’-6.16`, `D-220`).
 *
 * â›” ÐŸÐµÑ€ÐµÐ²Ñ–Ñ€ÑÑ”Ñ‚ÑŒÑÑ Ñ‚Ðµ, Ñ‡Ð¸Ð¼ Ñ€Ð¾Ð·Ñ€Ñ–Ð· Ð²Ñ–Ð´Ñ€Ñ–Ð·Ð½ÑÑ”Ñ‚ÑŒÑÑ Ð²Ñ–Ð´ Ð¿ÐµÑ€ÐµÐ»Ñ–ÐºÑƒ Ñ€Ð¾Ð»ÐµÐ¹: Ð²Ð½ÐµÑÐ¾Ðº, Ñ‰Ð¾ ÐÐ• Ð¿Ð¾Ñ€Ð°Ñ…ÑƒÐ²Ð°Ð²ÑÑ, ÑÑ‚Ð¾Ñ—Ñ‚ÑŒ
 * Ð½Ð° ÐµÐºÑ€Ð°Ð½Ñ– Ð·Ñ– ÑÐ»Ð¾Ð²Ð¾Ð¼ Â«Ð½Ñ–Â», Ð° Ð·Ð°Ð±Ð¾Ñ€Ð¾Ð½Ð° Ð½Ð°Ð·Ð²Ð°Ð½Ð° Ð¾ÐºÑ€ÐµÐ¼Ð¾. Ð Ð¾Ð·Ñ€Ñ–Ð·, ÑÐºÐ¸Ð¹ Ð¿Ð¾ÐºÐ°Ð·ÑƒÑ” Ð»Ð¸ÑˆÐµ Ñ‚Ðµ, Ñ‰Ð¾ Ð´Ð°Ð»Ð¾ Ð´Ð¾ÑÑ‚ÑƒÐ¿,
 * Ð½Ðµ Ð¿Ð¾ÑÑÐ½ÑŽÑ”, Ñ‡Ð¾Ð¼Ñƒ Ð´Ð¾ÑÑ‚ÑƒÐ¿Ñƒ Ð½ÐµÐ¼Ð°Ñ”.
 */
const Denied: EffectiveAccessView = {
  userId: 7,
  userName: 'ivanov',
  resource: 'Registry:5',
  level: 'None',
  isDenied: true,
  denyReason: 'ExplicitDeny',
  groupsFromTicket: false,
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
  it('Ð½Ñ–Ñ‡Ð¾Ð³Ð¾ Ð½Ðµ Ð¿Ð¸Ñ‚Ð°Ñ” Ð² ÑÐµÑ€Ð²ÐµÑ€Ð°, Ð´Ð¾ÐºÐ¸ Ð½Ðµ Ð½Ð°Ñ‚Ð¸ÑÐ½ÑƒÑ‚Ð¾ Â«ÐŸÐ¾ÑÑÐ½Ð¸Ñ‚Ð¸Â», Ñ– Ð½Ðµ Ð´Ð¾Ð·Ð²Ð¾Ð»ÑÑ” Ð¿Ð¾Ñ€Ð¾Ð¶Ð½Ñ–Ð¹ Ñ€ÐµÑÑƒÑ€Ñ', () => {
    const fetchMock = stub(Denied);
    show();

    const button = screen.getByRole('button', { name: /effectiveAccess\.explain/ });
    expect(button).toHaveProperty('disabled', true);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('Ð¿Ð¸Ñ‚Ð°Ñ” Ñ€ÐµÑÑƒÑ€Ñ Ñƒ Ñ„Ð¾Ñ€Ð¼Ñ– Ñ‚Ð¸Ð¿:Ñ–Ð´ÐµÐ½Ñ‚Ð¸Ñ„Ñ–ÐºÐ°Ñ‚Ð¾Ñ€ Ñ– Ð¿Ð¾ÐºÐ°Ð·ÑƒÑ” Ð·Ð°Ð±Ð¾Ñ€Ð¾Ð½Ñƒ Ñ‚Ð° Ð²Ð½ÐµÑÐ¾Ðº, Ñ‰Ð¾ Ð½Ðµ Ð¿Ð¾Ñ€Ð°Ñ…ÑƒÐ²Ð°Ð²ÑÑ', async () => {
    const fetchMock = stub(Denied);
    show();

    fireEvent.change(screen.getByLabelText(/effectiveAccess\.resourceId/), { target: { value: '5' } });
    fireEvent.click(screen.getByRole('button', { name: /effectiveAccess\.explain/ }));

    await waitFor(() => expect(screen.getByText(/effectiveAccess\.level/)).toBeDefined());

    const url = String((fetchMock.mock.calls[0] as unknown[])[0]);
    expect(url).toContain('/api/v1/security/users/7/effective-access?resource=Registry%3A5');

    // Ð—Ð°Ð±Ð¾Ñ€Ð¾Ð½Ð° Ð½Ð°Ð·Ð²Ð°Ð½Ð° Ð¾ÐºÑ€ÐµÐ¼Ð¾ (role=alert) â€” Ð²Ð¸Ð³Ñ€Ð°Ñ” Ð½Ð°Ð´ ÑƒÑÑ–Ð¼.
    expect(screen.getByText(/effectiveAccess\.denied/)).toBeDefined();

    // Ð“Ñ€ÑƒÐ¿Ð¸ Ñ‡ÑƒÐ¶Ð¾Ð³Ð¾ Ð·Ð°Ð¿Ð¸ÑÑƒ Ð½ÐµÐ²Ñ–Ð´Ð¾Ð¼Ñ– â€” Ð¿ÐµÑ€ÐµÐ»Ñ–Ðº Ð½Ðµ Ð¼Ð¾Ð²Ñ‡Ð¸Ñ‚ÑŒ.
    expect(screen.getByText(/effectiveAccess\.groupsUnknown/)).toBeDefined();

    // Ð¢Ð°Ð±Ð»Ð¸Ñ†Ñ Ð²Ð½ÐµÑÐºÑ–Ð² Ð¼Ð°Ñ” Ð´Ð¾ÑÑ‚ÑƒÐ¿Ð½Ñƒ Ð½Ð°Ð·Ð²Ñƒ (WCAG 1.3.1): Ð±ÐµÐ· Ð½ÐµÑ— Ñ‡Ð¸Ñ‚Ð°Ñ‡ ÐµÐºÑ€Ð°Ð½Ð° ÐºÐ°Ð¶Ðµ Ð»Ð¸ÑˆÐµ Â«Ñ‚Ð°Ð±Ð»Ð¸Ñ†ÑÂ».
    expect(screen.getByRole('table', { name: '⟦effectiveAccess.title⟧' })).toBeDefined();

    // Ð£ÑÑ– Ñ‚Ñ€Ð¸ Ð²Ð½ÐµÑÐºÐ¸ Ð²Ð¸Ð´Ð½Ð¾, Ñƒ Ñ‚Ð¾Ð¼Ñƒ Ñ‡Ð¸ÑÐ»Ñ– Ñ‚Ð¾Ð¹, Ñ‰Ð¾ Ð½Ðµ Ð¿Ð¾Ñ€Ð°Ñ…ÑƒÐ²Ð°Ð²ÑÑ.
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

  it('Ð¿Ð¾Ñ€Ð¾Ð¶Ð½Ñ–Ð¹ Ð¿ÐµÑ€ÐµÐ»Ñ–Ðº Ð²Ð½ÐµÑÐºÑ–Ð² Ñ– Ñ€Ñ–Ð²ÐµÐ½ÑŒ Â«Ð½Ñ–Ñ‡Ð¾Ð³Ð¾ Ð½Ðµ Ð´Ð°Ñ”Â» Ð¿Ð¾ÑÑÐ½ÑŽÑŽÑ‚ÑŒÑÑ ÑÐ»Ð¾Ð²Ð°Ð¼Ð¸', async () => {
    stub({ ...Denied, isDenied: false, denyReason: 'NoGrant', groupsFromTicket: true, contributions: [] });
    show();

    fireEvent.change(screen.getByLabelText(/effectiveAccess\.resourceId/), { target: { value: '9' } });
    fireEvent.click(screen.getByRole('button', { name: /effectiveAccess\.explain/ }));

    await waitFor(() => expect(screen.getByText(/effectiveAccess\.noGrant/)).toBeDefined());
    expect(screen.getByText(/effectiveAccess\.noContributions/)).toBeDefined();
    expect(screen.queryByText(/effectiveAccess\.groupsUnknown/)).toBeNull();
  });
});
