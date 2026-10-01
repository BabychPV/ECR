import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RoleView } from '@/api/types';
import { GroupAssignmentsPanel } from '@/features/security/GroupAssignmentsPanel';
import { testTheme } from '@/test/render';

/**
 * Ролі груп каталогу: стани «немає права» і «завантаження» (`ФВ-14.22`).
 *
 * ⚠ 500 і порожній перелік уже стереже `GroupAssignmentsPanel.test.tsx` (X-14).
 * Тут — 403 і запит у дорозі: жоден не має виглядати як «групам нічого не
 * призначено» чи як голий заголовок таблиці.
 */
configure({ asyncUtilTimeout: 10_000 });

const roles: RoleView[] = [
  { id: 2, code: 'Publishers', isActive: true, isBuiltIn: false, permissions: [], dangerousPermissions: [] },
];

function serve(listAnswer: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/security/group-assignments')) return listAnswer();

      // Довідники області (проєкти, аркуші) — порожні: предмет тут лише перелік.
      return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <GroupAssignmentsPanel roles={roles} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const marker = (state: string): Element | null => document.querySelector(`[data-group-assignments="${state}"]`);

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('GroupAssignmentsPanel — стани', () => {
  it('403: відмова з кодом, а не «групам нічого не призначено»', async () => {
    serve(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({ title: 'Forbidden', status: 403, errorCode: 'ECR-AUTH-0403', correlationId: 'c', detail: null }),
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText('⟦groupRoles.empty⟧')).toBeNull();
    expect(marker('empty')).toBeNull();
    expect(marker('pending')).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('у дорозі: скелет, а не «групам нічого не призначено» і не порожня таблиця', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    await waitFor(() => expect(marker('pending')).toBeTruthy());
    expect(screen.queryByText('⟦groupRoles.empty⟧')).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
