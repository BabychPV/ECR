import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { testTheme } from '@/test/render';

/**
 * Перелік зрізів: стани «помилка», «порожньо», «завантаження» (`ФВ-14.22`).
 *
 * ⚠ Відмову переліку ОПИСІВ звітів перевіряє `SnapshotsPage.requestFailure`;
 * тут — сам перелік зрізів, основний вміст сторінки. Довідники віддаються
 * нормально, щоб чужий банер не підмішувався в твердження.
 */
configure({ asyncUtilTimeout: 10_000 });

const json = (body: unknown): Promise<Response> =>
  Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({
        title: status === 403 ? 'Forbidden' : 'Server error',
        status,
        errorCode,
        correlationId: 'corr-snapshots',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function serve(snapshots: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Report.BuildSnapshot', 'Report.EditDefinition'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }
      // ⚠ Зрізи — раніше за описи: адреса описів є префіксом адреси зрізів.
      if (url.includes('/api/v1/reports/snapshots')) return snapshots();
      if (url.includes('/api/v1/reports')) return json([]);
      if (url.includes('/api/v1/projects')) {
        return json({ items: [{ id: 42, code: 'KASH_2026', status: 'Active' }], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/snapshots']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <SnapshotsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SnapshotsPage — стани переліку зрізів', () => {
  it('500: показує помилку з кодом, а не «зрізів немає»', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦snapshots.empty⟧')).toBeNull();
  });

  it('403: стан «немає права», а не «зрізів немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText('⟦snapshots.empty⟧')).toBeNull();
  });

  it('порожній перелік: пояснення «зрізів немає» і жодної таблиці чи помилки', async () => {
    serve(() => json([]));
    show();

    expect(await screen.findByText('⟦snapshots.empty⟧')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('у дорозі: скелет «завантаження», а не «зрізів немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('⟦snapshots.empty⟧')).toBeNull();
  });
});
