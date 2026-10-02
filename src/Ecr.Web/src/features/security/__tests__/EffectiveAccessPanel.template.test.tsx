import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { EffectiveAccessView } from '@/api/types';
import { EffectiveAccessPanel } from '@/features/security/EffectiveAccessPanel';
import { testTheme } from '@/test/render';

/**
 * Розріз на аркуші, таблиці й колонці (`ФВ-6.16`): проєкт обов'язковий, застереження про стан документа
 * стоїть поруч із рівнем, внески несуть «успадковано від …».
 *
 * ⛔ Без застереження розріз без документа брехав би про те, що людина зможе зробити з коміркою зараз.
 */
const Column: EffectiveAccessView = {
  userId: 7,
  userName: 'ivanov',
  resource: 'Column:9',
  level: 'Write',
  isDenied: false,
  denyReason: null,
  groupsFromTicket: false,
  caveat: 'DocumentStateNotConsidered',
  projectId: 3,
  contributions: [
    { source: 'Grant', roleCode: 'Prj', principalSid: null, permissionCode: null, level: 'Read', isDeny: false, scope: 'Unscoped', counted: true, inheritedFrom: 'Project:3' },
    { source: 'Grant', roleCode: 'Col', principalSid: null, permissionCode: null, level: 'Write', isDeny: false, scope: 'Unscoped', counted: true, inheritedFrom: null },
  ],
};

function stub(body: unknown): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async () =>
    Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })),
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

describe('EffectiveAccessPanel: аркуш, таблиця, колонка', () => {
  it('просить проєкт, шле його в запиті й показує застереження та «успадковано від»', async () => {
    const fetchMock = stub(Column);
    show();

    expect(screen.queryByLabelText(/effectiveAccess\.projectId/)).toBeNull();
    fireEvent.change(screen.getByLabelText(/effectiveAccess\.kind⟧/), { target: { value: 'Column' } });
    fireEvent.change(screen.getByLabelText(/effectiveAccess\.resourceId/), { target: { value: '9' } });

    // Без проєкту питати не можна: id колонки спільний для проєктів шаблону.
    const button = screen.getByRole('button', { name: /effectiveAccess\.explain/ });
    expect(button).toHaveProperty('disabled', true);

    fireEvent.change(screen.getByLabelText(/effectiveAccess\.projectId/), { target: { value: '3' } });
    expect(button).toHaveProperty('disabled', false);
    fireEvent.click(button);

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalled();
    });
    const url = String((fetchMock.mock.calls[0] as unknown[])[0]);
    expect(url).toContain('resource=Column%3A9');
    expect(url).toContain('projectId=3');

    expect(await screen.findByTestId('effective-access-caveat')).toBeDefined();
    expect(screen.getAllByText(/effectiveAccess\.inheritedFrom/)).toHaveLength(1);
  });

  it('для довідника проєкт не питає, застереження немає', async () => {
    stub({ ...Column, resource: 'Registry:5', caveat: null, projectId: null, contributions: [] });
    show();

    expect(screen.queryByLabelText(/effectiveAccess\.projectId/)).toBeNull();
    fireEvent.change(screen.getByLabelText(/effectiveAccess\.resourceId/), { target: { value: '5' } });
    fireEvent.click(screen.getByRole('button', { name: /effectiveAccess\.explain/ }));

    await screen.findByText(/effectiveAccess\.level/);
    expect(screen.queryByTestId('effective-access-caveat')).toBeNull();
  });
});
