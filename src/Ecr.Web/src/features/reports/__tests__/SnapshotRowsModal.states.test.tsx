import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import SnapshotRowsModal from '@/features/reports/SnapshotRowsModal';
import { testTheme } from '@/test/render';

/**
 * Рядки зрізу (`D-52a`): стани «помилка», «порожньо», «завантаження»
 * (`ФВ-14.22`). Вікно відкривається саме заради цього запиту — рядки і є його
 * вмістом, тож відмова не може виглядати як «у зрізі немає рядків».
 */
configure({ asyncUtilTimeout: 10_000 });

const Rows = '/api/v1/reports/snapshots/7/rows';

function serve(rows: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes(Rows)) return rows();

      return new Response('null', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({
        title: status === 403 ? 'Forbidden' : 'Server error',
        status,
        errorCode,
        correlationId: 'corr-rows',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SnapshotRowsModal snapshotId={7} onClose={() => undefined} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SnapshotRowsModal — стани', () => {
  it('500: показує помилку з кодом, а не «рядків немає»', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦snapshots.rowsEmpty⟧')).toBeNull();
  });

  it('403: стан «немає права», а не «рядків немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText('⟦snapshots.rowsEmpty⟧')).toBeNull();
  });

  it('порожній зріз: пояснення «рядків немає», жодної таблиці й кнопки «ще»', async () => {
    serve(() =>
      Promise.resolve(
        new Response(JSON.stringify({ columns: [], rows: [], nextCursor: null, groups: null, totals: null }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );
    show();

    expect(await screen.findByText('⟦snapshots.rowsEmpty⟧')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.queryByRole('button', { name: '⟦snapshots.rowsMore⟧' })).toBeNull();
  });

  it('у дорозі: скелет «завантаження», а не «рядків немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('⟦snapshots.rowsEmpty⟧')).toBeNull();
  });
});
